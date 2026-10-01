using EmptyEngine.Modules.Testing;
using System.Buffers;
using System.Buffers.Binary;
using EmptyEngine.Serialization.Editor;
using EmptyEngine.Storage;
using MessagePack;
using Xunit;

namespace EmptyEngine.Serialization.Tests;

public sealed class ArtifactLengthValidationTests
{
    [Fact]
    public void Oversized_envelope_is_rejected_before_allocation()
    {
        byte[] artifact = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(artifact, 64 * 1024 * 1024 + 1);
        using AssetStorage store = AssetStorage.InMemory();
        store.Add("asset", artifact);

        Assert.Throws<InvalidDataException>(() => store.DeserializeNow("asset"));
        Assert.Throws<InvalidDataException>(() => AuthoringAssetCodec.Decode(() => new MemoryStream(artifact), CatalogStub.Schemas));
    }

    [Fact]
    public void Binary_region_must_fit_inside_the_artifact()
    {
        byte[] artifact = ArtifactDeclaringBinary(length: 1);
        using AssetStorage store = AssetStorage.InMemory();
        store.Add("asset", artifact);

        Assert.Throws<InvalidDataException>(() => store.DeserializeNow("asset"));
        Assert.Throws<InvalidDataException>(() => AuthoringAssetCodec.Decode(() => new MemoryStream(artifact), CatalogStub.Schemas));
    }

    private static byte[] ArtifactDeclaringBinary(long length)
    {
        var envelope = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(envelope);
        writer.WriteArrayHeader(3);
        writer.Write("unused");
        writer.Write(Array.Empty<byte>());
        writer.WriteArrayHeader(1);
        writer.Write(length);
        writer.Flush();

        byte[] artifact = new byte[sizeof(int) + envelope.WrittenCount];
        BinaryPrimitives.WriteInt32LittleEndian(artifact, envelope.WrittenCount);
        envelope.WrittenSpan.CopyTo(artifact.AsSpan(sizeof(int)));
        return artifact;
    }
}
