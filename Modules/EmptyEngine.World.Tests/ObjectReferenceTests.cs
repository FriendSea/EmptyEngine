using EmptyEngine.Modules.Testing;
using EmptyEngine.Core;
using EmptyEngine.ObjectModel;
using EmptyEngine.Storage;
using EmptyEngine.Tests;
using Xunit;

namespace EmptyEngine.World.Tests;

/// <summary><see cref="ObjectReference"/> のシーン内解決の契約検証</summary>
public sealed class ObjectReferenceTests
{
    private static byte[] TwoObjectsInOneScene(string aTarget)
        => SceneBlob.Encode([new RootInstanceData("inst",
            new ObjectData("root", "S", [],
            [
                new ObjectData("a", "A",
                    [new ComponentData(typeof(ReferenceProbe).FullName!, new ReferenceProbe { Target = ObjectReference.FromId(aTarget) })],
                    []),
                new ObjectData("b", "B",
                    [new ComponentData(typeof(ReferenceProbe).FullName!, new ReferenceProbe())],
                    []),
            ]))]);

    [Fact]
    public void Resolves_sibling_object_within_the_same_scene()
    {
        ReferenceProbe.Log.Clear();
        var world = new SceneWorld();

        world.DeserializeSceneNow(TwoObjectsInOneScene("b"), isPlaying: true);

        (IObject Owner, IObject? Resolved) a = ReferenceProbe.Log.Single(e => Id(e.Owner) == "a");
        (IObject Owner, IObject? Resolved) b = ReferenceProbe.Log.Single(e => Id(e.Owner) == "b");

        Assert.NotNull(a.Resolved);
        Assert.Equal("b", Id(a.Resolved!));
        Assert.Same(b.Owner, a.Resolved);
        ObjectData saved = Assert.Single(SceneBlob.Decode(world.SerializeScene())).Root.Children.Single(o => o.Id == "a");
        var probe = Assert.IsType<ReferenceProbe>(saved.Components[0].Component);
        Assert.Equal("b", probe.Target.TargetId);
        Assert.Null(probe.Resolved);
    }

    [Fact]
    public void Unresolvable_id_yields_null()
    {
        ReferenceProbe.Log.Clear();
        var world = new SceneWorld();

        world.DeserializeSceneNow(TwoObjectsInOneScene("does-not-exist"), isPlaying: true);

        (IObject Owner, IObject? Resolved) a = ReferenceProbe.Log.Single(e => Id(e.Owner) == "a");
        Assert.Null(a.Resolved);
    }

    [Fact]
    public void Reference_does_not_resolve_across_scene_instances()
    {
        ReferenceProbe.Log.Clear();
        var world = new SceneWorld();

        byte[] blob = SceneBlob.Encode(
        [
            new RootInstanceData("s1",
                new ObjectData("a", "A",
                    [new ComponentData(typeof(ReferenceProbe).FullName!, new ReferenceProbe { Target = ObjectReference.FromId("b") })],
                    [])),
            new RootInstanceData("s2",
                new ObjectData("b", "B",
                    [new ComponentData(typeof(ReferenceProbe).FullName!, new ReferenceProbe())],
                    [])),
        ]);
        world.DeserializeSceneNow(blob, isPlaying: true);

        (IObject Owner, IObject? Resolved) a = ReferenceProbe.Log.Single(e => Id(e.Owner) == "a");
        Assert.Null(a.Resolved);
    }

    private static byte[] PrefabWithInternalRef()
        => SceneBlob.Encode([new RootInstanceData("prefab-scene",
            new ObjectData("root-a", "A",
                [new ComponentData(typeof(ReferenceProbe).FullName!, new ReferenceProbe { Target = ObjectReference.FromId("root-b") })],
                [
                    new ObjectData("root-b", "B",
                        [new ComponentData(typeof(ReferenceProbe).FullName!, new ReferenceProbe())],
                        []),
                ]))]);

    private static byte[] PrefabWithChildToRootRef()
        => SceneBlob.Encode([new RootInstanceData("prefab-scene",
            new ObjectData("r", "R",
                [new ComponentData(typeof(ReferenceProbe).FullName!, new ReferenceProbe())],
                [
                    new ObjectData("c", "C",
                        [new ComponentData(typeof(ReferenceProbe).FullName!, new ReferenceProbe { Target = ObjectReference.FromId("r") })],
                        []),
                ]))]);

