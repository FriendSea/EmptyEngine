using System.Numerics;
using System.Runtime.CompilerServices;
using EmptyEngine.Core;
using EmptyEngine.ObjectModel;
using EmptyEngine.Storage;
using EmptyEngine.Tests;
using Xunit;

namespace EmptyEngine.World.Tests;

/// <summary>世界の公開 API の契約検証</summary>
public sealed class SceneWorldTests
{
    private static byte[] SceneWithProbe(int value, string objId, string sceneId = "scene-1")
        => SceneBlob.Encode([new RootInstanceData(sceneId,
            new ObjectData(objId, "O",
                [new ComponentData(typeof(SpawnProbe).FullName!, new SpawnProbe { Value = value })],
                []))]);

    private static GameObject ProbeTemplate(int value)
        => new("template", "Spawned", [new SpawnProbe { Value = value }]);

    [Fact]
    public void Instantiate_adds_a_new_scene_instance_and_fires_deserialized_then_created()
    {
        SpawnProbe.Log.Clear();
        var world = new SceneWorld();
        Assert.Empty(SceneBlob.Decode(world.SerializeScene()));

        GameObject source = ProbeTemplate(7);
        IObject spawned = world.Instantiate(source);

        Assert.NotNull(spawned.GetAttachable<SpawnProbe>());
        Assert.NotSame(source, spawned);
        Assert.NotSame(source.GetAttachable<SpawnProbe>(), spawned.GetAttachable<SpawnProbe>());

        Assert.Collection(SpawnProbe.Log,
            e => { Assert.Equal("deserialized", e.Kind); Assert.Equal(7, e.Value); },
            e => Assert.Equal("created", e.Kind));

        IReadOnlyList<RootInstanceData> roots = SceneBlob.Decode(world.SerializeScene());
        RootInstanceData scene = Assert.Single(roots);
        Assert.Equal(7, ((SpawnProbe)scene.Root.Components[0].Component!).Value);
    }

    [Fact]
    public void Repeated_destroy_removes_the_last_object_once_and_disables_later_operations()
    {
        SpawnProbe.Log.Clear();
        using var world = new SceneWorld();
        IObject spawned = world.Instantiate(ProbeTemplate(3));
        SpawnProbe.Log.Clear();
        spawned.Destroy();
        spawned.Destroy();
        world.FlushPendingDestructions();
        Assert.Collection(SpawnProbe.Log, e =>
        {
            Assert.Equal("destroyed", e.Kind);
            Assert.Equal(3, e.Value);
        });
        Assert.Empty(SceneBlob.Decode(world.SerializeScene()));

        SpawnProbe.Log.Clear();
        spawned.SetActive(false);
        spawned.Destroy();
        world.FlushPendingDestructions();
        Assert.Empty(SpawnProbe.Log);
    }

    [Fact]
    public void Destroy_can_be_requested_while_children_are_being_enumerated()
    {
        SpawnProbe.Log.Clear();
        var world = new SceneWorld();
        var source = new GameObject("root", "Root", [],
        [
            new GameObject("child-a", "A", [new SpawnProbe { Value = 1 }]),
            new GameObject("child-b", "B", [new SpawnProbe { Value = 2 }]),
        ]);
        IObject root = world.Instantiate(source);
        SpawnProbe.Log.Clear();

        var enumerated = new List<IObject>();
        foreach (IObject child in root.GetChildren())
        {
            enumerated.Add(child);
            child.Destroy();
        }

        Assert.Equal(2, enumerated.Count);
        Assert.Equal(2, root.GetChildren().Count);
        Assert.Empty(SpawnProbe.Log);

        world.FlushPendingDestructions();

        Assert.Empty(root.GetChildren());
        Assert.Equal(2, SpawnProbe.Log.Count(e => e.Kind == "destroyed"));
    }

    [Fact]
    public void Instances_from_same_source_are_independent()
    {
        var world = new SceneWorld();
        GameObject source = ProbeTemplate(5);

        var a = world.Instantiate(source).GetAttachable<SpawnProbe>()!;
        var b = world.Instantiate(source).GetAttachable<SpawnProbe>()!;

        Assert.NotSame(a, b);
        a.Value = 99;
        Assert.Equal(5, b.Value);
    }

    [Fact]
    public void Template_resolves_without_lifecycle_and_instantiates_with_lifecycle()
    {
        SpawnProbe.Log.Clear();
        var store = AssetStorage.InMemory();
        store.Add("bullet.scene", SceneWithProbe(7, "src", "prefab-scene"));
        var resolver = WorldAssetResolver.FromStore(store);

        bool ok = resolver.TryLoad<IObject>(new AssetReference<IObject>("bullet.scene"), out IObject? template);
        Assert.True(ok);
        Assert.NotNull(template);
        Assert.Empty(SpawnProbe.Log);

        var world = new SceneWorld(resolver);
        IObject spawned = world.Instantiate(template!);

        Assert.Equal(7, spawned.GetAttachable<SpawnProbe>()!.Value);
        Assert.Collection(SpawnProbe.Log,
            e => Assert.Equal("deserialized", e.Kind),
            e => Assert.Equal("created", e.Kind));
    }

