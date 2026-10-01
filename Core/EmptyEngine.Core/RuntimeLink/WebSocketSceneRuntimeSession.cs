using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace EmptyEngine.Core.RuntimeLink;

/// <summary>エディタからの要求を捌く WebSocket ベースの runtime 同期セッション</summary>
public sealed class WebSocketSceneRuntimeSession : IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

    private readonly ISceneSerializer _sceneSerializer;
    private readonly IAssetUpdater? _assetUpdater;
    private readonly Uri _endpoint;
    private readonly Action<string>? _log;
    private readonly Action<bool>? _modeChanged;
    private readonly ConcurrentQueue<Func<Task?>> _pending = new();
    private readonly object _sync = new();
    private ClientWebSocket? _socket;
    private Task? _waiting;
    private bool? _isPlaying;

    public WebSocketSceneRuntimeSession(
        ISceneSerializer sceneSerializer,
        Uri endpoint,
        Action<string>? log = null,
        Action<bool>? modeChanged = null,
        IAssetUpdater? assetUpdater = null)
    {
        _sceneSerializer = sceneSerializer;
        _assetUpdater = assetUpdater;
        _endpoint = endpoint;
        _log = log;
        _modeChanged = modeChanged;
    }

    public WebSocketSceneRuntimeSession(
        ISceneSerializer sceneSerializer,
        string host,
        int port,
        Action<string>? log = null,
        Action<bool>? modeChanged = null,
        IAssetUpdater? assetUpdater = null)
        : this(sceneSerializer, BuildEndpoint(host, port), log, modeChanged, assetUpdater)
    {
    }

    /// <summary>受信済みの要求の呼び出しスレッドでの受信順の処理</summary>
    /// <remarks>シーン編集の適用が終わるまで、その先の要求は後の呼び出しへ持ち越す。</remarks>
    public void Pump()
    {
        while (_waiting is null || _waiting.IsCompleted)
        {
            _waiting = null;
            if (!_pending.TryDequeue(out Func<Task?>? work)) return;
            _waiting = work();
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        _log?.Invoke($"Runtime link dialing {_endpoint}...");
        bool reportedFailure = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            var socket = new ClientWebSocket();
            lock (_sync) _socket = socket;
            try
            {
                await socket.ConnectAsync(_endpoint, cancellationToken);
                reportedFailure = false;
                _log?.Invoke("Connected to editor host.");
                await ServeAsync(socket, cancellationToken);
                _log?.Invoke("Editor host disconnected. Reconnecting...");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!reportedFailure)
                {
                    reportedFailure = true;
                    _log?.Invoke($"Editor host unreachable ({ex.GetType().Name}). Retrying quietly until it answers...");
                }

                try { await Task.Delay(RetryDelay, cancellationToken); }
                catch (OperationCanceledException) { break; }
            }
            finally
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_socket, socket)) _socket = null;
                }
                socket.Dispose();
            }
        }
    }

    private async Task ServeAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            byte[]? frame = await SceneWire.ReadFrameAsync(socket, cancellationToken);
            if (frame is null) return;

            var message = new SceneMessage(
                (SceneMessageType)frame[0],
                frame[SceneWire.HeaderLength..]);

            switch (message.Type)
            {
                case SceneMessageType.RequestScene:
                    byte[] blob = await RunOnMainThreadAsync(_sceneSerializer.SerializeScene, cancellationToken);
                    await WriteMessageAsync(socket, SceneMessageType.SceneBlob, blob, cancellationToken);
                    break;
                case SceneMessageType.EditBlob:
                    if (SceneProtocol.Unframe(message.Payload) is { } edit)
                    {
                        _pending.Enqueue(() =>
                        {
                            if (_isPlaying != edit.Flag)
                            {
                                _isPlaying = edit.Flag;
                                _modeChanged?.Invoke(edit.Flag);
                            }
                            return Observe(_sceneSerializer.DeserializeSceneAsync(edit.Body, edit.Flag, cancellationToken));
                        });
                    }
                    break;
                case SceneMessageType.UpdateAssets:
                    if (_assetUpdater is { } updater
                        && SceneProtocol.UnframeKeys(message.Payload) is { Length: > 0 } keys)
                    {
                        AssetKey[] assetKeys = Array.ConvertAll(keys, static key => new AssetKey(key));
                        _pending.Enqueue(() =>
                        {
                            foreach (AssetKey key in assetKeys) updater.RequestUpdate(key, cancellationToken);
                            return null;
                        });
                    }
                    break;
            }
        }
    }

    private static Task WriteMessageAsync(
        WebSocket socket,
        SceneMessageType type,
        byte[] payload,
        CancellationToken cancellationToken) =>
        SceneWire.WriteFrameAsync(
            socket,
            SceneWire.Compose((SceneHandling)type, payload),
            cancellationToken);

    private Task<T> RunOnMainThreadAsync<T>(Func<T> work, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending.Enqueue(() =>
        {
            try { tcs.TrySetResult(work()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
            return null;
        });
        return AwaitAsync();

        async Task<T> AwaitAsync()
        {
            using (cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken)))
            {
                return await tcs.Task.ConfigureAwait(false);
            }
        }
    }

    /// <summary>適用の完了で持ち越しを解く待ち合わせ</summary>
    private Task? Observe(Task applying)
    {
        return applying.IsCompletedSuccessfully ? null : AwaitAsync(applying);

        async Task AwaitAsync(Task pending)
        {
            try
            {
                await pending.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _log?.Invoke($"Failed to apply a scene edit: {ex}");
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            try { _socket?.Abort(); } catch { }
        }
    }

    private static Uri BuildEndpoint(string host, int port) =>
        new($"ws://{host}:{port}{SceneWire.RuntimeWebSocketPath}");
}
