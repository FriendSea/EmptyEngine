using System.Runtime.CompilerServices;
using EmptyEngine.ObjectModel;
using EmptyEngine.Tests;
using Xunit;

namespace EmptyEngine.World.Tests;

/// <summary>SceneWorld に渡した Edit/Play mode のライフサイクル契約</summary>
public sealed class PlayEditModeTests
{
    private static byte[] OneScene(int value) => SceneBlob.Encode(
    [
        new RootInstanceData("inst",
            new ObjectData("obj", "O",
                [new ComponentData(typeof(ModeProbe).FullName!, new ModeProbe { Value = value })],
                [])),
    ]);

    [Fact]
    public void Edit_mode_fires_OnDeserialized_only()
    {
        ModeProbe.Log.Clear();
        var world = new SceneWorld();

        world.DeserializeSceneNow(OneScene(1), isPlaying: false);

        Assert.Collection(ModeProbe.Log,
            e => { Assert.Equal("deserialized", e.Kind); Assert.Equal(1, e.Value); });
        Assert.DoesNotContain(ModeProbe.Log, e => e.Kind == "created");
    }

    [Fact]
    public void Same_mode_blob_keeps_the_component_instance()
    {
        ModeProbe.Log.Clear();
        var world = new SceneWorld();

        world.DeserializeSceneNow(OneScene(1), isPlaying: false);
        int first = ModeProbe.Log[^1].Instance;
        ModeProbe.Log.Clear();

        world.DeserializeSceneNow(OneScene(2), isPlaying: false);

        Assert.Collection(ModeProbe.Log,
            e => { Assert.Equal("deserialized", e.Kind); Assert.Equal(2, e.Value); });
        Assert.Equal(first, ModeProbe.Log[0].Instance);
    }

    [Fact]
    public void Disposing_the_world_fires_OnDestroy()
    {
        ModeProbe.Log.Clear();
        var world = new SceneWorld();
        world.DeserializeSceneNow(OneScene(1), isPlaying: true);
        ModeProbe.Log.Clear();

        world.Dispose();
        world.Dispose();

        Assert.Collection(ModeProbe.Log, e => Assert.Equal("destroyed", e.Kind));
        Assert.Empty(world.Roots);
    }

}

public sealed class ModeProbe : ILifecycleAttachable
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