    private static byte[] PrefabWithChildren(int a, int b)
        => SceneBlob.Encode([new RootInstanceData("prefab-scene",
            new ObjectData("root", "Title", [],
            [
                new ObjectData("root-a", "A", [new ComponentData(typeof(SpawnProbe).FullName!, new SpawnProbe { Value = a })], []),
                new ObjectData("root-b", "B", [new ComponentData(typeof(SpawnProbe).FullName!, new SpawnProbe { Value = b })], []),
            ]))]);

    [Fact]
    public void Instantiate_prefab_spawns_whole_tree_as_one_instance()
    {
        SpawnProbe.Log.Clear();
        var store = AssetStorage.InMemory();
        store.Add("title.scene", PrefabWithChildren(1, 2));
        var resolver = WorldAssetResolver.FromStore(store);
        var world = new SceneWorld(resolver);

        Assert.True(resolver.TryLoad<IObject>(new AssetReference<IObject>("title.scene"), out IObject? template));
        IObject root = world.Instantiate(template!);

        IReadOnlyList<RootInstanceData> roots = SceneBlob.Decode(world.SerializeScene());
        RootInstanceData scene = Assert.Single(roots);
        Assert.Equal(2, scene.Root.Children.Count);
        Assert.Equal([1, 2], scene.Root.Children.Select(o => ((SpawnProbe)o.Components[0].Component!).Value).OrderBy(v => v));

        Assert.Equal(2, SpawnProbe.Log.Count(e => e.Kind == "deserialized"));
        SpawnProbe.Log.Clear();
        root.Destroy();
        world.FlushPendingDestructions();
        Assert.Empty(SceneBlob.Decode(world.SerializeScene()));
        Assert.Equal(new[] { 1, 2 }, SpawnProbe.Log.Where(e => e.Kind == "destroyed").Select(e => e.Value).Order());
    }

    [Fact]
    public void Instantiate_with_parent_adds_under_parent_in_same_instance()
    {
        SpawnProbe.Log.Clear();
        var world = new SceneWorld();

        IObject parent = world.Instantiate(ProbeTemplate(1));
        SpawnProbe.Log.Clear();
        IObject child = world.Instantiate(ProbeTemplate(2), parent);

        RootInstanceData scene = Assert.Single(SceneBlob.Decode(world.SerializeScene()));
        ObjectData childData = Assert.Single(scene.Root.Children);
        Assert.Equal(2, ((SpawnProbe)childData.Components[0].Component!).Value);
        Assert.Collection(SpawnProbe.Log,
            e => { Assert.Equal("deserialized", e.Kind); Assert.Equal(2, e.Value); },
            e => { Assert.Equal("created", e.Kind); Assert.Equal(2, e.Value); });
        Assert.Same(child, parent.FindObject(((GameObject)child).Id));
    }

    [Fact]
    public void Instantiate_with_position_places_the_instance_in_world_space()
    {
        var world = new SceneWorld();
        IObject parent = world.Instantiate(new GameObject("parent", "Parent",
            [new TransformComponent { Position = new Vector3(10f, 0f, 0f) }]));

        GameObject Template() => new("template", "Spawned",
            [new TransformComponent { Position = new Vector3(1f, 1f, 1f) }]);

        TransformComponent placed = world
            .Instantiate(Template(), parent, new Vector3(12f, 3f, 0f))
            .GetAttachable<TransformComponent>()!;

        Assert.Equal(new Vector3(12f, 3f, 0f), placed.WorldPosition);
        Assert.Equal(new Vector3(2f, 3f, 0f), placed.Position);

        TransformComponent kept = world
            .Instantiate(Template(), parent)
            .GetAttachable<TransformComponent>()!;

        Assert.Equal(new Vector3(1f, 1f, 1f), kept.Position);
    }

    [Fact]
    public void Instantiate_with_position_places_before_lifecycle_callbacks()
    {
        PlacementProbe.Log.Clear();
        var world = new SceneWorld();
        IObject parent = world.Instantiate(new GameObject("parent", "Parent",
            [new TransformComponent { Position = new Vector3(10f, 0f, 0f) }]));

        world.Instantiate(
            new GameObject("template", "Spawned", [new TransformComponent(), new PlacementProbe()]),
            parent,
            new Vector3(12f, 3f, 0f));

        Assert.Equal(
            [("deserialized", new Vector3(12f, 3f, 0f)), ("created", new Vector3(12f, 3f, 0f))],
            PlacementProbe.Log);
    }

