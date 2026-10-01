using System.Buffers;
using System.Buffers.Binary;
using EmptyEngine.Core;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Authoring;
using MessagePack;

namespace EmptyEngine.Serialization.Editor;

/// <summary>エディタ側のアセットアーティファクト ⇄ <see cref="AuthoringObject"/> 変換</summary>
internal static class AuthoringAssetCodec
{
    private const int MaxEnvelopeLength = 64 * 1024 * 1024;
    private const int MaxBodyCount = 65_536;

    /// <summary>アセットのアーティファクト形式での書き出し</summary>
    public static void Encode(Stream destination, AuthoringObject asset)
    {
        ArgumentNullException.ThrowIfNull(asset);

        byte[] metadata = FieldValueCodec.EncodeBytes(asset.Data, asset.Schema.Root);
        IAssetBinary?[] bodies = BodiesOf(asset);

        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        writer.WriteArrayHeader(3);
        writer.Write(asset.TypeName);
        writer.Write(metadata);
        writer.WriteArrayHeader(bodies.Length);
        foreach (IAssetBinary? body in bodies)
            writer.Write(body?.Length ?? 0);

        writer.Flush();

        ReadOnlySpan<byte> envelope = buffer.WrittenSpan;
        Span<byte> header = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, envelope.Length);
        destination.Write(header);
        destination.Write(envelope);

        foreach (IAssetBinary? body in bodies)
        {
            if (body is null) continue;
            using Stream source = body.OpenRead();
            source.CopyTo(destination);
        }
    }

    /// <summary>スキーマが宣言する順での本体ハンドルの取り出し</summary>
    private static IAssetBinary?[] BodiesOf(AuthoringObject asset)
    {
        IReadOnlyList<string> names = asset.Schema.BinaryFields;
        var bodies = new IAssetBinary?[names.Count];
        for (int i = 0; i < names.Count; i++)
            bodies[i] = asset.Data.Get(names[i])?.Binary;
        return bodies;
    }

    /// <summary>アーティファクトから <see cref="AuthoringObject"/> を復元する。</summary>
    /// <param name="open">シーク可能な読み取り専用ストリームを開く関数。本体データの読み込み時にも使用するため、復元後も呼び出し可能であること。</param>
    public static AuthoringObject? Decode(Func<Stream> open, ISchemaSource schemas)
    {
        using Stream file = open();

        Span<byte> header = stackalloc byte[sizeof(int)];
        file.ReadExactly(header);
        int envelopeLength = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (envelopeLength <= 0)
            return null;
        if (envelopeLength > MaxEnvelopeLength)
            throw new InvalidDataException($"Asset envelope length {envelopeLength} exceeds {MaxEnvelopeLength} bytes.");
        if (!file.CanSeek || envelopeLength > file.Length - file.Position)
            throw new InvalidDataException("Asset envelope extends beyond the artifact stream.");

        byte[] envelope = new byte[envelopeLength];
        file.ReadExactly(envelope);

        var reader = new MessagePackReader(envelope);
        int envelopeCount = reader.ReadArrayHeader();
        if (envelopeCount < 3)
            return null;

        string? typeName = reader.ReadString();
        ReadOnlySequence<byte>? metadata = reader.ReadBytes();
        int bodyCount = reader.ReadArrayHeader();
        if (bodyCount > MaxBodyCount)
            throw new InvalidDataException($"Asset declares {bodyCount} binary regions; the limit is {MaxBodyCount}.");

        var lengths = new long[bodyCount];
        long bodyLength = 0;
        for (int i = 0; i < bodyCount; i++)
        {
            long length = reader.ReadInt64();
            if (length < 0) throw new InvalidDataException($"Asset binary region {i} has a negative length.");
            lengths[i] = length;
            bodyLength = checked(bodyLength + length);
        }

        if (bodyLength > file.Length - file.Position)
            throw new InvalidDataException("Asset binary regions extend beyond the artifact stream.");

        if (typeName is null || metadata is null)
            return null;

        ObjectSchema schema = schemas.Get(typeName);
        FieldValue data = FieldValueCodec.DecodeBytes(metadata.Value.ToArray(), schema.Root);

        // 古い成果物とのスキーマ差を許容するため、存在する区画だけを読み込む。
        IReadOnlyList<string> names = schema.BinaryFields;
        if (bodyCount > 0 && data.IsNull) data = new FieldValue();
        long offset = sizeof(int) + envelopeLength;
        for (int i = 0; i < bodyCount; i++)
        {
            if (i < names.Count)
                data.Add(names[i], new FieldValue { Binary = new RegionAssetBinary(open, offset, lengths[i]) });
            offset += lengths[i];
        }

        return new AuthoringObject(schema, data);
    }

    /// <summary>アーティファクト内の領域を指す本体ハンドル</summary>
    private sealed class RegionAssetBinary(Func<Stream> open, long offset, long length) : IAssetBinary
    {
        public long Length => length;

        public Stream OpenRead()
        {
            Stream stream = open();
            stream.Seek(offset, SeekOrigin.Begin);
            return new BoundedReadStream(stream, length);
        }
    }

    /// <summary>下位ストリームの現在位置から最大 <c>length</c> バイトだけを公開する読み取り専用ストリーム</summary>
    private sealed class BoundedReadStream(Stream inner, long length) : Stream
    {
        private readonly long _length = length;
        private long _remaining = length;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _length;

        public override long Position
        {
            get => _length - _remaining;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_remaining <= 0) return 0;
            if (buffer.Length > _remaining) buffer = buffer[..(int)_remaining];
            int read = inner.Read(buffer);
            _remaining -= read;
            return read;
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
