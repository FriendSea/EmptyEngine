using System.Runtime.CompilerServices;
using EmptyEngine.ObjectModel;
using EmptyEngine.Tests;
using Xunit;

namespace EmptyEngine.World.Tests;

/// <summary>ライフサイクル再定義の契約検証</summary>
public sealed class LifecycleReconcileTests
{
    private static byte[] SceneWith(int value)
        => SceneBlob.Encode([new RootInstanceData("inst",
            new ObjectData("obj", "O",
                [new ComponentData(typeof(LifecycleProbe).FullName!, new LifecycleProbe { Value = value })],
                []))]);

    [Fact]
    public void Reapplying_blob_keeps_instance_and_runtime_state_and_fires_only_OnDeserialized()
    {
        LifecycleProbe.Log.Clear();
        var world = new SceneWorld();

        world.DeserializeSceneNow(SceneWith(1), isPlaying: true);
        world.DeserializeSceneNow(SceneWith(2), isPlaying: true);

        Assert.Collection(LifecycleProbe.Log,
            e => { Assert.Equal("deserialized", e.Kind); Assert.Equal(1, e.Value); },
            e => Assert.Equal("created", e.Kind),
            e => { Assert.Equal("deserialized", e.Kind); Assert.Equal(2, e.Value); });

        int instance = LifecycleProbe.Log[0].Instance;
        Assert.All(LifecycleProbe.Log, e => Assert.Equal(instance, e.Instance));
        Assert.Equal(Guid.Empty, LifecycleProbe.Log[0].RuntimeId);
        Guid runtimeId = LifecycleProbe.Log[1].RuntimeId;
        Assert.NotEqual(Guid.Empty, runtimeId);
        Assert.Equal(runtimeId, LifecycleProbe.Log[2].RuntimeId);
    }

    [Fact]
    public void Adding_component_fires_OnCreated_for_the_new_one_only()
    {
        LifecycleProbe.Log.Clear();
        var world = new SceneWorld();

        world.DeserializeSceneNow(SceneWith(1), isPlaying: true);
        world.DeserializeSceneNow(SceneBlob.Encode([new RootInstanceData("inst",
            new ObjectData("obj", "O",
            [
                new ComponentData(typeof(LifecycleProbe).FullName!, new LifecycleProbe { Value = 1 }),
                new ComponentData(typeof(LifecycleProbe).FullName!, new LifecycleProbe { Value = 9 }),
            ],
            []))]), isPlaying: true);

        Assert.Equal(2, LifecycleProbe.Log.Count(e => e.Kind == "created"));
    }
}

/// <summary>テスト用ライフサイクルコンポーネント</summary>
public sealed class LifecycleProbe : ILifecycleAttachable
{
    public readonly record struct Event(string Kind, int Instance, Guid RuntimeId, int Value);

    public static readonly List<Event> Log = [];

    public int Value { get; set; }

    private Guid _runtimeId;

    public void OnCreated(IObject owner)
    {
        _runtimeId = Guid.NewGuid();
        Log.Add(new Event("created", RuntimeHelpers.GetHashCode(this), _runtimeId, Value));
    }

    public void OnDeserialized(IObject owner)
        => Log.Add(new Event("deserialized", RuntimeHelpers.GetHashCode(this), _runtimeId, Value));

    public void OnDestroy(IObject owner)
        => Log.Add(new Event("destroyed", RuntimeHelpers.GetHashCode(this), _runtimeId, Value));
}
