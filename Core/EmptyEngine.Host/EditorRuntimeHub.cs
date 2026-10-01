using System.Net.WebSockets;
using EmptyEngine.Core.RuntimeLink;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace EmptyEngine.Host;

/// <summary>エディタとランタイムの間でシーン同期を中継する hub</summary>
/// <remarks>両者とも同じ WebSocket ポートへ dial し、どちらが繋いだかはパスで分かれる。</remarks>
internal sealed class EditorRuntimeHub : IDisposable
{
    private readonly int _webSocketPort;
    private readonly Action<string, HostLogSeverity> _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _sync = new();
    private Peer? _editor;
    private Peer? _runtime;
    private readonly List<(bool FromRuntime, byte[] Frame)> _retained = new();
    private WebApplication? _webApp;

    public EditorRuntimeHub(int webSocketPort, Action<string, HostLogSeverity> log)
    {
        _webSocketPort = webSocketPort;
        _log = log;
    }

    private void Log(string message, HostLogSeverity severity = HostLogSeverity.Normal) => _log(message, severity);

    /// <summary>接続状態が変わったときの発火</summary>
    public event Action? StateChanged;

    /// <summary>エディタが繋がっているか</summary>
    public bool EditorConnected { get { lock (_sync) return _editor is not null; } }

    /// <summary>ランタイムが繋がっているか</summary>
    public bool RuntimeConnected { get { lock (_sync) return _runtime is not null; } }

    public void Start() => StartWebSocket();

    private void StartWebSocket()
    {
        try
        {
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(
                new WebApplicationOptions { Args = Array.Empty<string>() });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls($"http://{SceneWire.DefaultHubHost}:{_webSocketPort}");

            WebApplication app = builder.Build();
            app.UseWebSockets();
            app.Map(SceneWire.EditorWebSocketPath, context => AcceptAsync(context, isRuntime: false, "editor"));
            app.Map(SceneWire.RuntimeWebSocketPath, context => AcceptAsync(context, isRuntime: true, "runtime"));
            app.StartAsync(_cts.Token).GetAwaiter().GetResult();
            _webApp = app;
            Log($"[Host] Hub listening on ws://{SceneWire.DefaultHubHost}:{_webSocketPort}" +
                $" ({SceneWire.EditorWebSocketPath} / {SceneWire.RuntimeWebSocketPath}).");
        }
        catch (Exception ex)
        {
            Log($"[Host] Hub error: failed to listen on port {_webSocketPort}: {ex.Message}",
                HostLogSeverity.Error);
        }
    }

