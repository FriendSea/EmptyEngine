using System.Text.Json;
using System.Text.Json.Serialization;
using EmptyEngine.Core;
using EmptyEngine.Storage;

namespace EmptyEngine.World;

/// <summary>どのアーティファクトを起動シーンにするかのランタイム側規約</summary>
public static class StartupScene
{
    /// <summary>デプロイ対象シーンの順序付きリストのキー</summary>
    public const string ManifestKey = "startup.json";

    /// <summary>起動順のシーン一覧を取得する（開発用フォルダにマニフェストがなければプロジェクトの一覧を使う）</summary>
    public static async Task<IReadOnlyList<AssetKey>> ReadStartupScenesAsync(
        AssetStorage store, Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        var scenes = new List<AssetKey>();
        foreach (string key in await ReadManifestAsync(store, log, cancellationToken))
        {
            using Stream? scene = await store.OpenAsync(key, cancellationToken);
            if (scene is not null)
                scenes.Add(new AssetKey(key));
        }

        return scenes;
    }

    /// <summary>起動シーンの一覧に載ったキー（存在は問わない）</summary>
    private static async Task<string[]> ReadManifestAsync(AssetStorage store, Action<string>? log, CancellationToken cancellationToken)
    {
        string source = ManifestKey;
        string[] keys;
        try
        {
            using Stream? manifest = await store.OpenAsync(ManifestKey, cancellationToken);
            if (manifest is not null)
            {
                keys = JsonSerializer.Deserialize(manifest, StartupSceneJson.Default.StringArray)
                       ?? Array.Empty<string>();
            }
            // publish ではこの分岐を無効にする。pak / メモリストアにもプロジェクトの設定を混ぜない。
            else if (RuntimeStore.DevelopmentAssetsEnabled
                     && store.DirectoryRoot is { } root
                     && new DirectoryInfo(root).Parent is { Name: ".artifacts", Parent: { } project })
            {
                source = Path.Combine(project.FullName, "BuildScenes.json");
                if (!File.Exists(source)) return Array.Empty<string>();
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(source));
                keys = document.RootElement.GetProperty("Scenes").EnumerateArray()
                    .Select(scene => scene.GetString() ?? string.Empty).ToArray();
            }
            else return Array.Empty<string>();
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            string message = $"[StartupScene] Cannot read {source}; starting with no startup scenes: {e.Message}";
            if (log is not null) log(message);
            else Console.Error.WriteLine(message);
            return Array.Empty<string>();
        }

        return keys.Where(k => !string.IsNullOrWhiteSpace(k)).ToArray();
    }

    /// <summary>起動シーン 1 つの決定</summary>
    public static async Task<AssetKey?> FindFirstAsync(AssetStorage store, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<AssetKey> scenes = await ReadStartupScenesAsync(store, cancellationToken: cancellationToken);
        return scenes.Count == 0 ? null : scenes[0];
    }

    /// <summary>起動リストの <c>startup.json</c> バイト列への変換</summary>
    public static byte[] SerializeManifest(IReadOnlyList<AssetKey> scenes) =>
        JsonSerializer.SerializeToUtf8Bytes(
            scenes.Select(s => s.Value).ToArray(), StartupSceneJson.Default.StringArray);

    /// <summary><see cref="ManifestKey"/> の起動シーンすべての読み込みと適用</summary>
    /// <returns>読み込んだシーンの参照（マニフェスト順）。シーンが無ければ空。</returns>
    /// <remarks>適用は 1 回だけで、世界全体で OnDestroy → OnDeserialized → OnCreated の順序が保たれる</remarks>
    public static async Task<IReadOnlyList<AssetKey>> LoadAllIntoAsync(
        this ISceneSerializer serializer, Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        using AssetStorage store = RuntimeStore.Open();
        return await serializer.LoadAllIntoAsync(store, log, cancellationToken);
    }

    /// <summary>読み出す <paramref name="store"/> を明示する <see cref="LoadAllIntoAsync(ISceneSerializer, Action{string}, CancellationToken)"/></summary>
    public static async Task<IReadOnlyList<AssetKey>> LoadAllIntoAsync(
        this ISceneSerializer serializer, AssetStorage store, Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        var scenes = new List<AssetKey>();
        var blobs = new List<ReadOnlyMemory<byte>>();
        foreach (string key in await ReadManifestAsync(store, log, cancellationToken))
        {
            using Stream? scene = await store.OpenAsync(key, cancellationToken);
            if (scene is null) continue;

            using var bytes = new MemoryStream();
            scene.CopyTo(bytes);
            blobs.Add(bytes.ToArray());
            scenes.Add(new AssetKey(key));
        }

        if (scenes.Count == 0) return scenes;

        byte[] merged = SceneBlob.MergeRoots(blobs, () => Guid.NewGuid().ToString());
        await serializer.DeserializeSceneAsync(merged, isPlaying: true, cancellationToken);
        return scenes;
    }
}

/// <summary><c>startup.json</c> の型情報のコンパイル時の焼き付け</summary>
[JsonSerializable(typeof(string[]))]
internal partial class StartupSceneJson : JsonSerializerContext;
