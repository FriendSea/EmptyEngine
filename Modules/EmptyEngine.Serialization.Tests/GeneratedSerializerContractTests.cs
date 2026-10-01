using EmptyEngine.Modules.Testing;
using System.Buffers;
using EmptyEngine.Serialization.Generated;
using MessagePack;
using Xunit;

namespace EmptyEngine.Serialization.Tests;

/// <summary>生成シリアライザが契約に含まれるメンバを過不足なく読み書きすることを検証する</summary>
/// <remarks>公開フィールドと自動プロパティを対象とし、本体データと代入できないメンバは除外する。基底型のメンバも対象に含める。</remarks>
public sealed class GeneratedSerializerContractTests
{
    [Theory]
    [InlineData(typeof(TestSprite), "Asset")]
    [InlineData(typeof(TestUnmarkedAssetReferenceHolder), "Asset")]
    [InlineData(typeof(TestSpriteHolder), "idle", "run")]
    [InlineData(typeof(TestArrayHolder), "Targets", "Speeds", "Stages")]
    [InlineData(typeof(TestNestedHolder), "Offset", "Tint")]
    [InlineData(typeof(TestModeHolder), "Mode", "Target")]
    // Position は Matrix から計算するだけ＝backing field を持たないので契約に載らない
    [InlineData(typeof(TestNumerics), "Matrix")]
    // 拾うのは public フィールドと auto-property だけ。readonly / static / private フィールド、
    // 計算プロパティ、外から代入できない get-only・init・private setter は落ちる
    [InlineData(typeof(TestMemberProbe), "PublicField", "AutoProperty")]
    // 基底クラスのメンバも拾う（派生が先）
    [InlineData(typeof(TestDerivedMemberProbe), "DerivedValue", "PublicField", "AutoProperty")]
    // 本体（IAssetBinary）はメンバではなく blob の別区画へ行く
    [InlineData(typeof(TestBinaryProbe), "Length")]
    public void Generated_serializers_write_exactly_the_declared_members(Type type, params string[] expected)
        => Assert.Equal(expected.Order(), WrittenKeys(GeneratedSerializers.ByName[type.FullName!]).Order());

    [Fact]
    public void A_nil_in_place_of_a_value_type_member_reads_as_the_default()
    {
        ITypeSerializer serializer = GeneratedSerializers.ByName[typeof(TestSprite).FullName!];
        string key = nameof(TestSprite.Asset);

        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        writer.WriteMapHeader(1);
        writer.Write(key);
        writer.WriteNil();
        writer.Flush();
        byte[] wire = buffer.WrittenSpan.ToArray();

        object restored = serializer.CreateUninitialized();
        var reader = new MessagePackReader(wire);
        serializer.Read(ref reader, restored, TypeSerializers.Options);

        Assert.True(((TestSprite)restored).Asset.IsEmpty);
    }

    [Fact]
    public void Inherited_fields_and_auto_properties_round_trip_with_nondefault_values()
    {
        var probe = new TestDerivedMemberProbe { PublicField = 3, AutoProperty = 42, DerivedValue = 7 };

        var restored = (TestDerivedMemberProbe)TypeSerializers.Deserialize(
            typeof(TestDerivedMemberProbe), TypeSerializers.Serialize(typeof(TestDerivedMemberProbe), probe));

        Assert.Equal(42, restored.AutoProperty);
        Assert.Equal(3, restored.PublicField);
        Assert.Equal(7, restored.DerivedValue);
    }

    [Fact]
    public void Asset_attribute_is_a_serializer_root_without_reference_discovery()
    {
        var generated = GeneratedSerializers.ByName.Values.Select(s => s.Type).ToHashSet();

        Assert.Contains(typeof(TestStandaloneAsset), generated);
        Assert.DoesNotContain(typeof(TestUnmarkedAsset), generated);
    }

    /// <summary>手順書が実際に書き出したメンバキー（宣言順）</summary>
    private static List<string> WrittenKeys(ITypeSerializer serializer)
    {
        var reader = new MessagePackReader(Write(serializer, serializer.CreateUninitialized()));
        int count = reader.ReadMapHeader();
        var keys = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            keys.Add(reader.ReadString() ?? string.Empty);
            reader.Skip();
        }

        return keys;
    }

    private static byte[] Write(ITypeSerializer serializer, object instance)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        serializer.Write(ref writer, instance, TypeSerializers.Options);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

}
