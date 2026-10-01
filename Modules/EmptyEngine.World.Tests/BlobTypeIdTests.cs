using EmptyEngine.Modules.Testing;
using EmptyEngine.Serialization;
using System.Buffers.Binary;
using MessagePack;
using Xunit;

namespace EmptyEngine.World.Tests;

/// <summary>blob の型自己記述の形の検証</summary>
public sealed class BlobTypeIdTests
{
    [Fact]
    public void Scene_blob_uses_only_the_component_type_id()
    {
        byte[] blob = SceneBlob.Encode([new RootInstanceData("inst",
            new ObjectData("obj", "o", [new ComponentData(
                typeof(TestTransform).FullName!, new TestTransform { X = 1f })], []))]);

        RootInstanceData decoded = Assert.Single(SceneBlob.Decode(blob));
        ComponentData component = Assert.Single(decoded.Root.Components);

        Assert.Equal(1f, Assert.IsType<TestTransform>(component.Component).X);
    }

    [Fact]
    public void Asset_blob_uses_a_three_element_envelope()
    {
        using var blob = new MemoryStream();
        AssetBlob.SerializeTo(blob, new TestAsset("hello"));

        byte[] bytes = blob.ToArray();
        int envelopeLength = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        var reader = new MessagePackReader(bytes.AsMemory(sizeof(int), envelopeLength));

        Assert.Equal(3, reader.ReadArrayHeader());
        Assert.Equal(typeof(TestAsset).FullName, reader.ReadString());
        reader.Skip();
        reader.Skip();
    }
}
