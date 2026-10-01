using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using EmptyEngine.Core;
using EmptyEngine.Core.RuntimeLink;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Sync;
using EmptyEngine.Host;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EmptyEngine.Tests;

/// <summary>Host のハブが「ランタイムの再起動をエディタから見えなくする」契約の回帰</summary>
public sealed class EditorRuntimeHubTests
{
    private const byte None = 0;
    private const byte Retain = 1 << 0;
    private const byte CancelOpposite = 1 << 1;

    private sealed class PassThroughSerializer : ISceneSerializer
    {
        private byte[] _current = Array.Empty<byte>();
        public byte[] SerializeScene() => _current;
        public Task DeserializeSceneAsync(byte[] blob, bool isPlaying, CancellationToken cancellationToken = default)
        {
            _current = blob;
            return Task.CompletedTask;
        }
    }

    private sealed class NameBlobSerializer : IHierarchyBlobSerializer
    {
        public byte[] Serialize(IReadOnlyList<HierarchyNode> roots) => Encoding.UTF8.GetBytes(roots[0].Name);
        public IReadOnlyList<HierarchyNode> Deserialize(byte[] blob) =>
            [new HierarchyNode("scene", Encoding.UTF8.GetString(blob))];
    }

    // 同名型が Core と Host に存在するため、値を SceneWire の接続パスと揃える。
    private const string EditorPath = "/editor";
    private const string RuntimePath = "/runtime";

    private static Uri EditorEndpoint(int port) =>
        new($"ws://127.0.0.1:{port}{EditorPath}");

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private sealed class RunningRuntime : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _serve;
        private readonly Task _pump;