    [Fact]
    public void Child_reference_to_root_survives_instantiate()
    {
        var store = AssetStorage.InMemory();
        store.Add("prefab.scene", PrefabWithChildToRootRef());
        var resolver = WorldAssetResolver.FromStore(store);
        var world = new SceneWorld(resolver);
        Assert.True(resolver.TryLoad<IObject>(new AssetReference<IObject>("prefab.scene"), out IObject? template));

        IObject root = world.Instantiate(template!);

        IObject? child = root.FindObject("c");
        Assert.NotNull(child);
        Assert.Same(root, child!.GetAttachable<ReferenceProbe>()!.Resolved);
    }

    [Fact]
    public void Instances_resolve_to_their_own_copy_not_each_other()
    {
        var store = AssetStorage.InMemory();
        store.Add("prefab.scene", PrefabWithInternalRef());
        var resolver = WorldAssetResolver.FromStore(store);
        var world = new SceneWorld(resolver);
        Assert.True(resolver.TryLoad<IObject>(new AssetReference<IObject>("prefab.scene"), out IObject? template));

        IObject first = world.Instantiate(template!);
        IObject second = world.Instantiate(template!);

        IObject? firstResolved = first.GetAttachable<ReferenceProbe>()!.Resolved;
        IObject? secondResolved = second.GetAttachable<ReferenceProbe>()!.Resolved;

        Assert.Same(first.FindObject("root-b"), firstResolved);
        Assert.Same(second.FindObject("root-b"), secondResolved);
        Assert.NotSame(firstResolved, secondResolved);
    }

    [Fact]
    public void Reference_array_round_trips_and_each_element_resolves()
    {
        MultiReferenceProbe.Log.Clear();
        var world = new SceneWorld();

        byte[] blob = SceneBlob.Encode([new RootInstanceData("inst",
            new ObjectData("root", "S", [],
            [
                new ObjectData("a", "A",
                    [new ComponentData(typeof(MultiReferenceProbe).FullName!, new MultiReferenceProbe
                    {
                        Targets = [ObjectReference.FromId("b"), ObjectReference.FromId("c")],
                    })],
                    []),
                new ObjectData("b", "B", [], []),
                new ObjectData("c", "C", [], []),
            ]))]);

        world.DeserializeSceneNow(blob, isPlaying: true);

        MultiReferenceProbe probe = MultiReferenceProbe.Log.Single();
        Assert.Equal(["b", "c"], probe.Targets.Select(t => t.TargetId));
        Assert.Equal(["b", "c"], probe.Resolved.Select(r => Id(r!)));
    }

    [Fact]
    public void Typed_reference_resolves_straight_to_the_declared_component()
    {
        TypedReferenceProbe.Log.Clear();
        var world = new SceneWorld();

        world.DeserializeSceneNow(TypedScene("b"), isPlaying: true);

        TypedReferenceProbe probe = TypedReferenceProbe.Log.Single();
        Assert.NotNull(probe.Resolved);
        Assert.Equal("marker-b", probe.Resolved!.Tag);
        ObjectData saved = Assert.Single(SceneBlob.Decode(world.SerializeScene())).Root.Children.Single(o => o.Id == "a");
        var restored = Assert.IsType<TypedReferenceProbe>(saved.Components[0].Component);
        Assert.Equal("b", restored.Target.TargetId);
        Assert.Null(restored.Resolved);
    }

    [Fact]
    public void Typed_reference_yields_null_when_target_lacks_the_component()
    {
        TypedReferenceProbe.Log.Clear();
        var world = new SceneWorld();

        world.DeserializeSceneNow(TypedScene("c"), isPlaying: true);

        Assert.Null(TypedReferenceProbe.Log.Single().Resolved);
    }

    private static byte[] TypedScene(string aTarget)
        => SceneBlob.Encode([new RootInstanceData("inst",
            new ObjectData("root", "S", [],
            [
                new ObjectData("a", "A",
                    [new ComponentData(typeof(TypedReferenceProbe).FullName!, new TypedReferenceProbe
                    {
                        Target = ComponentReference<Marker>.FromId(aTarget),
                    })],
                    []),
                new ObjectData("b", "B",
                    [new ComponentData(typeof(Marker).FullName!, new Marker { Tag = "marker-b" })],
                    []),
                new ObjectData("c", "C", [], []),
            ]))]);

    private static string Id(IObject obj) => ((GameObject)obj).Id;
}
