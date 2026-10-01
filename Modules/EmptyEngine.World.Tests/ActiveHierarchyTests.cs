using EmptyEngine.Modules.Testing;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmptyEngine.Core;
using EmptyEngine.Editor;
using EmptyEngine.ObjectModel;
using EmptyEngine.SceneSource.Editor;
using EmptyEngine.Storage;
using EmptyEngine.Tests;
using EmptyEngine.World.Editor;
using Xunit;

namespace EmptyEngine.World.Tests;

public sealed class ActiveHierarchyTests
{
    [Fact]
    public void Authoring_blob_preserves_active()
    {
        var serializer = new HierarchyBlobSerializer(CatalogStub.Schemas);
        var root = new HierarchyNode("root", "Root",
            [new HierarchyNode("child", "Child", active: false)]);

        HierarchyNode restored = Assert.Single(serializer.Deserialize(serializer.Serialize([root])));

        Assert.True(restored.Active);
        Assert.False(Assert.Single(restored.Children).Active);
    }

    [Fact]
    public void Scene_source_preserves_active()
    {
        var root = new HierarchyNode("root", "Root", active: false);

        HierarchyNode restored = SceneJsonCodec.FromJson(SceneJsonCodec.ToJson(root), CatalogStub.Schemas);

        Assert.False(restored.Active);
        Assert.True(SceneJsonCodec.FromJson("""{ "Id": "old", "Name": "Old" }""", CatalogStub.Schemas).Active);
    }

    [Fact]
    public void Prefab_variant_preserves_active_overrides()
    {
        var original = new HierarchyNode("root", "Root",
            [new HierarchyNode("child", "Child")]);
        var variant = new HierarchyNode("root", "Root",
            [new HierarchyNode("child", "Child", active: false)]);

        IReadOnlyList<PrefabVariantCodec.FieldOverride> overrides = PrefabVariantCodec.Diff(original, variant);
        HierarchyNode merged = PrefabVariantCodec.Merge(original, CatalogStub.Schemas, overrides);

        Assert.False(Assert.Single(merged.Children).Active);
    }

    [Fact]
    public void Prefab_variant_preserves_name_added_components_and_added_children()
    {
        var original = new HierarchyNode("root", "Enemy");
        var source = new PrefabVariantCodec.VariantData(
            "enemy-guid",
            [new PrefabVariantCodec.FieldOverride(
                "root", -1, typeof(HierarchyNode).FullName!, "Name", JsonValue.Create("RenamedEnemy"))],
            [new PrefabVariantCodec.ComponentAddition(
                "root", typeof(TestHealth).FullName!, new JsonObject())],
            [new PrefabVariantCodec.ChildAddition(
                "root", new HierarchyNode("added", "Quad (1)",
                    [new HierarchyNode("grandchild", "Nested")]))]);

        PrefabVariantCodec.VariantData restored = PrefabVariantCodec.FromJson(PrefabVariantCodec.ToJson(source), CatalogStub.Schemas);
        HierarchyNode merged = PrefabVariantCodec.Merge(
            original, CatalogStub.Schemas, restored.Overrides, restored.AddedComponents, restored.AddedChildren);

        Assert.Equal("RenamedEnemy", merged.Name);
        Assert.Equal(typeof(TestHealth).FullName!, Assert.Single(merged.Components).TypeName);
        HierarchyNode added = Assert.Single(merged.Children);
        Assert.Equal("Quad (1)", added.Name);
        Assert.Equal("Nested", Assert.Single(added.Children).Name);
        Assert.Single(PrefabVariantCodec.DiffAddedComponents(original, merged));
        PrefabVariantCodec.ChildAddition childAddition =
            Assert.Single(PrefabVariantCodec.DiffAddedChildren(original, merged));
        Assert.Equal("root", childAddition.ParentObjectId);
        Assert.Equal("added", childAddition.Child.ObjectId);
    }

