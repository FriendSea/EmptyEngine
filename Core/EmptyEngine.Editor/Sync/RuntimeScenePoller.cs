using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace EmptyEngine.Editor.Sync;

/// <summary>ランタイムへ現在のシーンを問い合わせ続けるループ</summary>
internal sealed class RuntimeScenePoller
{
    private readonly SceneClient _client;
    private readonly IHierarchyBlobSerializer _serializer;
    private readonly Action<IReadOnlyList<HierarchyNode>, int> _onSceneReceived;
    private readonly TimeSpan _pollingInterval;
    private readonly ILogger _logger;
    private string? _lastUnreadableReason;

    public RuntimeScenePoller(
        SceneClient client,
        IHierarchyBlobSerializer serializer,
        Action<IReadOnlyList<HierarchyNode>, int> onSceneReceived,
        TimeSpan pollingInterval,
        ILogger<RuntimeScenePoller> logger)
    {
        _client = client;
        _serializer = serializer;
        _onSceneReceived = onSceneReceived;
        _pollingInterval = pollingInterval;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                int askedAtEdit = await _client.RequestSceneAsync(cancellationToken);

                // 返信は任意なので、未応答を空シーンや切断と扱わない。
                // 受信後にも編集が進むため、応答の新旧は適用時に判定する。
                if (await _client.TryReceiveSceneBlobAsync(cancellationToken) is { } blob)
                {
                    IReadOnlyList<HierarchyNode> roots = _serializer.Deserialize(blob);
                    _onSceneReceived(roots, askedAtEdit);
                }
            }
            catch (Exception ex) when (IsTransientTransportError(ex))
            {
            }
            catch (InvalidDataException ex)
            {
                ReportUnreadableBlob(ex);
            }

            await Task.Delay(_pollingInterval, cancellationToken);
        }
    }

    private static bool IsTransientTransportError(Exception ex)
    {
        return ex is IOException or SocketException or ObjectDisposedException or InvalidOperationException;
    }

    /// <summary>読み取れなかった応答を、理由ごとに 1 回だけ報告する</summary>
    private void ReportUnreadableBlob(InvalidDataException ex)
    {
        if (_lastUnreadableReason == ex.Message) return;
        _lastUnreadableReason = ex.Message;
        _logger.LogWarning("The runtime's answer could not be read, so it was dropped: {Error}", ex.Message);
    }
}
