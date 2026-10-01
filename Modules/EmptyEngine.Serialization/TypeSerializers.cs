using System.Buffers;
using EmptyEngine.Core;
using MessagePack;

namespace EmptyEngine.Serialization;

/// <summary>1 つの型のシリアライズ・復元・複製を提供する契約</summary>
public interface ITypeSerializer
{
    /// <summary>シリアライズの対象となる型</summary>
    Type Type { get; }

    /// <summary>ワイヤ（blob）に載る型名</summary>
    string TypeName { get; }

    /// <summary><see cref="Read"/> の受け皿の生成</summary>
    object CreateUninitialized();

    /// <summary>map の読み取りと <paramref name="instance"/> のメンバへの流し込み</summary>
    void Read(ref MessagePackReader reader, object instance, MessagePackSerializerOptions options);

    /// <summary>シリアライズ対象メンバの map としての書き出し</summary>
    void Write(ref MessagePackWriter writer, object instance, MessagePackSerializerOptions options);

    /// <summary>指定したサービスから依存関係を解決し、新しいインスタンスを作成する</summary>
    object Construct(IServiceProvider services);

    /// <summary>シリアライズ対象メンバをそのまま写す（参照先は複製元と共有する）</summary>
    void CopyShallow(object destination, object source);

    /// <summary>シリアライズ対象メンバを、複製元から独立した値として複製する</summary>
    void CopyDeep(object destination, object source);

    /// <summary>本体（<see cref="IAssetBinary"/> 型のメンバ）の数</summary>
    int BinaryCount => 0;

    /// <summary>宣言順 <paramref name="index"/> 番目の本体の取り出し</summary>
    IAssetBinary? GetBinary(object instance, int index)
        => throw new NotSupportedException($"'{TypeName}' has no asset binary members.");

    /// <summary>宣言順 <paramref name="index"/> 番目の本体へ領域参照を割り当てる</summary>
    void SetBinary(object instance, int index, IAssetBinary? value)
        => throw new NotSupportedException($"'{TypeName}' has no asset binary members.");
}

/// <summary>ワイヤ上の型名から <see cref="ITypeSerializer"/> への表</summary>
public static class TypeSerializers
{
    /// <summary>シリアライズに使う標準オプション</summary>
    public static readonly MessagePackSerializerOptions Options = MessagePackSerializerOptions.Standard;

    private static readonly object Gate = new();

    private static IReadOnlyDictionary<string, ITypeSerializer> _byName =
        new Dictionary<string, ITypeSerializer>(StringComparer.Ordinal);

    /// <summary>生成された表の据え付け</summary>
    /// <remarks>同じ型の重複登録は許容するが、同じ型 id を異なる型へ割り当てることはできない。</remarks>
    public static void Install(IReadOnlyDictionary<string, ITypeSerializer> serializers)
    {
        lock (Gate)
        {
            if (_byName.Count == 0)
            {
                _byName = serializers;
                return;
            }

            var merged = new Dictionary<string, ITypeSerializer>(_byName, StringComparer.Ordinal);
            foreach (KeyValuePair<string, ITypeSerializer> pair in serializers)
            {
                if (merged.TryGetValue(pair.Key, out ITypeSerializer? existing) && existing.Type != pair.Value.Type)
                    throw new InvalidOperationException(
                        $"Duplicate serializer type id '{pair.Key}' for '{existing.Type}' and '{pair.Value.Type}'.");
                merged[pair.Key] = pair.Value;
            }

            _byName = merged;
        }
    }

    /// <summary>ワイヤ上の型名での引き当て</summary>
    /// <returns>手順書が無ければ <c>null</c></returns>
    public static ITypeSerializer? Lookup(string typeName)
        => _byName.TryGetValue(typeName, out ITypeSerializer? serializer) ? serializer : null;

    /// <summary>ワイヤ上の型名で生成シリアライザを引き当てる</summary>
    /// <exception cref="InvalidOperationException">この型の生成シリアライザが登録されていないとき</exception>
    public static ITypeSerializer For(string typeName)
        => Lookup(typeName) ?? throw Missing(typeName);

    /// <summary>CLR 型での引き当て（ワイヤ上の型名は <see cref="Type.FullName"/>）</summary>
    public static ITypeSerializer For(Type type) => For(type.FullName ?? type.Name);

    /// <summary>値 1 個の msgpack への書き出し</summary>
    public static byte[] Serialize(Type type, object instance)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        For(type).Write(ref writer, instance, Options);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>値 1 個の msgpack からの読み戻し</summary>
    public static object Deserialize(Type type, ReadOnlyMemory<byte> bytes)
    {
        ITypeSerializer serializer = For(type);
        object instance = serializer.CreateUninitialized();
        var reader = new MessagePackReader(bytes);
        serializer.Read(ref reader, instance, Options);
        return instance;
    }

    private static InvalidOperationException Missing(string typeName)
        => new($"No generated serializer for '{typeName}'. This host runs on generated serializers, so falling back " +
               "to reflection is not allowed (a trimmed build would lose this type's members). Make sure the " +
               "serializer generator covers it — serializable roots must implement IAttachable or have AssetAttribute.");
}