    [Fact]
    public void Nested_prefab_leaf_preserves_added_components_and_children()
    {
        var source = new NestedPrefabCodec.PrefabLeaf(
            "leaf",
            "enemy-guid",
            [],
            [new PrefabVariantCodec.ComponentAddition(
                "leaf:root", typeof(TestHealth).FullName!, new JsonObject())],
            [new PrefabVariantCodec.ChildAddition(
                "leaf:root", new HierarchyNode("added", "Quad (1)", active: false))]);
        using JsonDocument json = JsonDocument.Parse(NestedPrefabCodec.WriteLeaf(source).ToJsonString());

        NestedPrefabCodec.PrefabLeaf restored = NestedPrefabCodec.ReadLeaf(json.RootElement, CatalogStub.Schemas);

        Assert.Equal(typeof(TestHealth).FullName!, Assert.Single(restored.AddedComponents).TypeName);
        PrefabVariantCodec.ChildAddition child = Assert.Single(restored.AddedChildren);
        Assert.Equal("leaf:root", child.ParentObjectId);
        Assert.Equal("Quad (1)", child.Child.Name);
        Assert.False(child.Child.Active);
    }

    [Fact]
    public void Editor_checkbox_change_preserves_child_active_self_and_is_published()
    {
        var vm = EditorFixture.NewEditor();
        vm.UpdateHierarchy([
            new HierarchyNode("root", "Root", [new HierarchyNode("child", "Child")]),
        ]);
        IReadOnlyList<HierarchyNode>? published = null;
        vm.ScenePublished += p => published = p.Roots;

        vm.SetActive(vm.RootNodes[0], false);

        Assert.False(vm.RootNodes[0].Active);
        Assert.True(vm.RootNodes[0].Children[0].Active);
        Assert.False(Assert.Single(published!).Active);
        Assert.True(Assert.Single(Assert.Single(published!).Children).Active);
        Assert.True(vm.RootNodes[0].IsDirty);
    }

    /// <summary>プレイ中の有効状態の変更を保存対象とせず、停止時に元へ戻すことを検証する</summary>
    [Fact]
    public void Active_is_editable_while_playing_and_rolls_back_on_stop()
    {
        var vm = EditorFixture.NewEditor();
        vm.UpdateHierarchy([
            new HierarchyNode("root", "Root", [new HierarchyNode("child", "Child")]),
        ]);
        vm.SetPlayMode(true);

        IReadOnlyList<HierarchyNode>? published = null;
        vm.ScenePublished += p => published = p.Roots;

        vm.SetActive(vm.RootNodes[0].Children[0], false);

        Assert.False(vm.RootNodes[0].Children[0].Active);
        Assert.False(Assert.Single(Assert.Single(published!).Children).Active);
        Assert.False(vm.RootNodes[0].IsDirty);

        vm.SetPlayMode(false);

        Assert.True(vm.RootNodes[0].Children[0].Active);
    }

    [Fact]
    public void Runtime_blob_activation_uses_set_active_lifecycle_semantics()
    {
        ActiveHierarchyProbe.Events.Clear();
        var world = new SceneWorld();

        world.DeserializeSceneNow(Blob(active: false), isPlaying: true);
        Assert.Empty(ActiveHierarchyProbe.Events);

        world.DeserializeSceneNow(Blob(active: true), isPlaying: true);
        Assert.Equal(["deserialized", "created"], ActiveHierarchyProbe.Events);

        ActiveHierarchyProbe.Events.Clear();
        world.DeserializeSceneNow(Blob(active: false), isPlaying: true);
        Assert.Equal(["destroyed"], ActiveHierarchyProbe.Events);
    }

    [Fact]
    public void Set_active_state_preserves_and_serializes_child_active_self()
    {
        var world = new SceneWorld();
        world.DeserializeSceneNow(SceneBlob.Encode([
            new RootInstanceData("scene", new ObjectData("root", "Root", [],
                [new ObjectData("child", "Child", [], [])])),
        ]), isPlaying: false);

        Assert.Single(world.Roots).SetActive(false);

        ObjectData saved = Assert.Single(SceneBlob.Decode(world.SerializeScene())).Root;
        Assert.False(saved.Active);
        Assert.True(Assert.Single(saved.Children).Active);
    }

    [Fact]
    public void Reactivating_parent_does_not_activate_an_initially_inactive_child()
    {
        ActiveHierarchyProbe.Events.Clear();
        var world = new SceneWorld();
        world.DeserializeSceneNow(SceneBlob.Encode([
            new RootInstanceData("scene", new ObjectData("root", "Root", [],
                [new ObjectData("child", "Child",
                    [new ComponentData(typeof(ActiveHierarchyProbe).FullName!, new ActiveHierarchyProbe())],
                    [], Active: false)])),
        ]), isPlaying: true);
        IObject root = Assert.Single(world.Roots);

        root.SetActive(false);
        root.SetActive(true);

        Assert.Empty(ActiveHierarchyProbe.Events);
        ObjectData saved = Assert.Single(SceneBlob.Decode(world.SerializeScene())).Root;
        Assert.True(saved.Active);
        Assert.False(Assert.Single(saved.Children).Active);
    }

