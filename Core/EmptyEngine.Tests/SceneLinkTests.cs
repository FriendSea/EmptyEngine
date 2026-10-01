using System.Net;
using System.Net.Sockets;
using System.Text;
using EmptyEngine.Core;
using EmptyEngine.Core.RuntimeLink;
using EmptyEngine.Editor.Sync;
using EmptyEngine.Host;
using Xunit;

namespace EmptyEngine.Tests;

/// <summary>シーン同期リンクの契約（トランスポート層）</summary>
public sealed class SceneLinkTests
{
    private sealed class PassThroughSerializer : ISceneSerializer
    {
        private byte[] _current = Array.Empty<byte>();
        public bool LastIsPlaying { get; private set; }
        public byte[] SerializeScene() => _current;
        public Task DeserializeSceneAsync(byte[] blob, bool isPlaying, CancellationToken cancellationToken = default)
        {
            _current = blob;
            LastIsPlaying = isPlaying;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingAssetUpdater : IAssetUpdater
    {
        private readonly List<string> _keys = [];

        public string[] UpdatedKeys { get { lock (_keys) return _keys.ToArray(); } }

        public void RequestUpdate(AssetKey key, CancellationToken cancellationToken = default)
        {
            lock (_keys) _keys.Add(key.Value);
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
    public async Task Runtime_returns_the_blob_it_last_applied_along_with_its_mode()
    {
        int hubPort = FreePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        using var hub = new EditorRuntimeHub(hubPort, (_, _) => { });
        hub.Start();

        var serializer = new PassThroughSerializer();
        var runtime = new WebSocketSceneRuntimeSession(serializer, "127.0.0.1", hubPort);
        Task runtimeTask = runtime.RunAsync(cts.Token);
        Task pumpTask = PumpLoopAsync(runtime, cts.Token);

        await using var client = new SceneClient(EditorEndpoint(hubPort));

        byte[] sceneA = Encoding.UTF8.GetBytes("scene-A");
        await client.SendSceneAsync(sceneA, isPlaying: false, cancellationToken: cts.Token);

        Assert.Equal(sceneA, await PollUntilAsync(client, b => b.Length > 0, cts.Token));
        Assert.False(serializer.LastIsPlaying);

        byte[] sceneB = Encoding.UTF8.GetBytes("scene-B");
        await client.SendSceneAsync(sceneB, isPlaying: true, cancellationToken: cts.Token);

        Assert.Equal(sceneB, await PollUntilAsync(client, b => b.SequenceEqual(sceneB), cts.Token));
        Assert.True(serializer.LastIsPlaying);

        cts.Cancel();
        await IgnoreCancellation(runtimeTask);
        await IgnoreCancellation(pumpTask);
    }

    /// <summary>鮮度判定の目盛りがエディタの中だけで完結すること</summary>
    [Fact]
    public async Task Edit_written_after_a_request_marks_that_request_stale()
    {
        int hubPort = FreePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        using var hub = new EditorRuntimeHub(hubPort, (_, _) => { });
        hub.Start();

        await using var client = new SceneClient(EditorEndpoint(hubPort));

        int askedAt = await client.RequestSceneAsync(cts.Token);
        Assert.Equal(askedAt, client.EditSequence);

        await client.SendSceneAsync(Encoding.UTF8.GetBytes("edited"), isPlaying: false, cancellationToken: cts.Token);
        Assert.True(askedAt < client.EditSequence);

        Assert.Equal(client.EditSequence, await client.RequestSceneAsync(cts.Token));
    }

    /// <summary>要求が、先に積まれた編集を追い越さないこと</summary>
    [Fact]
    public async Task A_request_queued_after_an_edit_is_written_after_it()
    {
        int hubPort = FreePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        using var hub = new EditorRuntimeHub(hubPort, (_, _) => { });
        hub.Start();

        await using var client = new SceneClient(EditorEndpoint(hubPort));

        // 5 本まとめて積んでから要求を積む。列を飛ばせば、書き終わっていない編集を追い越して
        // 5 より小さい目盛りが返る（同じ blob は間引かれるので中身は毎回変える）
        var edits = new List<Task>();
        for (int i = 0; i < 5; i++)
        {
            edits.Add(client.SendSceneAsync(
                Encoding.UTF8.GetBytes($"edit-{i}"), isPlaying: false, cancellationToken: cts.Token));
        }

        int askedAt = await client.RequestSceneAsync(cts.Token);
        await Task.WhenAll(edits);

        Assert.Equal(5, askedAt);
        Assert.Equal(5, client.EditSequence);
    }

    /// <summary>再インポート通知が書き換わったキーを運ぶこと</summary>
    [Fact]
    public async Task Update_assets_carries_the_keys_that_changed()
    {
        int hubPort = FreePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        using var hub = new EditorRuntimeHub(hubPort, (_, _) => { });
        hub.Start();

        var updater = new RecordingAssetUpdater();
        var runtime = new WebSocketSceneRuntimeSession(
            new PassThroughSerializer(), "127.0.0.1", hubPort, assetUpdater: updater);
        Task runtimeTask = runtime.RunAsync(cts.Token);
        Task pumpTask = PumpLoopAsync(runtime, cts.Token);

        await using var client = new SceneClient(EditorEndpoint(hubPort));

        string[] keys = ["textures/hero.png", "guid-1234#sub", "日本語のキー"];
        while (!hub.RuntimeConnected)
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, cts.Token);
        }

        await client.SendUpdateAssetsAsync(keys, cts.Token);

        while (updater.UpdatedKeys.Length < keys.Length)
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, cts.Token);
        }

        Assert.Equal(keys, updater.UpdatedKeys);

        cts.Cancel();
        await IgnoreCancellation(runtimeTask);
        await IgnoreCancellation(pumpTask);
    }

    private static Task PumpLoopAsync(WebSocketSceneRuntimeSession runtime, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                runtime.Pump();
                await Task.Delay(5, cancellationToken);
            }
        }, cancellationToken);

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

    private static async Task IgnoreCancellation(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
    }
}
