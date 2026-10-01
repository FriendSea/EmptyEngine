using System.Net.WebSockets;
using System.Threading.Channels;
using EmptyEngine.Core.RuntimeLink;

namespace EmptyEngine.Editor.Sync;

/// <summary>編集・シーン要求・アセット更新を受付順に送るシーン同期クライアント</summary>
/// <remarks>受信は送信の列から独立して待ち、返信がなくても編集の送信を妨げない。</remarks>
internal sealed class SceneClient : IAsyncDisposable
{
    private readonly Uri _endpoint;
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly Channel<OutboundRequest> _outbound = Channel.CreateUnbounded<OutboundRequest>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly Task _outboundPump;
    private ClientWebSocket? _socket;
    private byte[]? _lastEditPayload;
    private int _editSequence;

    public SceneClient(Uri endpoint)
    {
        _endpoint = endpoint;
        _outboundPump = PumpOutboundAsync();
    }

    /// <summary>今までにワイヤへ書いた編集の本数</summary>
    public int EditSequence => Volatile.Read(ref _editSequence);

    /// <summary>ランタイムへの現在のシーンの要求</summary>
    /// <returns>先に積まれた編集を送り終え、要求を書いた時点の <see cref="EditSequence"/></returns>
    public Task<int> RequestSceneAsync(CancellationToken cancellationToken = default)
        => Enqueue(SceneMessageType.RequestScene, null, dedupe: false, cancellationToken);

    /// <summary>要求と対になるシーン blob の待ち受け</summary>
    public async Task<byte[]?> TryReceiveSceneBlobAsync(CancellationToken cancellationToken = default)
    {
        await _readGate.WaitAsync(cancellationToken);
        ClientWebSocket? socket = null;
        try
        {
            socket = await EnsureConnectedAsync(cancellationToken);
            return await ReadUntilAsync(socket, SceneMessageType.SceneBlob, cancellationToken);
        }
        catch (Exception ex) when (IsConnectionException(ex))
        {
            await ResetConnectionAsync(socket);
            return null;
        }
        finally
        {
            _readGate.Release();
        }
    }

    /// <summary>権威シーンの送信</summary>
    /// <param name="resend">直前と同じ内容でも送り直す（ロードは中身が同じでも積み直させる）</param>
    public Task SendSceneAsync(
        byte[] blob, bool isPlaying, bool resend = false, CancellationToken cancellationToken = default)
        => Enqueue(
            SceneMessageType.EditBlob,
            SceneProtocol.Frame(isPlaying, blob),
            dedupe: !resend,
            cancellationToken);

    /// <summary>ランタイムへ「このキーのアセットを読み直せ」と伝える（再インポート時）</summary>
    public Task SendUpdateAssetsAsync(
        IReadOnlyCollection<string> updatedKeys, CancellationToken cancellationToken = default)
        => Enqueue(SceneMessageType.UpdateAssets, SceneProtocol.FrameKeys(updatedKeys), dedupe: false, cancellationToken);

    private Task<int> Enqueue(
        SceneMessageType type, byte[]? payload, bool dedupe, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<int>(cancellationToken);
        }

        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_outbound.Writer.TryWrite(new OutboundRequest(type, payload, dedupe, cancellationToken, completion)))
        {
            completion.TrySetException(new ObjectDisposedException(nameof(SceneClient)));
        }

        return completion.Task;
    }

    private async Task PumpOutboundAsync()
    {
        while (await _outbound.Reader.WaitToReadAsync())
        {
            while (_outbound.Reader.TryRead(out OutboundRequest? request))
            {
                using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    request.CancellationToken, _disposeCancellation.Token);

                try
                {
                    linkedCancellation.Token.ThrowIfCancellationRequested();
                    request.Completion.TrySetResult(await WriteAsync(request, linkedCancellation.Token));
                }
                catch (OperationCanceledException)
                {
                    CancellationToken token = request.CancellationToken.IsCancellationRequested
                        ? request.CancellationToken
                        : _disposeCancellation.Token;
                    request.Completion.TrySetCanceled(token);
                }
                catch (Exception ex)
                {
                    request.Completion.TrySetException(ex);
                }
            }
        }
    }

    /// <summary>1 通の書き出し。送信済みの編集との比較・更新もこの列の中で行う。</summary>
    private async Task<int> WriteAsync(OutboundRequest request, CancellationToken cancellationToken)
    {
        // ペイロードにはモードも含まれる。同じ blob でもプレイ状態が変われば送る。
        if (request.Dedupe && _lastEditPayload is { } previous
            && previous.AsSpan().SequenceEqual(request.Payload))
        {
            return EditSequence;
        }

        ClientWebSocket? socket = null;
        try
        {
            socket = await EnsureConnectedAsync(cancellationToken);
            await SceneProtocol.WriteMessageAsync(socket, request.Type, request.Payload, cancellationToken);
            if (request.Type == SceneMessageType.EditBlob)
            {
                _lastEditPayload = request.Payload;
                return Interlocked.Increment(ref _editSequence);
            }

            return EditSequence;
        }
        catch (Exception ex) when (IsConnectionException(ex))
        {
            await ResetConnectionAsync(socket);
            throw;
        }
    }

    private async Task<byte[]?> ReadUntilAsync(
        ClientWebSocket socket,
        SceneMessageType type,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            SceneMessage? message = await SceneProtocol.ReadMessageAsync(socket, cancellationToken);
            if (message is null)
            {
                await ResetConnectionAsync(socket);
                return null;
            }

            if (message.Type == type)
            {
                return message.Payload;
            }
        }
    }

    private async Task<ClientWebSocket> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        await _connectGate.WaitAsync(cancellationToken);
        try
        {
            if (_socket is { State: WebSocketState.Open })
            {
                return _socket;
            }

            AbortSocket();
            var socket = new ClientWebSocket();
            await socket.ConnectAsync(_endpoint, cancellationToken);
            _socket = socket;
            return socket;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    /// <summary>壊れた接続の切り離し</summary>
    ///
    /// <param name="stale">落ちたと判断した接続。今のものがこれと違えば、既に張り直された後なので触らない</param>
    private async Task ResetConnectionAsync(ClientWebSocket? stale)
    {
        await _connectGate.WaitAsync();
        try
        {
            if (stale is not null && !ReferenceEquals(_socket, stale)) return;

            AbortSocket();
        }
        finally
        {
            _connectGate.Release();
        }
    }

    /// <summary>今の接続の破棄（<see cref="_connectGate"/> の内側から呼ぶこと）</summary>
    private void AbortSocket()
    {
        try
        {
            _socket?.Abort();
            _socket?.Dispose();
        }
        catch
        {
        }

        _socket = null;
    }

    private static bool IsConnectionException(Exception ex)
    {
        return ex is IOException or WebSocketException or ObjectDisposedException or InvalidOperationException;
    }

    public async ValueTask DisposeAsync()
    {
        _disposeCancellation.Cancel();
        _outbound.Writer.TryComplete();
        await _outboundPump;

        // 再接続待ちで破棄が停止しないよう、待機時間を制限する。
        bool held = await _connectGate.WaitAsync(TimeSpan.FromSeconds(1));
        try
        {
            AbortSocket();
        }
        finally
        {
            if (held) _connectGate.Release();
        }

        _readGate.Dispose();
        _connectGate.Dispose();
        _disposeCancellation.Dispose();
    }

    private sealed record OutboundRequest(
        SceneMessageType Type,
        byte[]? Payload,
        bool Dedupe,
        CancellationToken CancellationToken,
        TaskCompletionSource<int> Completion);
}
