using Microsoft.Extensions.Logging;

namespace EmptyEngine.Editor.Assets;

/// <summary>アセットの自動保存を予約し、順に書き込む。</summary>
/// <remarks>新しい予約は待機中の予約を置き換える。保存対象を替える前に <see cref="Flush"/> で書き込むこと。</remarks>
internal sealed class AssetSaveQueue(ILogger logger) : IAsyncDisposable
{
    private readonly object _gate = new();

    private Task _writes = Task.CompletedTask;
    private CancellationTokenSource? _delay;
    private SaveRequest? _pending;
    private bool _disposed;

    /// <summary>書き戻しの予約（前の予約は取り消す）</summary>
    /// <param name="delay">書くまでの待ち。<see cref="TimeSpan.Zero"/> なら待たない</param>
    public void Schedule(IAssetImporter importer, string path, AuthoringObject asset, TimeSpan delay)
    {
        var request = new SaveRequest(importer, path, asset);
        CancellationTokenSource? waiting = null;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            CancelDelay();
            _pending = request;
            if (delay > TimeSpan.Zero) _delay = waiting = new CancellationTokenSource();
        }

        if (waiting is null)
        {
            Flush();
            return;
        }

        Task.Delay(delay, waiting.Token).ContinueWith(
            delayed =>
            {
                if (delayed.Status == TaskStatus.RanToCompletion) Write(request);
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    /// <summary>待っている予約を今すぐ書く（無ければ何もしない）</summary>
    public void Flush() => Write(expected: null);

    /// <summary>待っている予約を書き、書き終わるまで待つ</summary>
    /// <remarks>同じソースを読み直す前に挟む。<see cref="Flush"/> は鎖に積むだけで書き終わりを待たない</remarks>
    public Task FlushAsync()
    {
        Flush();
        lock (_gate) return _writes;
    }

    /// <summary>待っている予約を書き切ってから閉じる</summary>
    public async ValueTask DisposeAsync()
    {
        Task writes = FlushAsync();
        lock (_gate)
        {
            _disposed = true;
            CancelDelay();
        }

        await writes.ConfigureAwait(false);
    }

    /// <param name="expected">待ちが明けた側の予約。既に別の予約へ替わっていたら書かない</param>
    private void Write(SaveRequest? expected)
    {
        lock (_gate)
        {
            if (_pending is not { } request) return;
            if (expected is not null && !ReferenceEquals(request, expected)) return;

            CancelDelay();
            _pending = null;
            _writes = _writes.ContinueWith(
                _ => SaveAsync(request),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default).Unwrap();
        }
    }

    private void CancelDelay()
    {
        CancellationTokenSource? delay = _delay;
        _delay = null;
        if (delay is null) return;

        delay.Cancel();
        delay.Dispose();
    }

    /// <summary>保存を実行し、失敗をログへ報告する。</summary>
    private async Task SaveAsync(SaveRequest request)
    {
        try
        {
            await request.Importer.SaveAsync(request.Asset, request.Path, CancellationToken.None)
                .ConfigureAwait(false);
            logger.LogInformation("Saved asset: {Asset}", Path.GetFileName(request.Path));
        }
        catch (Exception ex)
        {
            logger.LogError("Failed to save asset '{Asset}': {Error}", Path.GetFileName(request.Path), ex.Message);
        }
    }

    private sealed record SaveRequest(IAssetImporter Importer, string Path, AuthoringObject Asset);
}
