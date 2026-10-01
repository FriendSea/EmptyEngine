using System.Collections.Concurrent;

namespace EmptyEngine.PlayerLoop;

/// <summary>フレーム駆動される 1 グループを表すレジストリ</summary>
/// <remarks>レジストリは複数持てる。あるレジストリを <see cref="Tick"/> しなければ、そこに属する更新・待機・トゥイーンは丸ごと凍結する</remarks>
public abstract class PlayerLoopRegistryBase
{
    private readonly Action<string>? _log;
    private readonly List<UpdatableComponent> _instances = new();
    private readonly HashSet<UpdatableComponent> _registered = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<UpdatableComponent> _vacated = new(ReferenceEqualityComparer.Instance);
    private readonly List<FrameWaitAwaiter> _waits = new();
    private readonly List<TweenHandle> _tweens = new();
    private readonly List<UpdatableComponent> _tickBuffer = new();
    private readonly List<TweenHandle> _tweenBuffer = new();
    private readonly HashSet<TweenHandle> _finishedTweens = new(ReferenceEqualityComparer.Instance);
    private readonly ConcurrentQueue<Action> _mainThreadCompletions = new();
    private readonly int _mainThreadId = Environment.CurrentManagedThreadId;
    private TimeSpan _elapsed = TimeSpan.Zero;
    private long _frameIndex;

    /// <param name="log">更新が投げた例外の出力先。省略で標準エラー。</param>
    protected PlayerLoopRegistryBase(Action<string>? log = null) => _log = log;

    internal void Register(UpdatableComponent component)
    {
        if (_registered.Contains(component))
        {
            return;
        }

        // 再登録時に空席が残らないよう、登録前に詰める。
        if (_vacated.Remove(component))
        {
            CompactInstances();
        }

        _registered.Add(component);
        _instances.Add(component);
    }

    /// <summary>コンポーネントをフレーム更新の対象から外す</summary>
    internal void Unregister(UpdatableComponent component)
    {
        if (_registered.Remove(component))
        {
            _vacated.Add(component);
        }
    }

    private void CompactInstances()
    {
        _instances.RemoveAll(instance => !_registered.Contains(instance));
        _vacated.Clear();
    }

    /// <summary>指定フレーム数だけ待機する awaitable の取得</summary>
    public FrameWait WaitFrames(int frames, CancellationToken cancellationToken = default)
        => new FrameWait(this, frames, cancellationToken);

    /// <summary>次フレームまで待機する <see cref="WaitFrames(int, CancellationToken)"/> の簡易版</summary>
    public FrameWait WaitForNextFrame(CancellationToken cancellationToken = default)
        => new FrameWait(this, 1, cancellationToken);

    internal void Schedule(FrameWaitAwaiter awaiter) => _waits.Add(awaiter);

    internal void Unschedule(FrameWaitAwaiter awaiter) => _waits.Remove(awaiter);

    internal void AddTween(TweenHandle tween) => _tweens.Add(tween);

    internal void RemoveTween(TweenHandle tween) => _tweens.Remove(tween);

    internal void CompleteOnMainThread(Action completion)
    {
        if (Environment.CurrentManagedThreadId == _mainThreadId)
        {
            completion();
            return;
        }

        _mainThreadCompletions.Enqueue(completion);
    }

    /// <summary>このレジストリの 1 フレーム分の進行</summary>
    public void Tick(double deltaSeconds)
    {
        if (Environment.CurrentManagedThreadId != _mainThreadId)
        {
            throw new InvalidOperationException("PlayerLoopRegistryBase must be ticked on the thread that created it.");
        }

        if (deltaSeconds < 0)
        {
            deltaSeconds = 0;
        }

        DrainMainThreadCompletions();
        AdvanceWaits();

        TimeSpan delta = TimeSpan.FromSeconds(deltaSeconds);
        _elapsed += delta;

        var context = new UpdateContext(delta, _elapsed, _frameIndex++);
        if (_vacated.Count > 0)
        {
            CompactInstances();
        }

        _tickBuffer.Clear();
        _tickBuffer.AddRange(_instances);
        foreach (UpdatableComponent instance in _tickBuffer)
        {
            try
            {
                instance.Tick(context);
            }
            catch (Exception e) when (IsCancellation(e))
            {
            }
            catch (Exception e)
            {
                Log($"Update threw on {instance.GetType().FullName}: {e}");
            }
        }

        _tweenBuffer.Clear();
        _tweenBuffer.AddRange(_tweens);
        bool anyFinished = false;
        foreach (TweenHandle tween in _tweenBuffer)
        {
            if (!tween.Advance(deltaSeconds))
            {
                _finishedTweens.Add(tween);
                anyFinished = true;
            }
        }

        // 同時完了時の二乗コストを避けるため、一括で除去する。
        if (anyFinished)
        {
            _tweens.RemoveAll(_finishedTweens.Contains);
            _finishedTweens.Clear();
        }
    }

    private void DrainMainThreadCompletions()
    {
        while (_mainThreadCompletions.TryDequeue(out Action? completion))
        {
            completion();
        }
    }

    /// <summary>キャンセルの合図とみなせる例外か</summary>
    private static bool IsCancellation(Exception exception) => exception switch
    {
        OperationCanceledException => true,
        AggregateException aggregate => aggregate.InnerExceptions.All(IsCancellation),
        _ => false,
    };

    private void Log(string message)
    {
        if (_log is not null)
            _log($"[playerloop] {message}");
        else
            Console.Error.WriteLine($"[playerloop] {message}");
    }

    private void AdvanceWaits()
    {
        int count = _waits.Count;
        if (count == 0)
        {
            return;
        }

        List<FrameWaitAwaiter>? ready = null;
        for (int i = count - 1; i >= 0; i--)
        {
            FrameWaitAwaiter awaiter = _waits[i];
            if (awaiter.Advance())
            {
                _waits.RemoveAt(i);
                (ready ??= new List<FrameWaitAwaiter>()).Add(awaiter);
            }
        }

        if (ready is null)
        {
            return;
        }

        for (int i = ready.Count - 1; i >= 0; i--)
        {
            ready[i].Complete();
        }
    }
}

/// <summary>既定のフレーム駆動グループ</summary>
public sealed class DefaultPlayerLoopRegistry : PlayerLoopRegistryBase
{
    public DefaultPlayerLoopRegistry(Action<string>? log = null) : base(log)
    {
    }
}
