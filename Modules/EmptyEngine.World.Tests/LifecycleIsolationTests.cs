using EmptyEngine.ObjectModel;
using EmptyEngine.PlayerLoop;
using EmptyEngine.Tests;
using Xunit;

namespace EmptyEngine.World.Tests;

/// <summary>ライフサイクル処理の例外をログへ報告し、残りのコンポーネントの処理を継続する。</summary>
/// <remarks>キャンセルはログへ出力しない。</remarks>
public sealed class LifecycleIsolationTests
{
    private static byte[] Scene(LifecycleHook throwAt) => SceneBlob.Encode(
    [
        new RootInstanceData("inst", new ObjectData("obj", "O",
        [
            new ComponentData(typeof(ThrowingLifecycle).FullName!, new ThrowingLifecycle { ThrowAt = (int)throwAt }),
            new ComponentData(typeof(WitnessLifecycle).FullName!, new WitnessLifecycle()),
        ], [])),
    ]);

    private static (List<string> Log, SceneWorld World) NewWorld()
    {
        WitnessLifecycle.Log.Clear();
        var messages = new List<string>();
        return (messages, new SceneWorld(assetResolver: null, log: messages.Add));
    }

    [Theory]
    [InlineData(LifecycleHook.Created, "OnCreated")]
    [InlineData(LifecycleHook.Deserialized, "OnDeserialized")]
    public void A_throwing_hook_does_not_stop_the_next_component(LifecycleHook hook, string reported)
    {
        (List<string> messages, SceneWorld world) = NewWorld();

        world.DeserializeSceneNow(Scene(hook), isPlaying: true);

        Assert.Contains(reported, WitnessLifecycle.Log);
        Assert.Single(messages, m => m.Contains($"{reported} threw on 'O'")
                                     && m.Contains(nameof(ThrowingLifecycle)));
    }

    [Fact]
    public void A_throwing_OnDestroy_does_not_stop_the_next_component()
    {
        (List<string> messages, SceneWorld world) = NewWorld();

        world.DeserializeSceneNow(Scene(LifecycleHook.Destroyed), isPlaying: true);
        world.DeserializeSceneNow(SceneBlob.Encode([]), isPlaying: true);

        Assert.Contains("OnDestroy", WitnessLifecycle.Log);
        Assert.Single(messages, m => m.Contains("OnDestroy threw on 'O'"));
    }

    [Fact]
    public void A_cancellation_from_a_hook_is_not_reported()
    {
        (List<string> messages, SceneWorld world) = NewWorld();

        world.DeserializeSceneNow(Scene(LifecycleHook.Cancelled), isPlaying: true);

        Assert.Contains("OnCreated", WitnessLifecycle.Log);
        Assert.DoesNotContain(messages, m => m.Contains("threw"));
    }

    [Fact]
    public void A_throwing_update_does_not_stop_the_frame()
    {
        var messages = new List<string>();
        var registry = new DefaultPlayerLoopRegistry(messages.Add);
        var thrower = new ThrowingUpdate(registry);
        var counter = new CountingUpdate(registry);
        thrower.OnCreated(new DetachedOwner());
        counter.OnCreated(new DetachedOwner());

        registry.Tick(1.0 / 60.0);
        registry.Tick(1.0 / 60.0);

        Assert.Equal(2, counter.Ticks);
        // 壊れたままなら毎フレーム出る（黙らせない）
        Assert.Equal(2, messages.Count(m => m.Contains(nameof(ThrowingUpdate))));
    }

    private sealed class DetachedOwner : IObject
    {
        public IObject? Parent => null;

        public T? GetAttachable<T>() where T : class, IAttachable => null;

        public IEnumerable<IAttachable> GetAllAttachables() => [];
    }
}

/// <summary>どのフックで投げるか</summary>
public enum LifecycleHook
{
    Created = 1,
    Deserialized = 2,
    Destroyed = 3,
    Cancelled = 4,
}

/// <summary>指定のフックで投げるだけのコンポーネント</summary>
public sealed class ThrowingLifecycle : ILifecycleAttachable
{
    public int ThrowAt { get; set; }

    public void OnCreated(IObject owner)
    {
        if (ThrowAt == (int)LifecycleHook.Cancelled) throw new OperationCanceledException();
        if (ThrowAt == (int)LifecycleHook.Created) throw new InvalidOperationException("boom");
    }

    public void OnDeserialized(IObject owner)
    {
        if (ThrowAt == (int)LifecycleHook.Deserialized) throw new InvalidOperationException("boom");
    }

    public void OnDestroy(IObject owner)
    {
        if (ThrowAt == (int)LifecycleHook.Destroyed) throw new InvalidOperationException("boom");
    }
}

/// <summary>投げる隣で、自分のフックが呼ばれたことを控えるコンポーネント</summary>
public sealed class WitnessLifecycle : ILifecycleAttachable
{
    public static readonly List<string> Log = [];

    public void OnCreated(IObject owner) => Log.Add("OnCreated");

    public void OnDeserialized(IObject owner) => Log.Add("OnDeserialized");

    public void OnDestroy(IObject owner) => Log.Add("OnDestroy");
}

/// <summary>毎フレーム投げる更新</summary>
public sealed class ThrowingUpdate(DefaultPlayerLoopRegistry? registry) : UpdatableComponent(registry)
{
    protected override void Update(IObject owner, in UpdateContext context)
        => throw new InvalidOperationException("boom");
}

/// <summary>投げる隣で回り続ける更新</summary>
public sealed class CountingUpdate(DefaultPlayerLoopRegistry? registry) : UpdatableComponent(registry)
{
    public int Ticks { get; private set; }

    protected override void Update(IObject owner, in UpdateContext context) => Ticks++;
}