    [Fact]
    public void Activating_child_under_inactive_parent_does_not_invoke_lifecycle()
    {
        ActiveHierarchyProbe.Events.Clear();
        var world = new SceneWorld();
        world.DeserializeSceneNow(SceneBlob.Encode([
            new RootInstanceData("scene", new ObjectData("root", "Root", [],
                [new ObjectData("child", "Child",
                    [new ComponentData(typeof(ActiveHierarchyProbe).FullName!, new ActiveHierarchyProbe())],
                    [], Active: false)], Active: false)),
        ]), isPlaying: true);
        IObject root = Assert.Single(world.Roots);
        IObject child = Assert.Single(root.GetChildren());

        child.SetActive(true);
        Assert.Empty(ActiveHierarchyProbe.Events);

        root.SetActive(true);
        Assert.Equal(["deserialized", "created"], ActiveHierarchyProbe.Events);
    }

    [Fact]
    public void Reconcile_uses_parent_active_state_for_child_lifecycle()
    {
        static byte[] ParentBlob(bool parentActive) => SceneBlob.Encode([
            new RootInstanceData("scene", new ObjectData("root", "Root", [],
                [new ObjectData("child", "Child",
                    [new ComponentData(typeof(ActiveHierarchyProbe).FullName!, new ActiveHierarchyProbe())],
                    [], Active: true)], parentActive)),
        ]);

        ActiveHierarchyProbe.Events.Clear();
        var world = new SceneWorld();
        world.DeserializeSceneNow(ParentBlob(parentActive: false), isPlaying: true);
        Assert.Empty(ActiveHierarchyProbe.Events);

        world.DeserializeSceneNow(ParentBlob(parentActive: true), isPlaying: true);
        Assert.Equal(["deserialized", "created"], ActiveHierarchyProbe.Events);

        ActiveHierarchyProbe.Events.Clear();
        world.DeserializeSceneNow(ParentBlob(parentActive: false), isPlaying: true);
        Assert.Equal(["destroyed"], ActiveHierarchyProbe.Events);
    }

    [Fact]
    public void Instantiate_under_inactive_parent_defers_child_lifecycle()
    {
        ActiveHierarchyProbe.Events.Clear();
        var world = new SceneWorld();
        world.DeserializeSceneNow(SceneBlob.Encode([
            new RootInstanceData("scene", new ObjectData("root", "Root", [], [], Active: false)),
        ]), isPlaying: true);
        var template = new GameObject("child", "Child", [new ActiveHierarchyProbe()]);

        world.Instantiate(template, Assert.Single(world.Roots));
        Assert.Empty(ActiveHierarchyProbe.Events);

        Assert.Single(world.Roots).SetActive(true);
        Assert.Equal(["deserialized", "created"], ActiveHierarchyProbe.Events);
    }

    [Fact]
    public void Inactive_prefab_template_stays_inactive_when_instantiated()
    {
        ActiveHierarchyProbe.Events.Clear();
        using var store = AssetStorage.InMemory();
        store.Add("inactive.scene", Blob(active: false));
        using var resolver = WorldAssetResolver.FromStore(store);
        var world = new SceneWorld(resolver);

        Assert.True(resolver.TryLoad<IObject>(
            new AssetReference<IObject>("inactive.scene"), out IObject? template));
        IObject spawned = world.Instantiate(template!);

        Assert.Empty(ActiveHierarchyProbe.Events);
        Assert.False(Assert.Single(SceneBlob.Decode(world.SerializeScene())).Root.Active);
    }

    private static byte[] Blob(bool active) => SceneBlob.Encode([
        new RootInstanceData("scene", new ObjectData("root", "Root",
            [new ComponentData(typeof(ActiveHierarchyProbe).FullName!, new ActiveHierarchyProbe())], [], active)),
    ]);
}

public sealed class ActiveHierarchyProbe : ILifecycleAttachable
{
    public static readonly List<string> Events = [];

    public void OnCreated(IObject owner) => Events.Add("created");

    public void OnDeserialized(IObject owner) => Events.Add("deserialized");

    public void OnDestroy(IObject owner) => Events.Add("destroyed");
}
