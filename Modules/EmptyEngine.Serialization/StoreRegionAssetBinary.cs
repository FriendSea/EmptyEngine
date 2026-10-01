using EmptyEngine.Core;
using EmptyEngine.Storage;

namespace EmptyEngine.Serialization;

/// <summary>アーティファクト内の領域だけを指す本体ハンドル</summary>
internal sealed class StoreRegionAssetBinary(AssetStorage store, string key, long offset, long length) : IAssetBinary
{
    public long Length => length;

    public Stream OpenRead()
    {
        if (!store.TryOpen(key, out Stream? stream))
            throw new FileNotFoundException($"Asset '{key}' is not available without reading it again.", key);
        stream.Seek(offset, SeekOrigin.Begin);
        return new BoundedReadStream(stream, length);
    }
}

/// <summary>下位ストリームの現在位置から最大 <c>length</c> バイトだけを公開する読み取り専用ストリーム</summary>
internal sealed class BoundedReadStream(Stream inner, long length) : Stream
{
    private long _remaining = length;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_remaining <= 0)
        {
            return 0;
        }

        if (buffer.Length > _remaining)
        {
            buffer = buffer[..(int)_remaining];
        }

        int read = inner.Read(buffer);
        _remaining -= read;
        return read;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
