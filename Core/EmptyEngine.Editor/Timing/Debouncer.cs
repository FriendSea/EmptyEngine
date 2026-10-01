namespace EmptyEngine.Editor.Timing;

/// <summary>待ってから 1 本だけ走らせる遅延実行</summary>
/// <remarks>予約し直すと前の予約は取り消す。<c>hold</c> は走っても取り消されても必ず解く。
/// 操作は複数のスレッドから呼び出せる。</remarks>
internal sealed class Debouncer
{
    private CancellationTokenSource? _pending;

    /// <summary>実行の予約</summary>
    /// <param name="delay">走らせるまでの待ち</param>
    /// <param name="action">待ちが明けたら走らせるもの</param>
    /// <param name="hold">待っている間だけ掴んでおくもの。取り消されたときも解く</param>
    public void Schedule(TimeSpan delay, Action action, IDisposable? hold = null)
    {
        Cancel();

        var cancellation = new CancellationTokenSource();
        Volatile.Write(ref _pending, cancellation);

        Task.Delay(delay, cancellation.Token).ContinueWith(
            delayed =>
            {
                try
                {
                    if (delayed.Status == TaskStatus.RanToCompletion) action();
                }
                finally
                {
                    hold?.Dispose();
                    // 取り消し側との二重破棄を防ぐため、予約を取得した側だけが破棄する。
                    if (Interlocked.CompareExchange(ref _pending, null, cancellation) == cancellation)
                        cancellation.Dispose();
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    /// <summary>予約の取り消し</summary>
    public void Cancel()
    {
        CancellationTokenSource? pending = Interlocked.Exchange(ref _pending, null);
        if (pending is null) return;

        try
        {
            pending.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        pending.Dispose();
    }
}
