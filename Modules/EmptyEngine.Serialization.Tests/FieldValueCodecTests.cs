using EmptyEngine.Modules.Testing;
using System.Numerics;
using EmptyEngine.Core;
using EmptyEngine.ObjectModel;
using EmptyEngine.Editor.Authoring;
using EmptyEngine.Serialization.Editor;
using MessagePack;
using Xunit;

namespace EmptyEngine.Serialization.Tests;

/// <summary><see cref="FieldValueCodec"/> の中核契約の検証</summary>
public sealed class FieldValueCodecTests
{
    [Asset]
    public sealed class CodecProbe
    {
        public int Count;
        public AssetReference<TestAsset> Texture;
        public Matrix4x4 Matrix;
    }

    [Fact]
    public void Schema_driven_round_trip_preserves_integer_extremes_float_width_and_binary()
    {
        var type = new FieldTypeInfo("Probe", FieldKind.Map)
        {
            Members = new Dictionary<string, FieldTypeInfo>
            {
                ["Signed"] = new("System.Int64", FieldKind.Int), ["Unsigned"] = new("System.UInt64", FieldKind.UInt),
                ["Single"] = new("System.Single", FieldKind.Float32), ["Double"] = new("System.Double", FieldKind.Float64),
                ["Bytes"] = new("System.Byte[]", FieldKind.Array) { Element = new("System.Byte", FieldKind.UInt) },
                ["Flag"] = new("System.Boolean", FieldKind.Bool),
                ["Text"] = new("System.String", FieldKind.String),
                ["Values"] = new("System.String[]", FieldKind.Array) { Element = new("System.String", FieldKind.String) },
            },
        };
        var tree = new FieldValue();
        tree.Add("Signed", new FieldValue { Integer = long.MinValue });
        tree.Add("Unsigned", new FieldValue { Unsigned = ulong.MaxValue });
        tree.Add("Single", new FieldValue { Real = 1.25f });
        tree.Add("Double", new FieldValue { Real = Math.PI });
        tree.Add("Bytes", new FieldValue
        {
            Items = { new FieldValue { Unsigned = 1 }, new FieldValue { Unsigned = 2 }, new FieldValue { Unsigned = 3 } },
        });
        tree.Add("Flag", new FieldValue { Bool = true });
        tree.Add("Text", new FieldValue { Text = "text" });
        tree.Add("Values", new FieldValue { Items = { FieldValue.Nil(), new FieldValue { Text = "item" } } });
        byte[] encoded = FieldValueCodec.EncodeBytes(tree, type);
        FieldValue decoded = FieldValueCodec.DecodeBytes(encoded, type);
        Assert.Equal(long.MinValue, decoded.Get("Signed")!.Integer);
        Assert.Equal(ulong.MaxValue, decoded.Get("Unsigned")!.Unsigned);
        Assert.Equal(Math.PI, decoded.Get("Double")!.Real);
        Assert.Equal(3, decoded.Get("Bytes")!.Items.Count);
        Assert.Equal(2UL, decoded.Get("Bytes")!.Items[1].Unsigned);
        Assert.True(decoded.Get("Flag")!.Bool);
        Assert.True(decoded.Get("Values")!.Items[0].IsNull);
        Assert.Equal(encoded, FieldValueCodec.EncodeBytes(decoded.Clone(), type));
    }

    private static T TreeRoundTrip<T>(T value)
    {
        byte[] original = TypeSerializers.Serialize(typeof(T), value!);
        FieldTypeInfo type = CatalogStub.Schema(typeof(T)).Root;
        FieldValue tree = FieldValueCodec.DecodeBytes(original, type);
        byte[] reencoded = FieldValueCodec.EncodeBytes(tree, type);
        return (T)TypeSerializers.Deserialize(typeof(T), reencoded);
    }

    [Fact]
    public void Nondefault_integer_reference_and_matrix_values_survive_the_authoring_codec()
    {
        var matrix = Matrix4x4.CreateScale(2, 3, 4) * Matrix4x4.CreateTranslation(3, 4, 5);
        var restored = TreeRoundTrip(new CodecProbe
        {
            Count = -7,
            Texture = new AssetReference<TestAsset>("tex-a.png"),
            Matrix = matrix,
        });
        Assert.Equal(-7, restored.Count);
        Assert.Equal("tex-a.png", restored.Texture.AssetKey);
        Assert.Equal(matrix, restored.Matrix);
    }

    [Fact]
    public void Edited_leaf_reserializes_to_new_value()
    {
        byte[] data = TypeSerializers.Serialize(typeof(TestTransform), new TestTransform { X = 1f, Y = 2f, Z = 3f });
        FieldTypeInfo type = CatalogStub.Schema(typeof(TestTransform)).Root;
        var map = FieldValueCodec.DecodeBytes(data, type);

        map.Get("Y")!.Real = 9.5;

        byte[] reencoded = FieldValueCodec.EncodeBytes(map, type);
        var back = (TestTransform)TypeSerializers.Deserialize(typeof(TestTransform), reencoded);
        Assert.Equal(1f, back.X);
        Assert.Equal(9.5f, back.Y);
        Assert.Equal(3f, back.Z);
    }

