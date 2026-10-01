using EmptyEngine.Core;

namespace EmptyEngine.Modules.Testing;

/// <summary>指定したバイト列をアセットの本体データとして提供する</summary>
public sealed class BytesAssetBinary(byte[] bytes) : IAssetBinary
{
    public long Length => bytes.Length;

    public Stream OpenRead() => new MemoryStream(bytes, writable: false);
}
