using System.Net;
using System.Net.Sockets;
using System.Text;
using EmptyEngine.Core;
using EmptyEngine.Core.RuntimeLink;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Sync;
using EmptyEngine.Host;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EmptyEngine.Tests;

/// <summary>エディットモードへ戻した状態を、プレイ中の古い応答が上書きしないことの end-to-end 検証</summary>
public sealed class PlayModeStalePollOverwriteTests
{
    private sealed class NameBlobSerializer : IHierarchyBlobSerializer
    {
        public byte[] Serialize(IReadOnlyList<HierarchyNode> roots) => Encoding.UTF8.GetBytes(roots[0].Name);
        public IReadOnlyList<HierarchyNode> Deserialize(byte[] sceneBlob)
        {
            string name = Encoding.UTF8.GetString(sceneBlob);
            return new[] { new HierarchyNode(name, name) };
        }
    }

    private sealed class MutatingDuringPlaySerializer : ISceneSerializer
    {
        private byte[] _current = Encoding.UTF8.GetBytes("empty");
        private bool _isPlaying;
        public byte[] SerializeScene() =>
            _isPlaying ? Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(_current) + "-play") : _current;
        public Task DeserializeSceneAsync(byte[] blob, bool isPlaying, CancellationToken cancellationToken = default)
        {
            _current = blob;
            _isPlaying = isPlaying;
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

    [Fact]
    public async Task Stale_play_state_poll_never_overwrites_after_edit_restore()
    {
        int hubPort = FreePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        using var hub = new EditorRuntimeHub(hubPort, (_, _) => { });
        hub.Start();

        var runtime = new WebSocketSceneRuntimeSession(new MutatingDuringPlaySerializer(), "127.0.0.1", hubPort);
        Task runtimeTask = runtime.RunAsync(cts.Token);
        Task pumpTask = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                runtime.Pump();
                await Task.Delay(5, cts.Token);
            }
        }, cts.Token);

        var renderLock = new object();
        string? lastRendered = null;
        var renderedAfterExit = new List<(string Name, int AskedAtEdit)>();
        bool exited = false;

        await using var client = new SceneClient(EditorEndpoint(hubPort));
        var poller = new RuntimeScenePoller(
            client, new NameBlobSerializer(),
            (roots, askedAtEdit) =>
            {
                lock (renderLock)
                {
                    lastRendered = roots[0].Name;
                    if (exited) renderedAfterExit.Add((roots[0].Name, askedAtEdit));
                }
            },
            TimeSpan.FromMilliseconds(20), NullLogger<RuntimeScenePoller>.Instance);

        Task pollTask = poller.RunAsync(cts.Token);

        await client.SendSceneAsync(Encoding.UTF8.GetBytes("world"), isPlaying: false, cancellationToken: cts.Token);
        await WaitUntilAsync(() => Read(renderLock, () => lastRendered) == "world", cts.Token);

        await client.SendSceneAsync(Encoding.UTF8.GetBytes("world"), isPlaying: true, cancellationToken: cts.Token);
        await WaitUntilAsync(() => Read(renderLock, () => lastRendered) == "world-play", cts.Token);

        lock (renderLock) { exited = true; renderedAfterExit.Clear(); }
        await client.SendSceneAsync(Encoding.UTF8.GetBytes("world"), isPlaying: false, cancellationToken: cts.Token);

        await WaitUntilAsync(() => Read(renderLock, () => lastRendered) == "world", cts.Token);
        await Task.Delay(300, cts.Token);

        // 巻き戻し後に届くプレイ中の応答が、編集より古い番号を持つことを確認する。
        lock (renderLock)
        {
            Assert.Equal("world", lastRendered);
            Assert.All(
                renderedAfterExit.Where(answer => answer.Name == "world-play"),
                answer => Assert.True(
                    answer.AskedAtEdit < client.EditSequence,
                    $"Play-mode state arrived with a sequence newer than the edit ({answer.AskedAtEdit} / {client.EditSequence})"));
        }

        cts.Cancel();
        await IgnoreCancellation(runtimeTask);
        await IgnoreCancellation(pumpTask);
        await IgnoreCancellation(pollTask);
    }

    [Fact]
    public async Task Queued_scene_sends_are_applied_in_invocation_order()
    {
        int hubPort = FreePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        using var hub = new EditorRuntimeHub(hubPort, (_, _) => { });
        hub.Start();

        var runtimeSerializer = new MutatingDuringPlaySerializer();
        var runtime = new WebSocketSceneRuntimeSession(runtimeSerializer, "127.0.0.1", hubPort);
        Task runtimeTask = runtime.RunAsync(cts.Token);
        Task pumpTask = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                runtime.Pump();
                await Task.Delay(5, cts.Token);
            }
        }, cts.Token);

        await using var client = new SceneClient(EditorEndpoint(hubPort));

        byte[] first = Encoding.UTF8.GetBytes("first");
        byte[] second = Encoding.UTF8.GetBytes("second");
        Task firstSend = client.SendSceneAsync(first, isPlaying: false, cancellationToken: cts.Token);
        Task secondSend = client.SendSceneAsync(second, isPlaying: false, cancellationToken: cts.Token);

        await Task.WhenAll(firstSend, secondSend);
        await WaitUntilAsync(() => runtimeSerializer.SerializeScene().SequenceEqual(second), cts.Token);

        cts.Cancel();
        await IgnoreCancellation(runtimeTask);
        await IgnoreCancellation(pumpTask);
    }

    private static T Read<T>(object gate, Func<T> read) { lock (gate) return read(); }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(10, cancellationToken);
        }
    }

    private static async Task IgnoreCancellation(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
    }
}
