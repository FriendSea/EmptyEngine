using EmptyEngine.Core;

namespace EmptyEngine.Audio.Editor;

/// <summary>インポータがその場でエンコードしたバイト列を本体として公開する <see cref="IAssetBinary"/></summary>
internal sealed class EncodedAssetBinary(byte[] bytes) : IAssetBinary
{
    public long Length => bytes.Length;

    public Stream OpenRead() => new MemoryStream(bytes, writable: false);
}
