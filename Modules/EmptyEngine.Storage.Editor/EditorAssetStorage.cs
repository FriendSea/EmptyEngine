using System.IO.Compression;

namespace EmptyEngine.Storage.Editor;

/// <summary>エディタ側のバイト書き込みストア</summary>
public sealed class EditorAssetStorage
{
    private readonly string _root;

    private EditorAssetStorage(string root) => _root = Path.GetFullPath(root);

    /// <summary>明示したディレクトリへ書く構築</summary>
    public static EditorAssetStorage AtDirectory(string root) => new(root);

    /// <summary>編集セッションの共有ディレクトリへ書く構築</summary>
    public static EditorAssetStorage Shared() => new(ImportedAssetsLayout.ResolveEditorRoot());

    /// <summary>書き先ルート</summary>
    public string Root => _root;

    /// <summary>指定キーへの書き込みストリームの取得</summary>
    public Stream OpenWrite(string key) => Create(key);

    /// <summary>指定キーへバイト列を書き込む</summary>
    public void Write(string key, byte[] bytes)
    {
        using Stream stream = Create(key);
        stream.Write(bytes);
    }

    /// <summary>指定キーのアーティファクトを、書き込みが成功した内容で置き換える</summary>
    /// <remarks>書き込み処理が例外を投げても、既存のアーティファクトは変更しない。</remarks>
    public void WriteAtomically(string key, Action<Stream> write)
    {
        Replace(ResolvePath(key), write);
    }

    /// <summary>指定キーのシーク可能な read-only ストリームの取得</summary>
    /// <returns>そのキーが無ければ <c>null</c></returns>
    public Stream? OpenRead(string key)
    {
        string path = ResolvePath(key);
        return File.Exists(path) ? File.OpenRead(path) : null;
    }

    /// <summary>指定キーのアーティファクトが存在するか</summary>
    public bool Exists(string key) => File.Exists(ResolvePath(key));

    /// <summary>指定キーのアーティファクトの削除</summary>
    public void Delete(string key)
    {
        string path = ResolvePath(key);
        if (File.Exists(path)) File.Delete(path);

        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        string? directory = Path.GetDirectoryName(path);
        while (directory is not null
               && !string.Equals(directory, _root, comparison))
        {
            if (Directory.Exists(directory))
            {
                if (Directory.EnumerateFileSystemEntries(directory).Any()) break;
                try
                {
                    Directory.Delete(directory);
                }
                catch (IOException)
                {
                    break;
                }
                catch (UnauthorizedAccessException)
                {
                    break;
                }
            }

            directory = Path.GetDirectoryName(directory);
        }
    }

    /// <summary>ステージングディレクトリの単一アーカイブへのまとめ</summary>
    public static void PackDirectory(string sourceDir, string archivePath)
    {
        if (File.Exists(archivePath)) File.Delete(archivePath);
        ZipFile.CreateFromDirectory(sourceDir, archivePath, CompressionLevel.Optimal, includeBaseDirectory: false);
    }

    private Stream Create(string key)
    {
        string path = ResolvePath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return File.Create(path);
    }

    private static void Replace(string path, Action<Stream> write)
    {
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private string ResolvePath(string key) => AssetPath.ResolveUnderRoot(_root, ImportedAssetsLayout.ArtifactPath(key));
}
