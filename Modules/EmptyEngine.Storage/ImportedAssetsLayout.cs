using System.Diagnostics.CodeAnalysis;

namespace EmptyEngine.Storage;

/// <summary>ランタイムとエディタが同じ場所に到達するためのディレクトリ規約</summary>
/// <remarks>単体起動は exe 隣のアーカイブを読む。開発ビルドでアーカイブがない場合と編集セッションでは、walk-up した <see cref="FolderName"/> を直接読む。</remarks>
public static class ImportedAssetsLayout
{
    /// <summary>インポート済みアーティファクトを置くフォルダ名（編集セッションの共有・配布のステージング）</summary>
    public const string FolderName = "ImportedAssets";

    /// <summary>配布で全アーティファクトを 1 つにまとめる単一アーカイブのファイル名（exe 隣）</summary>
    public const string ArchiveFileName = "ImportedAssets.pak";

    /// <summary>成果物ファイルの拡張子</summary>
    /// <remarks>キー <c>a/b</c> の成果物は <c>a/b.bin</c>。ディレクトリ・アーカイブ・HTTP のどれもこの名前で置く。</remarks>
    public const string ArtifactExtension = ".bin";

    private const string ArtifactsDirName = ".artifacts";

    /// <summary>キーの成果物ファイルのルートからの相対パス（<c>/</c> 区切り）</summary>
    public static string ArtifactPath(string key) => AssetPath.NormalizeKey(key) + ArtifactExtension;

    /// <summary>ルートからの相対パスの成果物キーへの変換</summary>
    /// <returns>成果物ファイルでなければ <c>false</c></returns>
    public static bool TryGetArtifactKey(string relativePath, [NotNullWhen(true)] out string? key)
    {
        key = null;
        if (!relativePath.EndsWith(ArtifactExtension, StringComparison.OrdinalIgnoreCase)) return false;

        key = AssetPath.NormalizeKey(relativePath[..^ArtifactExtension.Length]);
        return key.Length > 0;
    }

    /// <summary>配布デプロイのステージング書き先</summary>
    public static string ResolveDistributionRoot(string exeDir) => Path.Combine(exeDir, FolderName);

    /// <summary>配布ランタイムが読む単一アーカイブ</summary>
    public static string ResolveDistributionArchive(string exeDir) => Path.Combine(exeDir, ArchiveFileName);

    /// <summary>エディタが読み書きする共有ルート（常に walk-up＝編集セッション前提）</summary>
    public static string ResolveEditorRoot() => ResolveSharedRoot();

    /// <summary>編集セッションの状態を置く <c>.artifacts</c> ルート</summary>
    public static string ResolveArtifactsRoot() =>
        FindArtifactsDir(AppContext.BaseDirectory) ?? AppContext.BaseDirectory;

    private static string ResolveSharedRoot()
    {
        string? artifacts = FindArtifactsDir(AppContext.BaseDirectory);
        return artifacts is not null
            ? Path.Combine(artifacts, FolderName)
            : Path.Combine(AppContext.BaseDirectory, FolderName);
    }

    private static string? FindArtifactsDir(string startDir)
    {
        for (DirectoryInfo? dir = new(startDir); dir is not null; dir = dir.Parent)
        {
            if (string.Equals(dir.Name, ArtifactsDirName, StringComparison.OrdinalIgnoreCase))
                return dir.FullName;

            string candidate = Path.Combine(dir.FullName, ArtifactsDirName);
            if (Directory.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
