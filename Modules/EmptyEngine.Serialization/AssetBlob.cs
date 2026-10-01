using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using EmptyEngine.Core;
using EmptyEngine.Storage;
using MessagePack;

namespace EmptyEngine.Serialization;

/// <summary>ランタイムが読むアセットアーティファクトのバイナリ形式</summary>
public static class AssetBlob
{
    private const int MaxEnvelopeLength = 64 * 1024 * 1024;
    private const int MaxBinaryCount = 65_536;
    private static readonly MessagePackSerializerOptions Options = TypeSerializers.Options;

    /// <summary>アセットのアーティファクトへの書き出し</summary>
    public static void SerializeTo(Stream destination, object asset)
    {
        Type type = asset.GetType();

        ITypeSerializer serializer = TypeSerializers.For(type);
        var metadataBuffer = new ArrayBufferWriter<byte>();
        var metadataWriter = new MessagePackWriter(metadataBuffer);
        serializer.Write(ref metadataWriter, asset, Options);
        metadataWriter.Flush();
        byte[] metadata = metadataBuffer.WrittenSpan.ToArray();

        var binaries = new IAssetBinary?[serializer.BinaryCount];
        for (int i = 0; i < binaries.Length; i++)
            binaries[i] = serializer.GetBinary(asset, i);

        var lengths = new long[binaries.Length];
        for (int i = 0; i < binaries.Length; i++)
            lengths[i] = binaries[i]?.Length ?? 0;

        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        writer.WriteArrayHeader(3);
        writer.Write(serializer.TypeName);
        writer.Write(metadata);
        writer.WriteArrayHeader(lengths.Length);
        foreach (long length in lengths)
        {
            writer.Write(length);
        }

        writer.Flush();

        ReadOnlySpan<byte> envelope = buffer.WrittenSpan;
        Span<byte> header = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, envelope.Length);
        destination.Write(header);
        destination.Write(envelope);

        foreach (IAssetBinary? handle in binaries)
        {
            if (handle is null)
            {
                continue;
            }

            using Stream source = handle.OpenRead();
            CopyWithoutManagedArray(source, destination);
        }
    }

    /// <summary>封筒だけを読んだアセットの復元</summary>
    /// <param name="artifact"><paramref name="store"/> から読み出したキーのアーティファクト</param>
    /// <param name="services">アセット実体の ctor 引数を解決するコンテナ。<c>null</c> なら引数なし ctor だけが通る。</param>
    public static object? Deserialize(AssetStorage store, string key, Stream artifact, IServiceProvider? services = null)
    {
        if (ReadEnvelope(artifact) is not { } envelope)
        {
            return null;
        }

        object? restored = envelope.Restore();
        if (restored is null)
        {
            return null;
        }

        object asset = InstanceActivator.Create(restored, services);
        envelope.AssignBinaries(store, key, asset);
        return asset;
    }

    /// <summary>既存のインスタンスへのアーティファクト内容の適用</summary>
    /// <remarks>シリアライズ対象のメンバと本体データ（<see cref="IAssetBinary"/>）を更新し、それ以外の状態を維持する。</remarks>
    public static bool UpdateInto(object target, AssetStorage store, string key, Stream artifact)
    {
        if (ReadEnvelope(artifact) is not { } envelope)
        {
            return false;
        }

        object? restored = envelope.Restore();
        if (restored is null || restored.GetType() != target.GetType())
        {
            return false;
        }

        InstanceActivator.CopySerializedMembers(target, restored);
        envelope.AssignBinaries(store, key, target);
        return true;
    }

    /// <summary>封筒だけの読み取り</summary>
    private static Envelope? ReadEnvelope(Stream file)
    {
        Span<byte> header = stackalloc byte[sizeof(int)];
        file.ReadExactly(header);
        int envelopeLength = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (envelopeLength <= 0)
        {
            return null;
        }
        ValidateEnvelopeLength(file, envelopeLength);

        byte[] envelope = new byte[envelopeLength];
        file.ReadExactly(envelope);

        var reader = new MessagePackReader(envelope);
        int envelopeCount = reader.ReadArrayHeader();
        if (envelopeCount < 3)
        {
            return null;
        }

        string? typeName = reader.ReadString();
        ReadOnlySequence<byte>? metadata = reader.ReadBytes();
        int binaryCount = reader.ReadArrayHeader();
        if (binaryCount > MaxBinaryCount)
            throw new InvalidDataException($"Asset declares {binaryCount} binary regions; the limit is {MaxBinaryCount}.");

        var lengths = new long[binaryCount];
        long binaryLength = 0;
        for (int i = 0; i < binaryCount; i++)
        {
            long length = reader.ReadInt64();
            if (length < 0) throw new InvalidDataException($"Asset binary region {i} has a negative length.");
            lengths[i] = length;
            binaryLength = checked(binaryLength + length);
        }

        if (binaryLength > file.Length - file.Position)
            throw new InvalidDataException("Asset binary regions extend beyond the artifact stream.");

        if (typeName is null || metadata is null)
        {
            return null;
        }

        return new Envelope(TypeSerializers.For(typeName), metadata.Value, lengths, envelopeLength);
    }

    private static void ValidateEnvelopeLength(Stream file, int envelopeLength)
    {
        if (envelopeLength > MaxEnvelopeLength)
            throw new InvalidDataException($"Asset envelope length {envelopeLength} exceeds {MaxEnvelopeLength} bytes.");
        if (!file.CanSeek || envelopeLength > file.Length - file.Position)
            throw new InvalidDataException("Asset envelope extends beyond the artifact stream.");
    }

    /// <summary>読み終えた封筒</summary>
    private readonly struct Envelope(
        ITypeSerializer serializer, ReadOnlySequence<byte> metadata, long[] lengths, int length)
    {
        /// <summary>メタデータからの「値の入れ物」の復元</summary>
        public object? Restore()
        {
            object instance = serializer.CreateUninitialized();
            var reader = new MessagePackReader(metadata);
            serializer.Read(ref reader, instance, Options);
            return instance;
        }

        /// <summary>本体（封筒の直後に宣言順で連結）の領域参照を割り当てる</summary>
        public void AssignBinaries(AssetStorage store, string key, object asset)
        {
            long offset = sizeof(int) + length;
            int assignable = Math.Min(serializer.BinaryCount, lengths.Length);
            for (int i = 0; i < assignable; i++)
            {
                var region = new StoreRegionAssetBinary(store, key, offset, lengths[i]);
                serializer.SetBinary(asset, i, region);

                offset += lengths[i];
            }
        }
    }

    /// <summary>ストリーム間コピー</summary>
    private static unsafe void CopyWithoutManagedArray(Stream source, Stream destination)
    {
        const int ChunkSize = 1 << 16;
        byte* scratch = (byte*)NativeMemory.Alloc(ChunkSize);
        try
        {
            var chunk = new Span<byte>(scratch, ChunkSize);
            int read;
            while ((read = source.Read(chunk)) > 0)
            {
                destination.Write(chunk[..read]);
            }
        }
        finally
        {
            NativeMemory.Free(scratch);
        }
    }
}
