using EmptyEngine.Modules.Testing;
using EmptyEngine.Serialization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using EmptyEngine.Core;
using EmptyEngine.ObjectModel;
using EmptyEngine.Storage.Editor;
using EmptyEngine.World;
using Xunit;

namespace EmptyEngine.Storage.Tests;

/// <summary>HTTP を供給元にする <see cref="AssetStorage"/> の検証</summary>
public sealed class HttpAssetStoreTests : IDisposable
{
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "ee-http-cache-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_cache)) Directory.Delete(_cache, recursive: true);
    }

    [Fact]
    public async Task A_key_is_read_from_the_server_once_and_then_opens_without_waiting()
    {
        using var server = new StubAssetServer();
        server.Put("nested/a", [1]);
        // キーは URL 予約文字を含みうる（サブアセットの LocalId はメッシュ名やスプライト名由来）。
        server.Put("nested/with space", [2]);

        using AssetStorage store = AssetStorage.FromHttp(server.BaseUrl, _cache);
        Assert.False(store.TryOpen("nested/a", out _));
        Assert.Equal(new byte[] { 1 }, await ReadAllAsync(store, "nested\\a"));
        Assert.Equal(new byte[] { 2 }, await ReadAllAsync(store, "nested/with space"));

        server.Stop();

        Assert.Equal(new byte[] { 1 }, ReadNow(store, "nested/a"));
        Assert.Equal(new byte[] { 2 }, await ReadAllAsync(store, "nested/with space"));
    }

    [Fact]
    public async Task A_key_missing_on_the_server_opens_as_nothing()
    {
        using var server = new StubAssetServer();

        using AssetStorage store = AssetStorage.FromHttp(server.BaseUrl, _cache);

        Assert.Null(await store.OpenAsync("missing"));
        Assert.False(store.TryOpen("missing", out _));
    }

    [Fact]
    public async Task Stores_pointing_at_the_same_cache_share_what_was_read()
    {
        using var server = new StubAssetServer();
        server.Put("a", [1]);

        using (AssetStorage first = AssetStorage.FromHttp(server.BaseUrl, _cache))
            await ReadAllAsync(first, "a");

        // 世界の作り直しで作られる次のストアは読み直さずに開ける。
        server.Stop();
        using AssetStorage next = AssetStorage.FromHttp(server.BaseUrl, _cache);
        Assert.Equal(new byte[] { 1 }, ReadNow(next, "a"));
    }

    [Fact]
    public async Task Clearing_the_cache_reads_the_latest_and_forgets_keys_the_server_no_longer_has()
    {
        using var server = new StubAssetServer();
        server.Put("a", [1]);

        using AssetStorage store = AssetStorage.FromHttp(server.BaseUrl, _cache);
        await ReadAllAsync(store, "a");

        server.Put("a", [2]);
        Assert.Equal(new byte[] { 1 }, await ReadAllAsync(store, "a"));

        store.ClearCache("a");
        Assert.False(store.TryOpen("a", out _));
        Assert.Equal(new byte[] { 2 }, await ReadAllAsync(store, "a"));

        server.Delete("a");
        store.ClearCache("a");
        Assert.Null(await store.OpenAsync("a"));
        Assert.False(store.TryOpen("a", out _));
    }

    [Fact]
    public async Task Resolver_reloads_in_place_from_the_latest_content()
    {
        using var server = new StubAssetServer();
        server.Put("asset", Serialize("v1"));

        using AssetStorage store = AssetStorage.FromHttp(server.BaseUrl, _cache);
        var resolver = WorldAssetResolver.FromStore(store);
        var key = new AssetKey("asset");
        TestAsset? first = await resolver.ResolveAsync(new AssetReference<TestAsset>(key));
        Assert.Equal("v1", first?.Text);

        server.Put("asset", Serialize("v2"));
        await resolver.UpdateAsync(key);

        Assert.True(resolver.TryResolve(new AssetReference<TestAsset>(key), out TestAsset? updated));
        Assert.Same(first, updated);
        Assert.Equal("v2", first!.Text);
    }

    [Fact]
    public async Task An_asset_body_opens_without_waiting_once_the_asset_is_resolved()
    {
        using var server = new StubAssetServer();
        byte[] payload = [7, 8, 9];
        using (var blob = new MemoryStream())
        {
            AssetBlob.SerializeTo(blob, new BinaryTestAsset { Width = 1, Height = 1, Payload = new BytesBinary(payload) });
            server.Put("body", blob.ToArray());
        }

        using AssetStorage store = AssetStorage.FromHttp(server.BaseUrl, _cache);
        var resolver = WorldAssetResolver.FromStore(store);
        BinaryTestAsset? asset =
            await resolver.ResolveAsync(new AssetReference<BinaryTestAsset>("body"));
        server.Stop();

        using Stream body = asset!.Payload.OpenRead();
        Assert.Equal(payload, ToArray(body));
    }

    [Fact]
    public async Task A_base_url_without_a_trailing_slash_still_reads_below_it()
    {
        using var server = new StubAssetServer();
        server.Put("a", [1]);

        using AssetStorage store = AssetStorage.FromHttp(server.BaseUrl.TrimEnd('/'), _cache);

        Assert.Equal(new byte[] { 1 }, await ReadAllAsync(store, "a"));
    }

    [Fact]
    public async Task A_shared_editor_directory_served_as_static_files_resolves_on_demand()
    {
        string root = Path.Combine(Path.GetTempPath(), "ee-http-served-" + Guid.NewGuid().ToString("N"));
        try
        {
            EditorAssetStorage editor = EditorAssetStorage.AtDirectory(root);
            editor.Write("nested/with space", Serialize("served"));
            editor.Write("untouched", Serialize("never asked"));
            using var server = new StubAssetServer(root);

            using AssetStorage store = AssetStorage.FromHttp(server.BaseUrl, _cache);
            var resolver = WorldAssetResolver.FromStore(store);
            TestAsset? served = await resolver.ResolveAsync(new AssetReference<TestAsset>("nested/with space"));
            server.Stop();

            Assert.Equal("served", served?.Text);
            Assert.False(store.TryOpen("untouched", out _));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_prefab_resolved_over_http_instantiates_synchronously_afterwards()
    {
        using var server = new StubAssetServer();
        server.Put("sprite", Serialize("from the server"));
        server.Put("prefab", SceneBlob.Encode([new RootInstanceData("prefab-scene",
            new ObjectData("src", "Prefab",
                [new ComponentData(typeof(HookedSprite).FullName!,
                    new HookedSprite { Asset = AssetReference<TestAsset>.FromKey("sprite") })],
                []))]));

        using AssetStorage store = AssetStorage.FromHttp(server.BaseUrl, _cache);
        var resolver = WorldAssetResolver.FromStore(store);
        IObject? template = await resolver.ResolveAsync(new AssetReference<IObject>("prefab"));
        server.Stop();

        var world = new SceneWorld(resolver);
        IObject spawned = world.Instantiate(template!);

        HookedSprite sprite = spawned.GetAttachable<HookedSprite>()!;
        Assert.Equal("from the server", sprite.Resolved?.Text);
        Assert.Equal("from the server", sprite.SeenOnDeserialized);
    }

    [Fact]
    public async Task A_template_stays_hidden_until_the_assets_it_references_arrive()
    {
        using var server = new StubAssetServer();
        server.Put("sprite", Serialize("arrived"));
        server.Put("prefab", SceneBlob.Encode([new RootInstanceData("prefab-scene",
            new ObjectData("src", "Prefab",
                [new ComponentData(typeof(HookedSprite).FullName!,
                    new HookedSprite { Asset = AssetReference<TestAsset>.FromKey("sprite") })],
                []))]));
        server.Hold("sprite");

        using AssetStorage store = AssetStorage.FromHttp(server.BaseUrl, _cache);
        var resolver = WorldAssetResolver.FromStore(store);
        var prefab = new AssetReference<IObject>("prefab");
        Task<IObject?> first = resolver.ResolveAsync(prefab).AsTask();
        await server.Requested("sprite");

        Assert.False(resolver.TryResolve(prefab, out _));
        Task<IObject?> second = resolver.ResolveAsync(prefab).AsTask();
        Assert.False(second.IsCompleted);

        server.Release("sprite");
        IObject? template = await first;
        Assert.Same(template, await second);
        Assert.True(resolver.TryResolve(prefab, out _));

        IObject spawned = new SceneWorld(resolver).Instantiate(template!);
        Assert.Equal("arrived", spawned.GetAttachable<HookedSprite>()!.SeenOnDeserialized);
    }

    [Fact]
    public async Task A_scene_edit_is_applied_after_the_assets_it_references_load()
    {
        using var server = new StubAssetServer();
        server.Put("sprite", Serialize("prepared"));
        byte[] blob = SceneBlob.Encode([new RootInstanceData("instance",
            new ObjectData("root", "Root",
                [new ComponentData(typeof(HookedSprite).FullName!,
                    new HookedSprite { Asset = AssetReference<TestAsset>.FromKey("sprite") })],
                []))]);

        using AssetStorage store = AssetStorage.FromHttp(server.BaseUrl, _cache);
        var resolver = WorldAssetResolver.FromStore(store);
        var world = new SceneWorld(resolver, log: _ => { });

        await world.DeserializeSceneAsync(blob, isPlaying: false);
        server.Stop();

        HookedSprite sprite = world.Roots[0].GetAttachable<HookedSprite>()!;
        Assert.Equal("prepared", sprite.SeenOnDeserialized);
    }

    [Fact]
    public void A_filesystem_path_is_rejected_instead_of_becoming_a_file_url()
    {
        Assert.Throws<ArgumentException>(() => AssetStorage.FromHttp(_cache, _cache));
    }

    private static async Task<byte[]> ReadAllAsync(AssetStorage store, string key)
    {
        using Stream stream = await store.OpenAsync(key) ?? throw new FileNotFoundException(key);
        return ToArray(stream);
    }

    private static byte[] ReadNow(AssetStorage store, string key)
    {
        Assert.True(store.TryOpen(key, out Stream? stream));
        using (stream)
            return ToArray(stream);
    }

    private static byte[] ToArray(Stream stream)
    {
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private sealed class BytesBinary(byte[] bytes) : IAssetBinary
    {
        public long Length => bytes.Length;

        public Stream OpenRead() => new MemoryStream(bytes, writable: false);
    }

    private static byte[] Serialize(string text)
    {
        using var blob = new MemoryStream();
        AssetBlob.SerializeTo(blob, new TestAsset(text));
        return blob.ToArray();
    }

    /// <summary>ディレクトリを静的ファイルとして配る最小の HTTP/1.1 サーバー</summary>
    private sealed class StubAssetServer : IDisposable
    {
        private readonly string _root;
        private readonly bool _ownsRoot;
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stopping = new();
        private readonly Task _loop;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource> _held = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource> _requested = new();

        public StubAssetServer(string? root = null)
        {
            _ownsRoot = root is null;
            _root = root ?? Path.Combine(Path.GetTempPath(), "ee-http-store-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            BaseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/assets/";
            _loop = Task.Run(AcceptAsync);
        }

        public string BaseUrl { get; }

        public void Put(string key, byte[] bytes) => WriteFile(ImportedAssetsLayout.ArtifactPath(key), bytes);

        public void Delete(string key) => File.Delete(PathOf(ImportedAssetsLayout.ArtifactPath(key)));

        /// <summary>キーへの応答を <see cref="Release"/> まで止める</summary>
        public void Hold(string key) => _held[ImportedAssetsLayout.ArtifactPath(key)] = NewSignal();

        public void Release(string key) => _held[ImportedAssetsLayout.ArtifactPath(key)].TrySetResult();

        /// <summary>キーへの要求が届いた合図</summary>
        public Task Requested(string key) => _requested.GetOrAdd(ImportedAssetsLayout.ArtifactPath(key), _ => NewSignal()).Task;

        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        private void WriteFile(string relativePath, byte[] bytes)
        {
            string path = PathOf(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }

        public void Stop()
        {
            _stopping.Cancel();
            _listener.Stop();
        }

        public void Dispose()
        {
            Stop();
            try { _loop.Wait(TimeSpan.FromSeconds(5)); } catch { }
            _stopping.Dispose();
            if (_ownsRoot && Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }

        private string PathOf(string relativePath) => Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stopping.IsCancellationRequested)
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync(_stopping.Token);
                    _ = RespondAsync(client);
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }

        private async Task RespondAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    NetworkStream stream = client.GetStream();

                    var head = new StringBuilder();
                    byte[] one = new byte[1];
                    while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                    {
                        if (await stream.ReadAsync(one, _stopping.Token) == 0) return;
                        head.Append((char)one[0]);
                    }

                    string[] request = head.ToString().Split(' ', 3);
                    const string prefix = "/assets/";
                    string target = request[1].StartsWith(prefix, StringComparison.Ordinal)
                        ? Uri.UnescapeDataString(request[1][prefix.Length..])
                        : string.Empty;

                    _requested.GetOrAdd(target, _ => NewSignal()).TrySetResult();
                    if (_held.TryGetValue(target, out TaskCompletionSource? hold))
                        await hold.Task.WaitAsync(_stopping.Token);

                    if (target.Length == 0 || !File.Exists(PathOf(target)))
                    {
                        await WriteAsync(stream, "HTTP/1.1 404 Not Found", [], body: false);
                        return;
                    }

                    byte[] bytes = await File.ReadAllBytesAsync(PathOf(target), _stopping.Token);
                    await WriteAsync(stream, "HTTP/1.1 200 OK", bytes, body: request[0] != "HEAD");
                }
                catch (OperationCanceledException) { }
                catch (IOException) { }
            }
        }

        private async Task WriteAsync(NetworkStream stream, string status, byte[] bytes, bool body)
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"{status}\r\nContent-Type: application/octet-stream\r\n"
                + $"Content-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"), _stopping.Token);
            if (body) await stream.WriteAsync(bytes, _stopping.Token);
            await stream.FlushAsync(_stopping.Token);
        }
    }
}
