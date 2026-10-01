using System.Runtime.CompilerServices;

namespace EmptyEngine.PlayerLoop;

/// <summary>時間（秒）ベースの補間をフレーム駆動に乗せる軽量トゥイーン</summary>
/// <remarks><see cref="Play"/> が返すハンドルを破棄すると途中停止する。満了したトゥイーンは自動的に更新対象から外れる。</remarks>
public static class Tween
{
    /// <summary><paramref name="durationSeconds"/> 秒かけて進むトゥイーンの開始</summary>
    /// <param name="durationSeconds">所要時間（秒）。0 以下なら同フレームで即座に完了（t=1 を一度適用）。</param>
    /// <param name="onUpdate">補間位置 t（ease 適用後、0..1）を受け取り値を適用するコールバック。開始時に t=0 で一度呼ばれる。</param>
    /// <param name="registry">駆動を載せるレジストリ。</param>
    /// <param name="ease">0..1 → 0..1 のイージング。既定は <see cref="Ease.Linear"/>。</param>
    /// <param name="onComplete">満了して自然完了したときに一度だけ呼ばれる。破棄による中断では呼ばれない。</param>
    /// <param name="cancellationToken">取り消されるとトゥイーンが中断され、<c>await</c> は <see cref="OperationCanceledException"/> で終わる。</param>
    /// <returns>停止用のハンドル。破棄で途中停止できる。</returns>
    public static TweenHandle Play(
        double durationSeconds,
        Action<float> onUpdate,
        PlayerLoopRegistryBase registry,
        Func<float, float>? ease = null,
        Action? onComplete = null,
        CancellationToken cancellationToken = default)
    {
        if (onUpdate is null) throw new ArgumentNullException(nameof(onUpdate));
        if (registry is null) throw new ArgumentNullException(nameof(registry));
        ease ??= Ease.Linear;

        var handle = new TweenHandle(registry, durationSeconds, onUpdate, ease, onComplete, cancellationToken);
        if (handle.IsActive)
        {
            registry.AddTween(handle);
        }

        return handle;
    }
}

/// <summary>実行中のトゥイーン 1 本を表すハンドル</summary>
/// <remarks>
/// <c>await handle</c> でトゥイーンの終わりまで待てる。既に終わっていれば同期継続する。
/// 自然完了なら <c>await</c> はそのまま返り、中断（<see cref="Dispose"/>・トークンの取り消し）なら
/// <see cref="OperationCanceledException"/> が投げられる。
/// </remarks>
public sealed class TweenHandle : IDisposable, INotifyCompletion
{
    private readonly PlayerLoopRegistryBase _registry;
    private readonly double _duration;
    private readonly Action<float> _onUpdate;
    private readonly Func<float, float> _ease;
    private readonly Action? _onComplete;
    private CancellationTokenRegistration _registration;
    private double _elapsed;
    private int _finished;
    private int _cancelled;
    private int _cancellationRequested;
    private Action? _continuation;

    internal TweenHandle(
        PlayerLoopRegistryBase registry,
        double duration,
        Action<float> onUpdate,
        Func<float, float> ease,
        Action? onComplete,
        CancellationToken cancellationToken)
    {
        _registry = registry;
        _duration = duration;
        _onUpdate = onUpdate;
        _ease = ease;
        _onComplete = onComplete;

        if (cancellationToken.IsCancellationRequested)
        {
            _finished = 1;
            _cancelled = 1;
            return;
        }

        if (_duration <= 0)
        {
            _finished = 1;
            _onUpdate(_ease(1f));
            _onComplete?.Invoke();
            return;
        }

        _onUpdate(_ease(0f));
        CancellationTokenRegistration registration = cancellationToken.Register(
            static state => ((TweenHandle)state!).RequestCancellation(),
            this);
        _registration = registration;
        if (Volatile.Read(ref _finished) != 0)
        {
            registration.Dispose();
        }
    }

    /// <summary>まだ進行中か</summary>
    public bool IsActive => Volatile.Read(ref _finished) == 0;

    /// <summary>await 用の awaiter</summary>
    public TweenHandle GetAwaiter() => this;

    /// <summary>終了済みか</summary>
    public bool IsCompleted => Volatile.Read(ref _finished) != 0;

    /// <summary>await の継続の捕捉</summary>
    public void OnCompleted(Action continuation)
    {
        if (Volatile.Read(ref _finished) != 0)
        {
            continuation();
            return;
        }

        _continuation = continuation;
    }

    /// <summary>中断されていた場合の <see cref="OperationCanceledException"/> の送出</summary>
    public void GetResult()
    {
        if (Volatile.Read(ref _cancelled) != 0)
            throw new OperationCanceledException();
    }

    /// <summary>捕捉した継続のメインスレッド上での一度だけの呼び出し</summary>
    private void ResumeAwaiter()
    {
        Action? continuation = _continuation;
        _continuation = null;
        continuation?.Invoke();
    }

    /// <summary>1 フレームの進行</summary>
    internal bool Advance(double deltaSeconds)
    {
        if (Volatile.Read(ref _finished) != 0)
        {
            return false;
        }

        if (Volatile.Read(ref _cancellationRequested) != 0)
        {
            CancelOnMainThread();
            return false;
        }

        _elapsed += deltaSeconds;
        double raw = _elapsed / _duration;
        float t = (float)Math.Clamp(raw, 0.0, 1.0);
        _onUpdate(_ease(t));

        if (Volatile.Read(ref _finished) != 0)
        {
            return false;
        }

        if (raw >= 1.0)
        {
            Volatile.Write(ref _finished, 1);
            _registration.Dispose();
            _onComplete?.Invoke();
            ResumeAwaiter();
            return false;
        }

        return true;
    }

    /// <summary>進行中のトゥイーンの途中停止</summary>
    /// <remarks>自然完了済みなら何も起きない</remarks>
    public void Dispose()
    {
        RequestCancellation();
    }

    private void RequestCancellation()
    {
        if (Volatile.Read(ref _finished) != 0
            || Interlocked.Exchange(ref _cancellationRequested, 1) != 0)
        {
            return;
        }

        _registry.CompleteOnMainThread(CancelOnMainThread);
    }

    private void CancelOnMainThread()
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref _cancelled, 1);
        _registration.Dispose();
        _registry.RemoveTween(this);
        ResumeAwaiter();
    }
}

/// <summary>よく使うイージング関数</summary>
public static class Ease
{
    public static float Linear(float t) => t;

    public static float QuadIn(float t) => t * t;

    public static float QuadOut(float t) => t * (2f - t);

    public static float QuadInOut(float t) =>
        t < 0.5f ? 2f * t * t : -1f + (4f - 2f * t) * t;

    public static float CubicIn(float t) => t * t * t;

    public static float CubicOut(float t)
    {
        float u = t - 1f;
        return u * u * u + 1f;
    }

    public static float CubicInOut(float t) =>
        t < 0.5f ? 4f * t * t * t : 1f + (t - 1f) * (2f * t - 2f) * (2f * t - 2f);
}