    [Fact]
    public void Root_object_components_are_realized_with_lifecycle()
    {
        SpawnProbe.Log.Clear();
        var world = new SceneWorld();

        world.DeserializeSceneNow(SceneBlob.Encode([new RootInstanceData("inst",
            new ObjectData("root", "Root",
                [new ComponentData(typeof(SpawnProbe).FullName!, new SpawnProbe { Value = 42 })],
                [
                    new ObjectData("child", "Child",
                        [new ComponentData(typeof(SpawnProbe).FullName!, new SpawnProbe { Value = 7 })],
                        []),
                ]))]), isPlaying: true);

        Assert.Equal(2, SpawnProbe.Log.Count(e => e.Kind == "created"));
        Assert.Contains(SpawnProbe.Log, e => e.Kind == "deserialized" && e.Value == 42);
        RootInstanceData scene = Assert.Single(SceneBlob.Decode(world.SerializeScene()));
        Assert.Equal(42, ((SpawnProbe)scene.Root.Components[0].Component!).Value);
    }

    [Fact]
    public void Switching_back_to_edit_mode_drops_spawned_objects()
    {
        SpawnProbe.Log.Clear();
        var world = new SceneWorld();
        world.DeserializeSceneNow(SceneWithProbe(1, "keep"), isPlaying: true);
        world.Instantiate(ProbeTemplate(7));

        Assert.Equal(2, SceneBlob.Decode(world.SerializeScene()).Count);

        world.DeserializeSceneNow(SceneWithProbe(1, "keep"), isPlaying: false);

        IReadOnlyList<RootInstanceData> roots = SceneBlob.Decode(world.SerializeScene());
        RootInstanceData remaining = Assert.Single(roots);
        Assert.Equal(1, ((SpawnProbe)remaining.Root.Components[0].Component!).Value);
        Assert.Contains(SpawnProbe.Log, e => e.Kind == "destroyed" && e.Value == 7);
    }

    [Fact]
    public void Destroy_on_an_object_outside_any_world_is_ignored()
    {
        var world = new SceneWorld();
        GameObject template = ProbeTemplate(9);

        template.Destroy();
        world.FlushPendingDestructions();

        Assert.Equal(9, world.Instantiate(template).GetAttachable<SpawnProbe>()!.Value);
    }

    [Fact]
    public void Parent_is_detached_before_OnDestroy_when_the_whole_tree_goes()
    {
        LivenessProbe.Log.Clear();
        var world = new SceneWorld();
        IObject root = world.Instantiate(new GameObject("root", "Root", [],
            [new GameObject("child", "Child", [new LivenessProbe()])]));

        root.Destroy();
        world.FlushPendingDestructions();

        Assert.Equal([false], LivenessProbe.Log);
    }

    [Fact]
    public void Parent_is_still_alive_in_OnDestroy_when_only_a_child_goes()
    {
        var world = new SceneWorld();
        IObject root = world.Instantiate(new GameObject("root", "Root", [],
            [new GameObject("child", "Child", [new LivenessProbe()])]));

        LivenessProbe.Log.Clear();
        Assert.Single(root.GetChildren()).Destroy();
        world.FlushPendingDestructions();

        Assert.Equal([true], LivenessProbe.Log);
    }

    [Fact]
    public void Reload_is_rejected_for_a_child_object()
    {
        var world = new SceneWorld();
        IObject root = world.Instantiate(new GameObject("root", "Root", [],
            [new GameObject("child", "Child", [new SpawnProbe { Value = 1 }])]));

        IObject child = Assert.Single(root.GetChildren());

        Assert.Throws<InvalidOperationException>(() => child.Reload());
    }
}

/// <summary>テスト用ライフサイクルコンポーネント（破棄時点の親の生死の観測）</summary>
public sealed class LivenessProbe : ILifecycleAttachable
{
    public static readonly List<bool> Log = [];

    public void OnCreated(IObject owner)
    {
    }

    public void OnDeserialized(IObject owner)
    {
    }

    public void OnDestroy(IObject owner) => Log.Add(owner.Parent?.IsAlive() ?? false);
}

/// <summary>テスト用ライフサイクルコンポーネント（コールバック時点のワールド位置の観測）</summary>
public sealed class PlacementProbe : ILifecycleAttachable
{
    public static readonly List<(string Kind, Vector3 Position)> Log = [];

    public void OnCreated(IObject owner) => Record("created", owner);

    public void OnDeserialized(IObject owner) => Record("deserialized", owner);

    public void OnDestroy(IObject owner) => Record("destroyed", owner);

    private static void Record(string kind, IObject owner)
        => Log.Add((kind, TransformComponent.GetWorldMatrix(owner).Translation));
}

/// <summary>テスト用ライフサイクルコンポーネント（生成/破棄の観測）</summary>
public sealed class SpawnProbe : ILifecycleAttachable
{
    public readonly record struct Event(string Kind, int Instance, int Value);

    public static readonly List<Event> Log = [];

    public int Value { get; set; }

    public void OnCreated(IObject owner)
        => Log.Add(new Event("created", RuntimeHelpers.GetHashCode(this), Value));

    public void OnDeserialized(IObject owner)
        => Log.Add(new Event("deserialized", RuntimeHelpers.GetHashCode(this), Value));

    public void OnDestroy(IObject owner)
        => Log.Add(new Event("destroyed", RuntimeHelpers.GetHashCode(this), Value));
}
