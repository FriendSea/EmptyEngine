using EmptyEngine.ObjectModel;
using EmptyEngine.Tests;
using Xunit;

namespace EmptyEngine.World.Tests;

/// <summary>複数シーンを 1 つの封筒で運ぶ契約の検証</summary>
public sealed class MultiSceneTests
{
    private static ObjectData Obj(string id, string name, int value)
        => new(id, name, [new ComponentData(typeof(MultiSceneProbe).FullName!, new MultiSceneProbe { Value = value })], []);

    private static byte[] TwoScenes(int aValue, int bValue) => SceneBlob.Encode(
    [
        new RootInstanceData("inst-a", Obj("a-obj", "A-Object", aValue)),
        new RootInstanceData("inst-b", Obj("b-obj", "B-Object", bValue)),
    ]);

    [Fact]
    public void Provider_holds_multiple_scenes_simultaneously()
    {
        var world = new SceneWorld();
        world.DeserializeSceneNow(TwoScenes(1, 2), isPlaying: true);

        IReadOnlyList<RootInstanceData> decoded = SceneBlob.Decode(world.SerializeScene());

        Assert.Equal(new[] { "inst-a", "inst-b" }, decoded.Select(s => s.InstanceId).ToArray());
        Assert.Equal("A-Object", decoded[0].Root.Name);
        Assert.Equal("B-Object", decoded[1].Root.Name);
    }

    [Fact]
    public void Same_source_loads_as_independent_instances()
    {
        byte[] blob = SceneBlob.Encode(
        [
            new RootInstanceData("p1", Obj("o", "O", 10)),
            new RootInstanceData("p2", Obj("o", "O", 20)),
        ]);

        var world = new SceneWorld();
        world.DeserializeSceneNow(blob, isPlaying: true);

        IReadOnlyList<RootInstanceData> decoded = SceneBlob.Decode(world.SerializeScene());

        Assert.Equal(new[] { "p1", "p2" }, decoded.Select(s => s.InstanceId).ToArray());
        Assert.All(decoded, s => Assert.Equal("O", s.Root.Name));
    }

    [Fact]
    public void Removing_a_scene_fires_OnDestroy_only_for_that_scene()
    {
        var destroyed = new List<int>();
        MultiSceneProbe.OnDestroyed = destroyed.Add;
        try
        {
            var world = new SceneWorld();
            world.DeserializeSceneNow(TwoScenes(1, 2), isPlaying: true);

            world.DeserializeSceneNow(SceneBlob.Encode(
            [
                new RootInstanceData("inst-a", Obj("a-obj", "A-Object", 1)),
            ]), isPlaying: true);

            Assert.Equal([2], destroyed);

            IReadOnlyList<RootInstanceData> remaining = SceneBlob.Decode(world.SerializeScene());
            Assert.Equal("inst-a", Assert.Single(remaining).InstanceId);
        }
        finally
        {
            MultiSceneProbe.OnDestroyed = null;
        }
    }
}

/// <summary>このテスト専用のライフサイクルコンポーネント</summary>
public sealed class MultiSceneProbe : ILifecycleAttachable
{
    /// <summary>破棄時のコールバック</summary>
    public static Action<int>? OnDestroyed;

    public int Value { get; set; }

    public void OnCreated(IObject owner) { }

    public void OnDeserialized(IObject owner) { }

    public void OnDestroy(IObject owner) => OnDestroyed?.Invoke(Value);
}