    private async Task AcceptAsync(HttpContext context, bool isRuntime, string label)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, context.RequestAborted);
        await AttachAndServeAsync(new Peer(socket, isRuntime, label), linked.Token);
    }

    private async Task AttachAndServeAsync(Peer peer, CancellationToken cancellationToken)
    {
        Peer? replaced = Attach(peer);
        replaced?.Dispose();
        Log($"[Host] Hub: {peer.Label} connected.");
        StateChanged?.Invoke();

        try
        {
            await ReplayAsync(peer, cancellationToken);
            await ServePeerAsync(peer, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException e)
        {
            Log($"[Host] Hub: {peer.Label} link closed: {e.Message}");
        }
        catch (Exception e)
        {
            Log($"[Host] Hub: {peer.Label} link failed: {e}", HostLogSeverity.Error);
        }
        finally
        {
            if (Detach(peer))
            {
                Log($"[Host] Hub: {peer.Label} disconnected.");
                StateChanged?.Invoke();
            }
            peer.Dispose();
        }
    }

    private Peer? Attach(Peer peer)
    {
        lock (_sync)
        {
            Peer? previous;
            if (peer.IsRuntime)
            {
                previous = _runtime;
                _runtime = peer;
            }
            else
            {
                previous = _editor;
                _editor = peer;
            }

            ForgetPendingFrom(peer);
            return previous;
        }
    }

    private async Task ReplayAsync(Peer peer, CancellationToken cancellationToken)
    {
        byte[][] pending;
        lock (_sync)
        {
            pending = _retained
                .Where(held => held.FromRuntime != peer.IsRuntime)
                .OrderBy(held => Handling(held.Frame).Has(SceneHandling.CancelOpposite) ? 1 : 0)
                .Select(held => held.Frame)
                .ToArray();
        }

        foreach (byte[] frame in pending)
        {
            Log($"[Host] Hub: replaying a held frame ({frame.Length - SceneWire.HeaderLength} bytes) to {peer.Label}.");
            await SendAsync(peer, frame, cancellationToken);
        }
    }

    private async Task ServePeerAsync(Peer peer, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            byte[]? frame = await peer.ReadFrameAsync(cancellationToken);
            if (frame is null) break;
            await RelayAsync(peer, frame, cancellationToken);
        }
    }

    private bool Detach(Peer peer)
    {
        lock (_sync)
        {
            if (peer.IsRuntime)
            {
                if (!ReferenceEquals(_runtime, peer)) return false;
                _runtime = null;
            }
            else
            {
                if (!ReferenceEquals(_editor, peer)) return false;
                _editor = null;
            }

            ForgetPendingFrom(peer);
            return true;
        }
    }

    private async Task RelayAsync(Peer from, byte[] frame, CancellationToken cancellationToken)
    {
        SceneHandling handling = Handling(frame);
        Peer? destination;
        lock (_sync)
        {
            if (handling.Has(SceneHandling.Retain)) Remember(from, frame);
            if (handling.Has(SceneHandling.CancelOpposite))
            {
                _retained.RemoveAll(held =>
                    Handling(held.Frame).Has(SceneHandling.CancelOpposite)
                    && held.FromRuntime != from.IsRuntime);
            }
            destination = from.IsRuntime ? _editor : _runtime;
        }

        await SendAsync(destination, frame, cancellationToken);
    }

    private static SceneHandling Handling(byte[] frame) => (SceneHandling)frame[0];

    private void Remember(Peer from, byte[] frame)
    {
        for (int i = 0; i < _retained.Count; i++)
        {
            if (_retained[i].FromRuntime == from.IsRuntime && _retained[i].Frame[0] == frame[0])
            {
                _retained[i] = (from.IsRuntime, frame);
                return;
            }
        }
        _retained.Add((from.IsRuntime, frame));
    }

    private void ForgetPendingFrom(Peer peer) =>
        _retained.RemoveAll(held =>
            Handling(held.Frame).Has(SceneHandling.CancelOpposite)
            && held.FromRuntime == peer.IsRuntime);

    private static async Task<bool> SendAsync(Peer? peer, byte[] frame, CancellationToken cancellationToken)
    {
        if (peer is null) return false;
        await peer.WriteGate.WaitAsync(cancellationToken);
        try
        {
            await peer.WriteFrameAsync(frame, cancellationToken);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            peer.WriteGate.Release();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        Peer? editor;
        Peer? runtime;
        lock (_sync)
        {
            editor = _editor;
            runtime = _runtime;
            _editor = null;
            _runtime = null;
        }

        editor?.Dispose();
        runtime?.Dispose();
        if (_webApp is { } app)
        {
            try { app.StopAsync().GetAwaiter().GetResult(); } catch { }
            try { app.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            _webApp = null;
        }
        _cts.Dispose();
    }

    private sealed class Peer(WebSocket socket, bool isRuntime, string label) : IDisposable
    {
        public bool IsRuntime { get; } = isRuntime;
        public string Label { get; } = label;
        public SemaphoreSlim WriteGate { get; } = new(1, 1);

        public Task<byte[]?> ReadFrameAsync(CancellationToken cancellationToken) =>
            SceneWire.ReadFrameAsync(socket, cancellationToken);

        public Task WriteFrameAsync(byte[] frame, CancellationToken cancellationToken) =>
            SceneWire.WriteFrameAsync(socket, frame, cancellationToken);

        public void Dispose()
        {
            try { socket.Abort(); } catch { }
            try { socket.Dispose(); } catch { }
            WriteGate.Dispose();
        }
    }
}
