using EmptyEngine.Core;

namespace EmptyEngine.Editor.Assets;

/// <summary>ソースファイルを遅延で開くインポート時の本体ハンドル</summary>
public sealed class FileAssetBinary(string path) : IAssetBinary
{
    public long Length => new FileInfo(path).Length;

    public Stream OpenRead() => File.OpenRead(path);
}
