namespace EmptyEngine.Editor.Assets;

/// <summary>プロジェクトのアセットルートと、参照先のパッケージが同梱するアセットの配置</summary>
/// <remarks>新しいアセットを作る場所と、書き戻せるかどうかはここで決める。取り込みへは <see cref="Sources"/> としてまとめて渡す</remarks>
public sealed class ProjectAssetLayout
{
    /// <param name="assetsRootPath">プロジェクトのソースアセットのルート</param>
    /// <param name="packages">参照先のパッケージが同梱するソースアセットの置き場（<see cref="Package"/> で作る）</param>
    public ProjectAssetLayout(string assetsRootPath, IEnumerable<AssetSource>? packages = null)
    {
        AssetsRootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(assetsRootPath));
        Sources = [new AssetSource(AssetsRootPath, string.Empty), .. packages ?? []];
    }

    /// <summary>パッケージが同梱するソースアセットの置き場</summary>
    /// <remarks><c>Packages/&lt;name&gt;/</c> の下に表示され、書き込めない</remarks>
    public static AssetSource Package(string name, string path) =>
        new(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), "Packages/" + name, IsReadOnly: true);

    /// <summary>プロジェクトのソースアセットのルート</summary>
    public string AssetsRootPath { get; }

    /// <summary>取り込みへ渡す置き場（ルートとパッケージ）</summary>
    public IReadOnlyList<AssetSource> Sources { get; }

    /// <summary>ソースがパッケージ同梱のもの（書き戻せないもの）か</summary>
    public bool IsReadOnlySource(string sourcePath)
    {
        string full = Path.GetFullPath(sourcePath);
        return Sources.Any(source => source.IsReadOnly && full.StartsWith(
            source.Path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>アセットルート配下のフォルダ（ルート基準の相対パスで、先頭の空文字はルート自身）</summary>
    /// <remarks>ドットで始まるフォルダは出さない</remarks>
    public IReadOnlyList<string> EnumerateFolders()
    {
        if (!Directory.Exists(AssetsRootPath)) return Array.Empty<string>();

        var folders = new List<string> { string.Empty };
        folders.AddRange(Directory.EnumerateDirectories(AssetsRootPath, "*", SearchOption.AllDirectories)
            .Select(dir => Path.GetRelativePath(AssetsRootPath, dir).Replace('\\', '/'))
            .Where(relative => !relative.StartsWith('.') && !relative.Contains("/."))
            .OrderBy(relative => relative, StringComparer.OrdinalIgnoreCase));
        return folders;
    }

    /// <summary>アセットルート基準の相対フォルダの絶対パス化</summary>
    /// <remarks>空とルートの外を指すものはルート自身へ丸める</remarks>
    public string ResolveFolder(string? relativeFolder)
    {
        if (string.IsNullOrWhiteSpace(relativeFolder)) return AssetsRootPath;

        string full = Path.GetFullPath(
            Path.Combine(AssetsRootPath, relativeFolder.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(AssetsRootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? full
            : AssetsRootPath;
    }
}