    [Fact]
    public void Json_integer_tokens_are_normalized_to_the_declared_float()
    {
        var type = new FieldTypeInfo("System.Single", FieldKind.Float32);
        using var json = System.Text.Json.JsonDocument.Parse("8");
        FieldValue value = FieldJson.Decode(json.RootElement, type);
        var reader = new MessagePackReader(FieldValueCodec.EncodeBytes(value, type));
        Assert.Equal(MessagePackCode.Float32, reader.NextCode);
        Assert.Equal(8f, reader.ReadSingle());
    }

    [Fact]
    public void Enums_are_written_as_integers()
    {
        var type = new FieldTypeInfo("Tests.Mode", FieldKind.Enum);
        var reader = new MessagePackReader(FieldValueCodec.EncodeBytes(new FieldValue { Integer = 2 }, type));
        Assert.Equal(2, reader.ReadInt32());
    }

    [Theory]
    [InlineData("8.0", 8L)]
    [InlineData("8.5", 8L)]
    [InlineData("-8.5", -8L)]
    public void Fractional_json_values_declared_as_integers_are_truncated(string json, long expected)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(expected, FieldJson.Decode(document.RootElement, new("System.Int32", FieldKind.Int)).Integer);
    }

    [Fact]
    public void Out_of_range_integer_values_are_rejected()
    {
        var type = new FieldTypeInfo("System.Int32", FieldKind.Int);
        Assert.Throws<MessagePackSerializationException>(() => FieldValueCodec.EncodeBytes(new FieldValue { Integer = (long)int.MaxValue + 1 }, type));
    }

    [Fact]
    public void Undeclared_fields_are_dropped_on_decode_but_rejected_on_encode()
    {
        var type = new FieldTypeInfo("Probe", FieldKind.Map) { Members = new Dictionary<string, FieldTypeInfo>() };
        using var json = System.Text.Json.JsonDocument.Parse("{\"Missing\":1}");
        Assert.Empty(FieldJson.Decode(json.RootElement, type).Entries);

        var value = new FieldValue();
        value.Add("Missing", new FieldValue { Integer = 1 });
        Assert.Throws<InvalidDataException>(() => FieldValueCodec.EncodeBytes(value, type));
    }

    [Fact]
    public void Undeclared_fields_are_skipped_without_derailing_the_msgpack_stream()
    {
        var wide = new FieldTypeInfo("Probe", FieldKind.Map)
        {
            Members = new Dictionary<string, FieldTypeInfo>
            {
                ["Before"] = new("System.Int32", FieldKind.Int),
                ["Stale"] = new("System.String[]", FieldKind.Array) { Element = new("System.String", FieldKind.String) },
                ["After"] = new("System.String", FieldKind.String),
            },
        };
        var narrow = new FieldTypeInfo("Probe", FieldKind.Map)
        {
            Members = new Dictionary<string, FieldTypeInfo>
            {
                ["Before"] = new("System.Int32", FieldKind.Int),
                ["After"] = new("System.String", FieldKind.String),
            },
        };
        var tree = new FieldValue();
        tree.Add("Before", new FieldValue { Integer = 7 });
        tree.Add("Stale", new FieldValue { Items = { new FieldValue { Text = "a" }, new FieldValue { Text = "b" } } });
        tree.Add("After", new FieldValue { Text = "kept" });

        FieldValue decoded = FieldValueCodec.DecodeBytes(FieldValueCodec.EncodeBytes(tree, wide), narrow);

        Assert.Null(decoded.Get("Stale"));
        Assert.Equal(7, decoded.Get("Before")!.Integer);
        Assert.Equal("kept", decoded.Get("After")!.Text);
    }

    [Fact]
    public void Union_selection_and_unit_payload_survive_both_formats()
    {
        var type = new FieldTypeInfo("Maybe", FieldKind.Union)
        {
            Members = new Dictionary<string, FieldTypeInfo>
            {
                ["None"] = new("None", FieldKind.Nil), ["Some"] = new("System.UInt64", FieldKind.UInt),
            },
        };
        foreach (string source in new[] { "{\"None\":null}", "{\"Some\":18446744073709551615}" })
        {
            using var json = System.Text.Json.JsonDocument.Parse(source);
            FieldValue value = FieldJson.Decode(json.RootElement, type);
            FieldValue copy = FieldValueCodec.DecodeBytes(FieldValueCodec.EncodeBytes(value, type), type);
            Assert.Equal(source, FieldJson.Encode(copy, type)!.ToJsonString());
        }
    }
}
