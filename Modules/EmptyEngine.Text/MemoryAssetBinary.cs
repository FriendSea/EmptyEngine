using EmptyEngine.Core;

namespace EmptyEngine.Text;

/// <summary>メモリ上のバイト配列をそのまま公開する <see cref="IAssetBinary"/></summary>
internal sealed class MemoryAssetBinary(byte[] bytes) : IAssetBinary
{
    public long Length => bytes.Length;

    public Stream OpenRead() => new MemoryStream(bytes, writable: false);
}
