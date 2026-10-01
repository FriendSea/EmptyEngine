using EmptyEngine.Core;

namespace EmptyEngine.Editor.Assets;

/// <summary>カタログの 1 エントリ</summary>
public sealed record CatalogEntry(string DisplayPath, AssetKey Key, bool IsScene);

/// <summary>インポート済みアセットのインデックス</summary>
public sealed class AssetCatalog
{
    private sealed record Snapshot(IReadOnlyDictionary<string, ImportedSource> AssetsByKey);

    private Snapshot _snapshot = new(new Dictionary<string, ImportedSource>(StringComparer.Ordinal));

    /// <summary>GUID 参照と表示パスの組の表示パス順の列挙</summary>
    public IEnumerable<CatalogEntry> EnumerateAssets()
    {
        Snapshot snapshot = Volatile.Read(ref _snapshot);
        return snapshot.AssetsByKey.Values
            .Select(a => new CatalogEntry(DisplayPathFor(a), new AssetKey(a.Key), a is ImportedScene))
            .OrderBy(e => e.DisplayPath, StringComparer.OrdinalIgnoreCase);
    }

    private static string DisplayPathFor(ImportedSource asset) =>
        string.IsNullOrEmpty(asset.LocalId) ? asset.RelativePath : asset.RelativePath + "/" + asset.LocalId;

    public bool IsSceneAsset(string key) =>
        Volatile.Read(ref _snapshot).AssetsByKey.TryGetValue(key, out ImportedSource? asset) && asset is ImportedScene;

    public bool TryGetImportedAsset(string key, out ImportedSource? asset) =>
        Volatile.Read(ref _snapshot).AssetsByKey.TryGetValue(key, out asset);

    /// <summary>キーから人間向け表示パスの取得</summary>
    public bool TryGetDisplayPath(string key, out string? displayPath)
    {
        if (Volatile.Read(ref _snapshot).AssetsByKey.TryGetValue(key, out ImportedSource? asset))
        {
            displayPath = DisplayPathFor(asset);
            return true;
        }
        displayPath = null;
        return false;
    }

    /// <summary>キーから人間向け表示パス（引けなければ <c>null</c>）</summary>
    public string? ResolveDisplayPath(string key) =>
        !string.IsNullOrEmpty(key) && TryGetDisplayPath(key, out string? displayPath) ? displayPath : null;

    /// <summary>取り込み済み非シーンアセットの実体</summary>
    /// <returns>そのキーがシーン・未取り込みなら <c>null</c></returns>
    public AuthoringObject? GetAsset(string key) =>
        TryGetImportedAsset(key, out ImportedSource? imported) && imported is ImportedAsset asset
            ? asset.Value
            : null;

    /// <summary>取り込み済みシーンのテンプレートルート</summary>
    /// <returns>そのキーがアセット・未取り込みなら <c>null</c></returns>
    public HierarchyNode? GetSceneRoot(string key) =>
        TryGetImportedAsset(key, out ImportedSource? imported) && imported is ImportedScene scene
            ? scene.Root
            : null;

    /// <summary>キーから元のソースファイルのフルパスの取得</summary>
    public bool TryGetSourcePath(string key, out string? sourcePath)
    {
        if (TryGetImportedAsset(key, out ImportedSource? asset) && asset is not null)
        {
            sourcePath = asset.SourcePath;
            return true;
        }
        sourcePath = null;
        return false;
    }

    /// <summary>アセットのソースファイルの拡張子が <paramref name="extension"/> か</summary>
    /// <remarks>先頭のドットの有無と大文字小文字は区別しない</remarks>
    public bool IsSourceExtension(string key, string extension) =>
        TryGetSourcePath(key, out string? source)
        && source is not null
        && string.Equals(
            Path.GetExtension(source).TrimStart('.'),
            extension.TrimStart('.'),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>ソースファイルの実パスからアセットキーの取得</summary>
    /// <remarks>1 ファイルが複数アセットを生む場合は本体（<see cref="ImportedSource.LocalId"/> なし）を優先し、無ければ <c>LocalId</c> の辞書順で最初のものを返す</remarks>
    public bool TryGetKeyBySourcePath(string sourcePath, out string? key)
    {
        key = null;
        if (string.IsNullOrWhiteSpace(sourcePath)) return false;

        string full;
        try
        {
            full = Path.GetFullPath(sourcePath);
        }
        catch (Exception)
        {
            return false;
        }

        ImportedSource? best = null;
        foreach (ImportedSource asset in Volatile.Read(ref _snapshot).AssetsByKey.Values)
        {
            if (!SamePath(asset.SourcePath, full)) continue;
            if (string.IsNullOrEmpty(asset.LocalId))
            {
                best = asset;
                break;
            }
            if (best is null || string.CompareOrdinal(asset.LocalId, best.LocalId) < 0) best = asset;
        }

        key = best?.Key;
        return key is { Length: > 0 };
    }

    /// <summary>ソースファイルの名前だけからアセットキーの取得</summary>
    /// <remarks>同名のソースが複数あれば <c>false</c>。実パスが分かるときは <see cref="TryGetKeyBySourcePath"/> を使うこと</remarks>
    public bool TryGetKeyByFileName(string fileName, out string? key)
    {
        key = null;
        if (string.IsNullOrWhiteSpace(fileName)) return false;

        string? found = null;
        foreach (ImportedSource asset in Volatile.Read(ref _snapshot).AssetsByKey.Values)
        {
            if (!string.Equals(Path.GetFileName(asset.SourcePath), fileName, PathComparison)) continue;

            string full;
            try
            {
                full = Path.GetFullPath(asset.SourcePath);
            }
            catch (Exception)
            {
                continue;
            }

            if (found is null)
            {
                found = full;
                continue;
            }
            if (!string.Equals(found, full, PathComparison)) return false;
        }

        return found is not null && TryGetKeyBySourcePath(found, out key);
    }

    private static bool SamePath(string a, string bFull)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), bFull, PathComparison);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal void Update(IReadOnlyDictionary<string, ImportedSource> assetsByKey)
    {
        var assets = new Dictionary<string, ImportedSource>(assetsByKey, StringComparer.Ordinal);
        Volatile.Write(ref _snapshot, new Snapshot(assets));
    }

    internal void AddOrUpdate(ImportedSource asset)
    {
        Snapshot snapshot = Volatile.Read(ref _snapshot);
        var updated = new Dictionary<string, ImportedSource>(snapshot.AssetsByKey, StringComparer.Ordinal)
        {
            [asset.Key] = asset
        };
        Volatile.Write(ref _snapshot, new Snapshot(updated));
    }
}
