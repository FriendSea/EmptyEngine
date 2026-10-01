using EmptyEngine.ObjectModel;
using EmptyEngine.PlayerLoop;
using EmptyEngine.Tests;
using Xunit;

namespace EmptyEngine.World.Tests;

/// <summary>取り壊しによる寿命トークンの取り消しと、飛行中の待機・トゥイーンの打ち切り</summary>
public sealed class WorldTeardownCancellationTests
{
    private static byte[] OneObject() => SceneBlob.Encode(
    [
        new RootInstanceData("inst",
            new ObjectData("obj", "O",
                [new ComponentData(typeof(LifetimeProbe).FullName!, new LifetimeProbe())],
                [])),
    ]);

    /// <summary>コンポーネントを持たない入れ物の下に、持つ子が 1 つ</summary>
    private static byte[] EmptyRootWithChild() => SceneBlob.Encode(
    [
        new RootInstanceData("inst",
            new ObjectData("root", "Root", [],
            [
                new ObjectData("child", "Child",
                    [new ComponentData(typeof(LifetimeProbe).FullName!, new LifetimeProbe())],
                    []),
            ])),
    ]);

    private static (SceneWorld World, IObject Root) NewWorld(byte[] blob)
    {
        var world = new SceneWorld();
        world.DeserializeSceneNow(blob, isPlaying: true);
        return (world, world.Roots[0]);
    }

    [Fact]
    public void Destroying_an_object_cancels_its_lifetime()
    {
        (SceneWorld world, IObject root) = NewWorld(OneObject());
        CancellationToken lifetime = root.Lifetime();
        Assert.False(lifetime.IsCancellationRequested);

        root.Destroy();
        Assert.False(lifetime.IsCancellationRequested);

        world.FlushPendingDestructions();

        Assert.True(lifetime.IsCancellationRequested);
    }

    [Fact]
    public void Disposing_cancels_empty_parent_and_component_child_including_late_tokens()
    {
        (SceneWorld world, IObject root) = NewWorld(EmptyRootWithChild());
        IObject child = Assert.Single(root.GetChildren());
        Assert.Empty(root.GetAllAttachables());
        CancellationToken parentToken = root.Lifetime();
        CancellationToken childToken = child.Lifetime();
        Assert.False(parentToken.IsCancellationRequested);
        Assert.False(childToken.IsCancellationRequested);

        world.Dispose();

        Assert.True(parentToken.IsCancellationRequested);
        Assert.True(childToken.IsCancellationRequested);
        Assert.True(root.Lifetime().IsCancellationRequested);
        Assert.True(child.Lifetime().IsCancellationRequested);
    }

    [Fact]
    public void SetActive_false_does_not_cancel_the_lifetime()
    {
        (SceneWorld world, IObject root) = NewWorld(OneObject());
        CancellationToken lifetime = root.Lifetime();

        root.SetActive(false);

        Assert.False(lifetime.IsCancellationRequested);
    }

    [Fact]
    public void Teardown_stops_a_pending_frame_wait()
    {
        var registry = new DefaultPlayerLoopRegistry();
        var log = new List<string>();
        (SceneWorld world, IObject root) = NewWorld(OneObject());

        ValueTask pending = WaitThenLog();
        world.Dispose();
        registry.Tick(1.0 / 60.0);
        registry.Tick(1.0 / 60.0);

        Assert.True(pending.IsCanceled);
        Assert.Empty(log);

        async ValueTask WaitThenLog()
        {
            await registry.WaitFrames(2, root.Lifetime());
            log.Add("resumed");
        }
    }

    [Fact]
    public void Teardown_stops_a_pending_tween()
    {
        var registry = new DefaultPlayerLoopRegistry();
        var log = new List<string>();
        (SceneWorld world, IObject root) = NewWorld(OneObject());

        ValueTask pending = TweenThenLog();
        world.Dispose();
        registry.Tick(1.0);

        Assert.True(pending.IsCanceled);
        Assert.Empty(log);

        async ValueTask TweenThenLog()
        {
            await Tween.Play(1.0, static _ => { }, registry, cancellationToken: root.Lifetime());
            log.Add("resumed");
        }
    }