        public RunningRuntime(int hubRuntimePort)
        {
            var session = new WebSocketSceneRuntimeSession(new PassThroughSerializer(), "127.0.0.1", hubRuntimePort);
            _serve = session.RunAsync(_cts.Token);
            _pump = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    session.Pump();
                    await Task.Delay(5, _cts.Token);
                }
            });
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            await Swallow(_serve);
            await Swallow(_pump);
            _cts.Dispose();
        }

        private static async Task Swallow(Task task)
        {
            try { await task; } catch (OperationCanceledException) { } catch (AggregateException) { }
        }
    }

    [Fact]
    public async Task Runtime_restart_recovers_the_scene_without_the_editor_resending()
    {
        int hubPort = FreePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        using var hub = new EditorRuntimeHub(hubPort, (_, _) => { });
        hub.Start();
        Assert.False(hub.RuntimeConnected);
        Assert.False(hub.EditorConnected);

        await using var client = new SceneClient(EditorEndpoint(hubPort));

        byte[] scene = Encoding.UTF8.GetBytes("scene-A");
        await using (var first = new RunningRuntime(hubPort))
        {
            await WaitUntilAsync(() => hub.RuntimeConnected, cts.Token);
            await client.SendSceneAsync(scene, isPlaying: false, cancellationToken: cts.Token);
            Assert.Equal(scene, await PollUntilAsync(client, b => b.Length > 0, cts.Token));
        }

        await WaitUntilAsync(() => !hub.RuntimeConnected, cts.Token);
        await using var restarted = new RunningRuntime(hubPort);

        Assert.Equal(scene, await PollUntilAsync(client, b => b.Length > 0, cts.Token));
        Assert.True(hub.RuntimeConnected);
    }

    [Fact]
    public async Task Poll_issued_while_no_runtime_is_connected_is_answered_once_one_arrives()
    {
        int hubPort = FreePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        using var hub = new EditorRuntimeHub(hubPort, (_, _) => { });
        hub.Start();

        await using var client = new SceneClient(EditorEndpoint(hubPort));

        byte[] scene = Encoding.UTF8.GetBytes("scene-before-runtime");
        await client.SendSceneAsync(scene, isPlaying: false, cancellationToken: cts.Token);
        await client.RequestSceneAsync(cts.Token);

        Task<byte[]?> pending = client.TryReceiveSceneBlobAsync(cts.Token);
        Assert.False(pending.IsCompleted);

        await using var runtime = new RunningRuntime(hubPort);

        Assert.Equal(scene, await pending);
    }

    [Fact]
    public async Task Runtime_that_never_replies_still_receives_edits_modes_and_asset_updates()
    {
        int hubPort = FreePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var hub = new EditorRuntimeHub(hubPort, (_, _) => { });
        hub.Start();

        // このランタイムは一度も WriteAsync しない。要求を無視し、編集だけを受け取る。
        using RawWebSocketPeer runtime = await RawWebSocketPeer.ConnectAsync(hubPort, RuntimePath, cts.Token);
        int rendered = 0;
        await using var client = new SceneClient(EditorEndpoint(hubPort));
        var poller = new RuntimeScenePoller(
            client, new NameBlobSerializer(),
            (_, _) => Interlocked.Increment(ref rendered), TimeSpan.FromMilliseconds(20), NullLogger<RuntimeScenePoller>.Instance);
        Task polling = poller.RunAsync(cts.Token);

        try
        {
            Assert.Equal((byte)SceneMessageType.RequestScene, (await runtime.ReadAsync(cts.Token)).Type);
            Assert.False(polling.IsCompleted);

            byte[] initial = Encoding.UTF8.GetBytes("initial");
            await client.SendSceneAsync(initial, isPlaying: false, cancellationToken: cts.Token);
            (byte type, byte[] payload) = await runtime.ReadAsync(cts.Token);
            Assert.Equal((byte)SceneMessageType.EditBlob, type);
            Assert.Equal(SceneProtocol.Frame(false, initial), payload);

            byte[] edited = Encoding.UTF8.GetBytes("edited");
            foreach (bool playing in new[] { false, true, false })
            {
                await client.SendSceneAsync(edited, playing, cancellationToken: cts.Token);
                (type, payload) = await runtime.ReadAsync(cts.Token);
                Assert.Equal((byte)SceneMessageType.EditBlob, type);
                Assert.Equal(SceneProtocol.Frame(playing, edited), payload);
            }

            await client.SendUpdateAssetsAsync(["changed-asset"], cts.Token);
            (type, payload) = await runtime.ReadAsync(cts.Token);
            Assert.Equal((byte)SceneMessageType.UpdateAssets, type);
            Assert.Equal(new[] { "changed-asset" }, SceneProtocol.UnframeKeys(payload));

            Assert.Equal(0, Volatile.Read(ref rendered));
            Assert.False(polling.IsCompleted);
            Assert.True(hub.RuntimeConnected);
            Assert.True(hub.EditorConnected);
        }
        finally
        {
            cts.Cancel();
            try { await polling; }
            catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("scene")]
    public async Task Repeated_scenes_are_skipped_but_changes_and_explicit_resends_are_sent(string scene)
    {
        int hubPort = FreePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var hub = new EditorRuntimeHub(hubPort, (_, _) => { });
        hub.Start();

        using RawWebSocketPeer runtime = await RawWebSocketPeer.ConnectAsync(hubPort, RuntimePath, cts.Token);
        await using var client = new SceneClient(EditorEndpoint(hubPort));
        byte[] blob = Encoding.UTF8.GetBytes(scene);
        byte[] changed = Encoding.UTF8.GetBytes(scene + "-changed");

        Task[] sends =
        [
            client.SendSceneAsync(blob, isPlaying: false, cancellationToken: cts.Token),
            client.SendSceneAsync(blob.ToArray(), isPlaying: false, cancellationToken: cts.Token),
            client.SendSceneAsync(blob, isPlaying: true, cancellationToken: cts.Token),
            client.SendSceneAsync(blob.ToArray(), isPlaying: true, cancellationToken: cts.Token),
            client.SendSceneAsync(blob, isPlaying: true, resend: true, cancellationToken: cts.Token),
            client.SendSceneAsync(changed, isPlaying: true, cancellationToken: cts.Token),
            client.SendSceneAsync(blob, isPlaying: true, cancellationToken: cts.Token),
        ];
        int askedAt = await client.RequestSceneAsync(cts.Token);
        await Task.WhenAll(sends);

        byte[][] expected =
        [
            SceneProtocol.Frame(false, blob),
            SceneProtocol.Frame(true, blob),
            SceneProtocol.Frame(true, blob),
            SceneProtocol.Frame(true, changed),
            SceneProtocol.Frame(true, blob),
        ];
        foreach (byte[] expectedPayload in expected)
        {
            (byte type, byte[] payload) = await runtime.ReadAsync(cts.Token);
            Assert.Equal((byte)SceneMessageType.EditBlob, type);
            Assert.Equal(expectedPayload, payload);
        }

        // 次が要求なら、間引いた編集はワイヤにも出ていない。
        Assert.Equal((byte)SceneMessageType.RequestScene, (await runtime.ReadAsync(cts.Token)).Type);
        Assert.Equal(expected.Length, askedAt);
        Assert.Equal(askedAt, client.EditSequence);
    }

    [Fact]
    public async Task Asset_updates_share_the_send_order_without_advancing_the_edit_sequence()
    {
        int hubPort = FreePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var hub = new EditorRuntimeHub(hubPort, (_, _) => { });
        hub.Start();

        using RawWebSocketPeer runtime = await RawWebSocketPeer.ConnectAsync(hubPort, RuntimePath, cts.Token);
        await using var client = new SceneClient(EditorEndpoint(hubPort));
        byte[] scene = Encoding.UTF8.GetBytes("scene");
        string[] keys = ["changed-asset"];

        Task[] sends =
        [
            client.SendSceneAsync(scene, isPlaying: false, cancellationToken: cts.Token),
            client.SendUpdateAssetsAsync(keys, cts.Token),
            client.SendSceneAsync(scene, isPlaying: true, cancellationToken: cts.Token),
            client.SendUpdateAssetsAsync(keys, cts.Token),
        ];
        int askedAt = await client.RequestSceneAsync(cts.Token);
        await Task.WhenAll(sends);

        foreach (bool isPlaying in new[] { false, true })
        {
            (byte type, byte[] payload) = await runtime.ReadAsync(cts.Token);
            Assert.Equal((byte)SceneMessageType.EditBlob, type);
            Assert.Equal(SceneProtocol.Frame(isPlaying, scene), payload);

            (type, payload) = await runtime.ReadAsync(cts.Token);
            Assert.Equal((byte)SceneMessageType.UpdateAssets, type);
            Assert.Equal(keys, SceneProtocol.UnframeKeys(payload));
        }

        Assert.Equal((byte)SceneMessageType.RequestScene, (await runtime.ReadAsync(cts.Token)).Type);
        Assert.Equal(2, askedAt);
        Assert.Equal(askedAt, client.EditSequence);
    }

    [Fact]
    public async Task A_type_with_no_name_is_held_or_dropped_by_what_its_own_number_says()
    {
        const byte unnamed = 1 << 7;
        const byte held = unnamed | Retain;
        int hubPort = FreePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        using var hub = new EditorRuntimeHub(hubPort, (_, _) => { });
        hub.Start();

        using RawWebSocketPeer runtime = await RawWebSocketPeer.ConnectAsync(hubPort, RuntimePath, cts.Token);
        using (RawWebSocketPeer editor = await RawWebSocketPeer.ConnectAsync(hubPort, EditorPath, cts.Token))
        {
            await WaitUntilAsync(() => hub.EditorConnected && hub.RuntimeConnected, cts.Token);

            await runtime.WriteAsync(unnamed, [1, 2, 3], cts.Token);
            await runtime.WriteAsync(held, [4, 5, 6], cts.Token);
            Assert.Equal(unnamed, (await editor.ReadAsync(cts.Token)).Type);
            Assert.Equal(held, (await editor.ReadAsync(cts.Token)).Type);
        }

        using RawWebSocketPeer reopened = await RawWebSocketPeer.ConnectAsync(hubPort, EditorPath, cts.Token);

        (byte type, byte[] payload) = await reopened.ReadAsync(cts.Token);
        Assert.Equal(held, type);
        Assert.Equal(new byte[] { 4, 5, 6 }, payload);
    }

    [Fact]
    public async Task Retained_frames_with_the_same_handling_are_kept_separately_for_each_sender()
    {
        int hubPort = FreePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var hub = new EditorRuntimeHub(hubPort, (_, _) => { });
        hub.Start();

        using (RawWebSocketPeer editor = await RawWebSocketPeer.ConnectAsync(hubPort, EditorPath, cts.Token))
        using (RawWebSocketPeer runtime = await RawWebSocketPeer.ConnectAsync(hubPort, RuntimePath, cts.Token))
        {
            await WaitUntilAsync(() => hub.EditorConnected && hub.RuntimeConnected, cts.Token);
            await runtime.WriteAsync(None, [0], cts.Token);
            Assert.Equal(new byte[] { 0 }, (await editor.ReadAsync(cts.Token)).Payload);

            await editor.WriteAsync(Retain, [1], cts.Token);
            Assert.Equal(new byte[] { 1 }, (await runtime.ReadAsync(cts.Token)).Payload);
            await runtime.WriteAsync(Retain, [2], cts.Token);
            Assert.Equal(new byte[] { 2 }, (await editor.ReadAsync(cts.Token)).Payload);
            await editor.WriteAsync(Retain, [3], cts.Token);
            Assert.Equal(new byte[] { 3 }, (await runtime.ReadAsync(cts.Token)).Payload);
        }

        await WaitUntilAsync(() => !hub.EditorConnected && !hub.RuntimeConnected, cts.Token);
        using RawWebSocketPeer reopenedEditor = await RawWebSocketPeer.ConnectAsync(hubPort, EditorPath, cts.Token);
        Assert.Equal(new byte[] { 2 }, (await reopenedEditor.ReadAsync(cts.Token)).Payload);
        using RawWebSocketPeer reopenedRuntime = await RawWebSocketPeer.ConnectAsync(hubPort, RuntimePath, cts.Token);
        Assert.Equal(new byte[] { 3 }, (await reopenedRuntime.ReadAsync(cts.Token)).Payload);

        // 各方向の最新の 1 通だけが再送され、自分が送ったフレームは返ってこない。
        await reopenedRuntime.WriteAsync(None, [4], cts.Token);
        Assert.Equal(new byte[] { 4 }, (await reopenedEditor.ReadAsync(cts.Token)).Payload);
        await reopenedEditor.WriteAsync(None, [5], cts.Token);
        Assert.Equal(new byte[] { 5 }, (await reopenedRuntime.ReadAsync(cts.Token)).Payload);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Cancel_opposite_clears_pending_frames_in_either_direction(bool fromRuntime, bool retainReply)
    {
        int hubPort = FreePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var hub = new EditorRuntimeHub(hubPort, (_, _) => { });
        hub.Start();

        string senderPath = fromRuntime ? RuntimePath : EditorPath;
        string receiverPath = fromRuntime ? EditorPath : RuntimePath;
        const byte pending = Retain | CancelOpposite;
        byte reply = (byte)(CancelOpposite | (retainReply ? Retain : 0));
        bool ReceiverConnected() => fromRuntime ? hub.EditorConnected : hub.RuntimeConnected;

        using RawWebSocketPeer sender = await RawWebSocketPeer.ConnectAsync(hubPort, senderPath, cts.Token);
        using (RawWebSocketPeer receiver = await RawWebSocketPeer.ConnectAsync(hubPort, receiverPath, cts.Token))
        {
            await WaitUntilAsync(() => hub.EditorConnected && hub.RuntimeConnected, cts.Token);
            await receiver.WriteAsync(None, [0], cts.Token);
            Assert.Equal(new byte[] { 0 }, (await sender.ReadAsync(cts.Token)).Payload);

            await sender.WriteAsync(pending, [1], cts.Token);
            await sender.WriteAsync(Retain, [2], cts.Token);
            Assert.Equal(pending, (await receiver.ReadAsync(cts.Token)).Type);
            Assert.Equal(Retain, (await receiver.ReadAsync(cts.Token)).Type);

            // フラグなしの逆方向フレームでは保留を解除しない。
            await receiver.WriteAsync(None, [3], cts.Token);
            Assert.Equal(new byte[] { 3 }, (await sender.ReadAsync(cts.Token)).Payload);
        }

        await WaitUntilAsync(() => !ReceiverConnected(), cts.Token);
        using (RawWebSocketPeer receiver = await RawWebSocketPeer.ConnectAsync(hubPort, receiverPath, cts.Token))
        {
            // 通常の保持データを復元してから、未解決のフレームを再送する。
            Assert.Equal(Retain, (await receiver.ReadAsync(cts.Token)).Type);
            Assert.Equal(pending, (await receiver.ReadAsync(cts.Token)).Type);

            await receiver.WriteAsync(reply, [4], cts.Token);
            Assert.Equal(reply, (await sender.ReadAsync(cts.Token)).Type);
        }

        await WaitUntilAsync(() => !ReceiverConnected(), cts.Token);
        using RawWebSocketPeer reopened = await RawWebSocketPeer.ConnectAsync(hubPort, receiverPath, cts.Token);
        (byte type, byte[] payload) = await reopened.ReadAsync(cts.Token);
        Assert.Equal(Retain, type);
        Assert.Equal(new byte[] { 2 }, payload);

        // 往復で再送の完了を確認し、破棄したフレームが次の通常通信に混ざらないことを確かめる。
        await reopened.WriteAsync(None, [5], cts.Token);
        Assert.Equal(new byte[] { 5 }, (await sender.ReadAsync(cts.Token)).Payload);
        await sender.WriteAsync(None, [6], cts.Token);
        (type, payload) = await reopened.ReadAsync(cts.Token);
        Assert.Equal(None, type);
        Assert.Equal(new byte[] { 6 }, payload);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1 << 7)]
    public async Task Frames_are_relayed_to_the_opposite_socket(byte handling)
    {
        int hubPort = FreePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        using var hub = new EditorRuntimeHub(hubPort, (_, _) => { });
        hub.Start();

        using RawWebSocketPeer editor = await RawWebSocketPeer.ConnectAsync(hubPort, EditorPath, cts.Token);
        using RawWebSocketPeer runtime = await RawWebSocketPeer.ConnectAsync(hubPort, RuntimePath, cts.Token);
        await WaitUntilAsync(() => hub.EditorConnected && hub.RuntimeConnected, cts.Token);

        await editor.WriteAsync(handling, [8], cts.Token);
        (byte type, byte[] payload) = await runtime.ReadAsync(cts.Token);
        Assert.Equal(handling, type);
        Assert.Equal(new byte[] { 8 }, payload);

        await runtime.WriteAsync(handling, [9], cts.Token);
        (type, payload) = await editor.ReadAsync(cts.Token);
        Assert.Equal(handling, type);
        Assert.Equal(new byte[] { 9 }, payload);
    }

    [Fact]
    public async Task Oversized_opaque_frame_disconnects_its_sender()
    {
        int hubPort = FreePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        using var hub = new EditorRuntimeHub(hubPort, (_, _) => { });
        hub.Start();

        using RawWebSocketPeer editor = await RawWebSocketPeer.ConnectAsync(hubPort, EditorPath, cts.Token);
        await WaitUntilAsync(() => hub.EditorConnected, cts.Token);

        await editor.WriteDeclaredLengthAsync(None, 256 * 1024 * 1024 + 1, cts.Token);

        await WaitUntilAsync(() => !hub.EditorConnected, cts.Token);
    }

    private sealed class RawWebSocketPeer : IDisposable
    {
        private readonly ClientWebSocket _socket;

        private RawWebSocketPeer(ClientWebSocket socket) => _socket = socket;

        public static async Task<RawWebSocketPeer> ConnectAsync(
            int port, string path, CancellationToken cancellationToken)
        {
            var endpoint = new Uri($"ws://127.0.0.1:{port}{path}");
            while (true)
            {
                var socket = new ClientWebSocket();
                try
                {
                    await socket.ConnectAsync(endpoint, cancellationToken);
                    return new RawWebSocketPeer(socket);
                }
                catch (WebSocketException)
                {
                    socket.Dispose();
                    await Task.Delay(10, cancellationToken);
                }
            }
        }

        public Task WriteAsync(byte type, byte[] payload, CancellationToken cancellationToken)
        {
            byte[] frame = new byte[5 + payload.Length];
            frame[0] = type;
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(1), payload.Length);
            payload.CopyTo(frame, 5);
            return _socket.SendAsync(frame, WebSocketMessageType.Binary, true, cancellationToken);
        }

        /// <summary>本体を伴わない頭だけを 1 通として送る（宣言長だけが過大なフレーム）</summary>
        public Task WriteDeclaredLengthAsync(byte type, int length, CancellationToken cancellationToken)
        {
            byte[] header = new byte[5];
            header[0] = type;
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), length);
            return _socket.SendAsync(header, WebSocketMessageType.Binary, true, cancellationToken);
        }

        public async Task<(byte Type, byte[] Payload)> ReadAsync(CancellationToken cancellationToken)
        {
            byte[] chunk = new byte[1024];
            using var frame = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(chunk, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new IOException("Hub closed the connection.");
                frame.Write(chunk, 0, result.Count);
            }
            while (!result.EndOfMessage);

            byte[] bytes = frame.ToArray();
            return (bytes[0], bytes[5..]);
        }

        public void Dispose()
        {
            _socket.Abort();
            _socket.Dispose();
        }
    }

    private static async Task<byte[]> PollUntilAsync(
        SceneClient client, Func<byte[], bool> accept, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await client.RequestSceneAsync(cancellationToken);
            if (await client.TryReceiveSceneBlobAsync(cancellationToken) is { } blob && accept(blob))
                return blob;

            await Task.Delay(10, cancellationToken);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(10, cancellationToken);
        }
    }
}
