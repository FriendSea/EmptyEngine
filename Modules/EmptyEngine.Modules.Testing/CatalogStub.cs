using EmptyEngine.Serialization;
using EmptyEngine.Tests;
using System.Collections.Concurrent;
using System.Reflection;
using EmptyEngine.Core;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Authoring;
using EmptyEngine.Editor.Catalog;
using EmptyEngine.Serialization.Editor;
using EmptyEngine.ObjectModel;

namespace EmptyEngine.Modules.Testing;

/// <summary>ビルド時カタログの代役</summary>
internal static class CatalogStub
{
    private static readonly ConcurrentDictionary<string, Entry> Entries = new(StringComparer.Ordinal);

    /// <summary>この代役カタログを引く窓口（テストが復元器やインポータへ渡す）</summary>
    public static ISchemaSource Schemas { get; } = new Source();

    /// <summary>型 <typeparamref name="T"/> のカタログへの登録</summary>
    public static void Register<T>() where T : new()
    {
        Type type = typeof(T);
        string typeName = type.FullName!;

        // 値の雛形は生成された手順書に書かせる（本番のカタログの default と同じ経路）。
        ObjectSchema schema = Schema(type);
        var template = FieldValueCodec.DecodeBytes(TypeSerializers.Serialize(type, new T()), schema.Root);

        Entries[typeName] = new Entry(schema, template);
    }

    /// <summary>スキーマに載せるメンバの引き当て</summary>
    /// <remarks>テスト用のメンバ検索に使うスキーマを返す。メンバの順序とシリアライズ対象の判定には使わない。</remarks>
    public static ObjectSchema Schema(Type type)
    {
        var binaries = new List<string>();
        return new ObjectSchema
        {
            TypeName = type.FullName!, DisplayName = type.Name,
            Fields = FindFields(type, binaries: binaries), BinaryFields = binaries,
            AssignableTypeNames = FindAssignableTypeNames(type),
        };
    }

    private static Dictionary<string, FieldTypeInfo> FindFields(Type type, int depth = 0, List<string>? binaries = null)
    {
        var result = new Dictionary<string, FieldTypeInfo>(StringComparer.Ordinal);
        if (depth >= 8)
            return result;

        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly;
        for (Type? t = type; t is not null && t != typeof(object); t = t.BaseType)
        {
            foreach (FieldInfo field in t.GetFields(Flags))
            {
                if (!field.IsInitOnly && !result.ContainsKey(field.Name))
                    Add(field.Name, field.FieldType);
            }

            foreach (PropertyInfo property in t.GetProperties(Flags))
            {
                if (property is { GetMethod.IsPublic: true, SetMethod.IsPublic: true }
                    && property.GetIndexParameters().Length == 0
                    && !result.ContainsKey(property.Name))
                {
                    Add(property.Name, property.PropertyType);
                }
            }
        }

        return result;

        void Add(string name, Type memberType)
        {
            FieldTypeInfo described = Describe(memberType, depth);
            result[name] = described;
            if (described.Kind == FieldKind.Binary) binaries?.Add(name);
        }
    }

    public static FieldTypeInfo Describe(Type fieldType, int depth = 0)
    {
        if (typeof(IAssetBinary).IsAssignableFrom(fieldType))
            return new FieldTypeInfo(typeof(IAssetBinary).FullName!, FieldKind.Binary);
        if (IsAssetReference(fieldType))
            return new FieldTypeInfo(
                fieldType == typeof(AssetReference<IObject>) ? TypeKind.SceneTarget : ConstraintName(fieldType),
                FieldKind.AssetReference);
        if (IsObjectReference(fieldType))
            return new FieldTypeInfo(ConstraintName(fieldType), FieldKind.ObjectReference);

        string name = fieldType.FullName ?? fieldType.Name;

        if (fieldType.IsEnum)
            return new FieldTypeInfo(name, FieldKind.Enum) { EnumMembers = EnumMembersOf(fieldType) };

        if (fieldType.IsArray && fieldType.GetArrayRank() == 1 && fieldType.GetElementType() is { } elementType)
            return new FieldTypeInfo(name, FieldKind.Array) { Element = Describe(elementType, depth + 1) };

        if (fieldType.IsValueType && !fieldType.IsPrimitive && fieldType != typeof(decimal))
        {
            return new FieldTypeInfo(name, FieldKind.Map)
            {
                Members = FindFields(fieldType, depth + 1),
                ColorChannels = ColorChannelsOf(fieldType),
            };
        }

        FieldTypeInfo scalar = TestSchemas.Scalar(name);
        return scalar.Kind == FieldKind.Map ? scalar with { Members = FindFields(fieldType, depth + 1) } : scalar;
    }

    /// <summary><c>[ColorChannels]</c> が名乗る RGBA メンバ名（本番は CatalogTool が同じものを刻む）</summary>
    private static IReadOnlyList<string> ColorChannelsOf(Type type) =>
        type.GetCustomAttribute<ColorChannelsAttribute>() is { } channels
            ? [channels.Red, channels.Green, channels.Blue, channels.Alpha]
            : [];

    private static bool IsAssetReference(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(AssetReference<>);

    private static bool IsObjectReference(Type type) =>
        type == typeof(ObjectReference)
        || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ComponentReference<>));

    private static string ConstraintName(Type referenceType) =>
        referenceType.IsGenericType
            ? referenceType.GetGenericArguments()[0].FullName ?? string.Empty
            : string.Empty;

    private static List<FieldEnumMember> EnumMembersOf(Type enumType)
    {
        var members = new List<FieldEnumMember>();
        foreach (object? value in Enum.GetValues(enumType))
        {
            if (value is null)
                continue;
            members.Add(new FieldEnumMember(Enum.GetName(enumType, value) ?? string.Empty, Convert.ToInt64(value)));
        }

        return members;
    }

    private static List<string> FindAssignableTypeNames(Type type)
    {
        var names = new List<string>();
        for (Type? current = type; current is not null && current != typeof(object); current = current.BaseType)
        {
            if (current.FullName is { } name) names.Add(name);
        }

        foreach (Type contract in type.GetInterfaces())
        {
            if (contract.FullName is { } name) names.Add(name);
        }

        return names;
    }

    private sealed class Source : ISchemaSource
    {
        public ObjectSchema? Find(string typeName) =>
            Entries.TryGetValue(typeName, out Entry? entry) ? entry.Schema
                : AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(typeName)).OfType<Type>().FirstOrDefault() is { } type
                    ? Schema(type) : null;

        public AuthoringObject? CreateDefault(string typeName) =>
            Entries.TryGetValue(typeName, out Entry? entry)
                ? new AuthoringObject(entry.Schema, entry.Template.Clone())
                : null;
    }

    private sealed record Entry(ObjectSchema Schema, FieldValue Template);
}
