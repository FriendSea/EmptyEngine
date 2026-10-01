using System.Runtime.CompilerServices;

namespace EmptyEngine.PlayerLoop;

/// <summary><see cref="PlayerLoopRegistryBase.WaitFrames(int, CancellationToken)"/> が返す awaitable</summary>
public readonly struct FrameWait
{
    private readonly PlayerLoopRegistryBase _registry;
    private readonly int _frames;
    private readonly CancellationToken _cancellationToken;

    internal FrameWait(PlayerLoopRegistryBase registry, int frames, CancellationToken cancellationToken)
    {
        _registry = registry;
        _frames = frames;
        _cancellationToken = cancellationToken;
    }

    public FrameWaitAwaiter GetAwaiter() => new FrameWaitAwaiter(_registry, _frames, _cancellationToken);
}

/// <summary><see cref="FrameWait"/> の awaiter</summary>
/// <remarks>トークンが取り消されると待機は打ち切られ、<c>await</c> は <see cref="OperationCanceledException"/> で終わる</remarks>
public sealed class FrameWaitAwaiter : INotifyCompletion
{
    private readonly PlayerLoopRegistryBase _registry;
    private readonly CancellationToken _cancellationToken;
    private CancellationTokenRegistration _registration;
    private int _remaining;
    private int _cancellationRequested;
    private int _cancelled;
    private int _completed;
    private Action? _continuation;

    internal FrameWaitAwaiter(PlayerLoopRegistryBase registry, int frames, CancellationToken cancellationToken)
    {
        _registry = registry;
        _remaining = frames;
        _cancellationToken = cancellationToken;
        if (cancellationToken.IsCancellationRequested)
        {
            _cancellationRequested = 1;
            _cancelled = 1;
            _completed = 1;
        }
    }

    public bool IsCompleted => Volatile.Read(ref _completed) != 0 || _remaining <= 0;

    public void OnCompleted(Action continuation)
    {
        _continuation = continuation;
        _registry.Schedule(this);
        CancellationTokenRegistration registration = _cancellationToken.Register(
            static state => ((FrameWaitAwaiter)state!).RequestCancellation(),
            this);
        _registration = registration;
        if (Volatile.Read(ref _completed) != 0)
        {
            registration.Dispose();
        }
    }

    /// <summary>打ち切られていた場合の <see cref="OperationCanceledException"/> の送出</summary>
    public void GetResult()
    {
        if (Volatile.Read(ref _cancelled) != 0)
            throw new OperationCanceledException(_cancellationToken);
    }

    /// <summary>1 フレームの進行</summary>
    internal bool Advance()
    {
        if (Volatile.Read(ref _cancellationRequested) != 0)
        {
            Volatile.Write(ref _cancelled, 1);
            return true;
        }

        return --_remaining <= 0;
    }

    /// <summary>捕捉した継続のメインスレッド上での呼び出し</summary>
    internal void Complete()
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
        {
            return;
        }

        _registration.Dispose();
        Action? continuation = _continuation;
        _continuation = null;
        continuation?.Invoke();
    }

    /// <summary>待機の打ち切り</summary>
    private void RequestCancellation()
    {
        if (Interlocked.Exchange(ref _cancellationRequested, 1) != 0)
        {
            return;
        }

        _registry.CompleteOnMainThread(CancelOnMainThread);
    }

    private void CancelOnMainThread()
    {
        if (Volatile.Read(ref _completed) != 0)
        {
            return;
        }

        Volatile.Write(ref _cancelled, 1);
        _registry.Unschedule(this);
        Complete();
    }
}