    [Fact]
    public void A_frame_wait_started_with_a_dead_token_never_runs_its_continuation()
    {
        var registry = new DefaultPlayerLoopRegistry();
        var log = new List<string>();
        (SceneWorld world, IObject root) = NewWorld(OneObject());
        world.Dispose();

        ValueTask pending = WaitThenLog();
        registry.Tick(1.0 / 60.0);

        Assert.True(pending.IsCanceled);
        Assert.Empty(log);

        async ValueTask WaitThenLog()
        {
            await registry.WaitFrames(2, root.Lifetime());
            log.Add("resumed");
        }
    }

    [Fact]
    public void Disposing_a_running_tween_stops_the_await()
    {
        var registry = new DefaultPlayerLoopRegistry();
        var log = new List<string>();
        TweenHandle handle = Tween.Play(1.0, static _ => { }, registry);

        ValueTask pending = AwaitThenLog();
        handle.Dispose();

        Assert.True(pending.IsCanceled);
        Assert.Empty(log);

        async ValueTask AwaitThenLog()
        {
            await handle;
            log.Add("resumed");
        }
    }

    [Fact]
    public void A_tween_that_finishes_naturally_resumes_the_await()
    {
        var registry = new DefaultPlayerLoopRegistry();
        var log = new List<string>();
        TweenHandle handle = Tween.Play(0.1, static _ => { }, registry);

        ValueTask pending = AwaitThenLog();
        registry.Tick(0.2);

        Assert.True(pending.IsCompletedSuccessfully);
        Assert.Equal(["resumed"], log);
        handle.Dispose();
        ValueTask late = AwaitOnly();
        Assert.True(late.IsCompletedSuccessfully);

        async ValueTask AwaitOnly() => await handle;

        async ValueTask AwaitThenLog()
        {
            await handle;
            log.Add("resumed");
        }
    }

    [Fact]
    public void Worker_thread_cancellation_completes_a_frame_wait_on_the_player_loop_thread()
    {
        int playerLoopThread = Environment.CurrentManagedThreadId;
        var registry = new DefaultPlayerLoopRegistry();
        using var cancellation = new CancellationTokenSource();
        int completionThread = -1;
        int cancellationThread = -1;

        ValueTask pending = WaitOnly();
        var worker = new Thread(() =>
        {
            cancellationThread = Environment.CurrentManagedThreadId;
            cancellation.Cancel();
        });
        worker.Start();
        worker.Join();

        Assert.NotEqual(playerLoopThread, cancellationThread);
        Assert.False(pending.IsCompleted);
        registry.Tick(1.0 / 60.0);

        Assert.True(pending.IsCanceled);
        Assert.Equal(playerLoopThread, completionThread);

        async ValueTask WaitOnly()
        {
            try
            {
                await registry.WaitFrames(10, cancellation.Token);
            }
            finally
            {
                completionThread = Environment.CurrentManagedThreadId;
            }
        }
    }

    [Fact]
    public void Worker_thread_cancellation_completes_a_tween_on_the_player_loop_thread()
    {
        int playerLoopThread = Environment.CurrentManagedThreadId;
        var registry = new DefaultPlayerLoopRegistry();
        using var cancellation = new CancellationTokenSource();
        int completionThread = -1;
        int cancellationThread = -1;
        TweenHandle handle = Tween.Play(10.0, static _ => { }, registry, cancellationToken: cancellation.Token);

        ValueTask pending = AwaitOnly();
        var worker = new Thread(() =>
        {
            cancellationThread = Environment.CurrentManagedThreadId;
            cancellation.Cancel();
        });
        worker.Start();
        worker.Join();

        Assert.NotEqual(playerLoopThread, cancellationThread);
        Assert.False(pending.IsCompleted);
        registry.Tick(1.0 / 60.0);

        Assert.True(pending.IsCanceled);
        Assert.Equal(playerLoopThread, completionThread);

        async ValueTask AwaitOnly()
        {
            try
            {
                await handle;
            }
            finally
            {
                completionThread = Environment.CurrentManagedThreadId;
            }
        }
    }
}

/// <summary>このテスト専用の、寿命だけを見るコンポーネント</summary>
public sealed class LifetimeProbe : IAttachable
{
    public int Value { get; set; }
}
