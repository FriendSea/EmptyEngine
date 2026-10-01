using System.Net;
using System.Net.Sockets;
using System.Text;
using EmptyEngine.Core;
using EmptyEngine.Core.RuntimeLink;
using EmptyEngine.Editor.Sync;
using EmptyEngine.Host;
using Xunit;

namespace EmptyEngine.Tests;

/// <summary>プレイを押した瞬間の通知の契約</summary>
public sealed class PlayStartNotificationTests
{
    private sealed class PassThroughSerializer : ISceneSerializer
    {
        private readonly Func<int> _modeChangeCount;
        private byte[] _current = [];

        public PassThroughSerializer(Func<int> modeChangeCount) => _modeChangeCount = modeChangeCount;

        public int ModeChangesAtLastApply { get; private set; }

        public byte[] SerializeScene() => _current;

        public Task DeserializeSceneAsync(byte[] blob, bool isPlaying, CancellationToken cancellationToken = default)
        {
            ModeChangesAtLastApply = _modeChangeCount();
            _current = blob;
            return Task.CompletedTask;
        }
    }

    // 同名型が Core と Host に存在するため、値を SceneWire の接続パスと揃える。
    private const string EditorPath = "/editor";

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
        private readonly PassThroughSerializer _serializer;
        private int _modeChanges;

        public RunningRuntime(int hubRuntimePort)
        {
            _serializer = new PassThroughSerializer(() => Volatile.Read(ref _modeChanges));
            var session = new WebSocketSceneRuntimeSession(
                _serializer,
                "127.0.0.1",
                hubRuntimePort,
                modeChanged: _ => Interlocked.Increment(ref _modeChanges));
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

        /// <summary>Pump スレッドに揃えた読み取り</summary>
        public int ModeChangeCount => Volatile.Read(ref _modeChanges);

        public int ModeChangesAtLastApply => _serializer.ModeChangesAtLastApply;

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
    public async Task Initial_mode_and_each_transition_notify_once()
    {
        int hubPort = FreePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        using var hub = new EditorRuntimeHub(hubPort, (_, _) => { });
        hub.Start();

        await using var runtime = new RunningRuntime(hubPort);
        await using var client = new SceneClient(EditorEndpoint(hubPort));

        byte[] editing = Encoding.UTF8.GetBytes("scene-editing");
        await SendWhenHubIsUpAsync(client, editing, isPlaying: false, cts.Token);
        Assert.Equal(editing, await PollUntilAsync(client, b => b.SequenceEqual(editing), cts.Token));
        await WaitUntilAsync(() => runtime.ModeChangeCount == 1, cts.Token);
        Assert.Equal(1, runtime.ModeChangesAtLastApply);

        byte[] playing = Encoding.UTF8.GetBytes("scene-playing");
        await client.SendSceneAsync(playing, isPlaying: true, cancellationToken: cts.Token);
        await WaitUntilAsync(() => runtime.ModeChangeCount == 2, cts.Token);
        Assert.Equal(2, runtime.ModeChangesAtLastApply);

        byte[] tweaked = Encoding.UTF8.GetBytes("scene-playing-tweaked");
        await client.SendSceneAsync(tweaked, isPlaying: true, cancellationToken: cts.Token);
        Assert.Equal(tweaked, await PollUntilAsync(client, b => b.SequenceEqual(tweaked), cts.Token));
        Assert.Equal(2, runtime.ModeChangeCount);
    }

    [Fact]
    public async Task Play_blob_pushed_into_a_fresh_runtime_reports_its_initial_mode()
    {
        int hubPort = FreePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        using var hub = new EditorRuntimeHub(hubPort, (_, _) => { });
        hub.Start();

        await using var client = new SceneClient(EditorEndpoint(hubPort));

        byte[] playing = Encoding.UTF8.GetBytes("scene-playing");
        await SendWhenHubIsUpAsync(client, playing, isPlaying: true, cts.Token);

        await using var runtime = new RunningRuntime(hubPort);

        Assert.Equal(playing, await PollUntilAsync(client, b => b.SequenceEqual(playing), cts.Token));
        await WaitUntilAsync(() => runtime.ModeChangeCount == 1, cts.Token);

        await client.SendSceneAsync(playing, isPlaying: false, cancellationToken: cts.Token);
        await client.SendSceneAsync(playing, isPlaying: true, cancellationToken: cts.Token);
        await WaitUntilAsync(() => runtime.ModeChangeCount == 3, cts.Token);
    }

    private static async Task SendWhenHubIsUpAsync(
        SceneClient client, byte[] blob, bool isPlaying, CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                await client.SendSceneAsync(blob, isPlaying, cancellationToken: cancellationToken);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(10, cancellationToken);
            }
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
