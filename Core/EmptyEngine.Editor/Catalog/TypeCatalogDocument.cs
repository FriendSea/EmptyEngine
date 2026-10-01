using System.Text.Json;
using EmptyEngine.Editor.Authoring;
using Microsoft.Extensions.Logging;

namespace EmptyEngine.Editor.Catalog;

/// <summary>型カタログ（<c>.artifacts/TypeCatalog.json</c>）を読んだもの</summary>
public sealed class TypeCatalogDocument
{
    /// <summary>型記述を引く <c>$ref</c> の前置き</summary>
    private const string DefsPointer = "#/$defs/";

    private TypeCatalogDocument(IReadOnlyList<TypeCatalogEntry> types) => Types = types;

    /// <summary>型記述子（ファイル上の順序のまま＝「Add Component」メニュー順）</summary>
    public IReadOnlyList<TypeCatalogEntry> Types { get; }

    /// <summary>カタログの読み取り</summary>
    /// <param name="schemaFor">読み取った宣言に対して、使うスキーマの実体を返す。省略すると宣言をそのまま使う。</param>
    public static TypeCatalogDocument Parse(
        ReadOnlyMemory<byte> utf8Json, ILogger logger, Func<ObjectSchema, ObjectSchema>? schemaFor = null)
    {
        using JsonDocument document = JsonDocument.Parse(utf8Json);
        JsonElement root = document.RootElement;

        var table = new TypeTable(root, logger, schemaFor);
        var types = new List<TypeCatalogEntry>();
        foreach (string id in table.Ids)
        {
            if (table.Entry(id) is { } parsed)
                types.Add(parsed);
        }

        return new TypeCatalogDocument(types);
    }

    internal static string? TextOf(JsonElement owner, string key) =>
        owner.ValueKind == JsonValueKind.Object
        && owner.TryGetProperty(key, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>スキーマ参照が指す型 id</summary>
    /// <remarks>JSON Pointer（RFC 6901）と URI 断片（RFC 3986）の規則だけを解き、型 id の中身には触らない</remarks>
    internal static string? RefTarget(JsonElement schema)
    {
        if (TextOf(schema, "$ref") is not { } reference || !reference.StartsWith(DefsPointer, StringComparison.Ordinal))
            return null;

        string token = Uri.UnescapeDataString(reference[DefsPointer.Length..]);
        return token.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
    }
}

/// <summary>種類（<c>x-kind</c>）の名前</summary>
public static class TypeKind
{
    public const string Float = "float";
    public const string Double = "double";
    public const string Int = "int";
    public const string UInt = "uint";
    public const string Bool = "bool";
    public const string String = "string";

    /// <summary>本体データ（<c>IAssetBinary</c>）</summary>
    public const string Binary = "binary";
    public const string Struct = "struct";
    public const string Array = "array";
    public const string Enum = "enum";

    /// <summary>タグ付きバリアント</summary>
    public const string Union = "union";
    public const string AssetRef = "assetRef";
    public const string ObjectRef = "objectRef";

    /// <summary>シーンアセットを指す <see cref="AssetRef"/> の参照先（<c>x-target</c>）</summary>
    /// <remarks>型 id としては現れない予約値</remarks>
    public const string SceneTarget = "@scene";
}

/// <summary>カタログ 1 型分</summary>
public sealed class TypeCatalogEntry
{
    private readonly FieldValue? _defaultData;

    internal TypeCatalogEntry(
        ObjectSchema schema,
        bool attachable,
        FieldValue? defaultData,
        TypeSourceLocation? sourceLocation)
    {
        Schema = schema;
        Attachable = attachable;
        _defaultData = defaultData;
        SourceLocation = sourceLocation;
    }

    public ObjectSchema Schema { get; }

    /// <summary>「Add Component」候補に出す型か</summary>
    public bool Attachable { get; }

    /// <summary>型を宣言または登録しているソース位置（カタログに有効な記述がなければ <c>null</c>）</summary>
    public TypeSourceLocation? SourceLocation { get; }

    /// <summary>カタログが既定値を持っている型か</summary>
    public bool HasDefault => _defaultData is not null;

    /// <summary>既定値ツリーの生成</summary>
    /// <remarks>既定値のない型には空の map を返す。</remarks>
    public FieldValue CreateDefaultData() => _defaultData?.Clone() ?? new FieldValue();

    /// <summary>既定値とスキーマから authoring 実体を起こす</summary>
    public AuthoringObject CreateAuthoringObject() =>
        new(Schema, CreateDefaultData());
}

/// <summary>型カタログに記録されたソースファイル上の位置</summary>
public sealed record TypeSourceLocation(string Path, int Line);

/// <summary><c>$defs</c> の表と参照解決</summary>
internal sealed class TypeTable
{
    private readonly ILogger _logger;
    private readonly Func<ObjectSchema, ObjectSchema> _schemaFor;
    private readonly Dictionary<string, JsonElement> _raw = new(StringComparer.Ordinal);
    private readonly List<string> _order = new();
    private readonly Dictionary<string, FieldTypeInfo?> _resolved = new(StringComparer.Ordinal);
    private readonly HashSet<string> _resolving = new(StringComparer.Ordinal);

    public TypeTable(JsonElement root, ILogger logger, Func<ObjectSchema, ObjectSchema>? schemaFor = null)
    {
        _logger = logger;
        _schemaFor = schemaFor ?? (declaration => declaration);

        if (!root.TryGetProperty("$defs", out JsonElement defs) || defs.ValueKind != JsonValueKind.Object)
            return;

        foreach (JsonProperty def in defs.EnumerateObject())
        {
            if (def.Name.Length == 0 || def.Value.ValueKind != JsonValueKind.Object || _raw.ContainsKey(def.Name))
                continue;

            _raw.Add(def.Name, def.Value);
            _order.Add(def.Name);
        }
    }

    /// <summary>ファイル上の順序の型 id（＝「Add Component」メニュー順）</summary>
    public IReadOnlyList<string> Ids => _order;

    public TypeCatalogEntry? Entry(string id)
    {
        if (!_raw.TryGetValue(id, out JsonElement raw))
            return null;

        FieldTypeInfo? spec = Resolve(id);
        FieldValue? defaultData = spec is null ? null : spec.Default ?? DefaultOf(raw, spec);

        ObjectSchema schema = _schemaFor(new ObjectSchema
        {
            TypeName = id,
            DisplayName = TypeCatalogDocument.TextOf(raw, "title") ?? id,
            Fields = spec?.Members is { } members
                ? members.ToDictionary(m => m.Key, m => m.Value, StringComparer.Ordinal)
                : new Dictionary<string, FieldTypeInfo>(StringComparer.Ordinal),
            BinaryFields = spec?.Binaries ?? [],
            AssignableTypeNames = ReadStrings(raw, "x-assignableTo"),
            Defaults = defaultData,
        });

        bool attachable = raw.TryGetProperty("x-attachable", out JsonElement flag) && flag.ValueKind == JsonValueKind.True;
        TypeSourceLocation? sourceLocation = ReadSourceLocation(raw);

        return new TypeCatalogEntry(schema, attachable, defaultData, sourceLocation);
    }

    private static FieldValue? DefaultOf(JsonElement raw, FieldTypeInfo spec) =>
        raw.TryGetProperty("default", out JsonElement tree) ? FieldJson.Decode(tree, spec) : null;

    private static TypeSourceLocation? ReadSourceLocation(JsonElement raw)
    {
        string? path = TypeCatalogDocument.TextOf(raw, "x-sourcePath");
        if (string.IsNullOrWhiteSpace(path)
            || !raw.TryGetProperty("x-sourceLine", out JsonElement lineValue)
            || lineValue.ValueKind != JsonValueKind.Number
            || !lineValue.TryGetInt32(out int line)
            || line <= 0)
        {
            return null;
        }

        return new TypeSourceLocation(path, line);
    }

    /// <summary>型 id の解決</summary>
    public FieldTypeInfo? Resolve(string id)
    {
        if (_resolved.TryGetValue(id, out FieldTypeInfo? cached))
            return cached;
        if (!_raw.TryGetValue(id, out JsonElement raw) || !_resolving.Add(id))
            return null;

        FieldTypeInfo? spec;
        try
        {
            spec = Build(id, raw);
        }
        finally
        {
            _resolving.Remove(id);
        }

        _resolved[id] = spec;
        return spec;
    }

    private FieldTypeInfo? Build(string id, JsonElement raw)
    {
        string kind = TypeCatalogDocument.TextOf(raw, "x-kind") ?? TypeKind.Struct;

        switch (kind)
        {
            case TypeKind.AssetRef:
            case TypeKind.ObjectRef:
            {
                string target = TypeCatalogDocument.TextOf(raw, "x-target") ?? string.Empty;
                FieldKind semantic = kind == TypeKind.AssetRef
                    ? FieldKind.AssetReference
                    : FieldKind.ObjectReference;
                return new FieldTypeInfo(target, semantic);
            }

            case TypeKind.Enum:
                return new FieldTypeInfo(id, FieldKind.Enum)
                {
                    EnumMembers = ReadEnumMembers(raw),
                };

            case TypeKind.Struct:
            case TypeKind.Union:
            {
                bool union = kind == TypeKind.Union;
                var binaries = new List<string>();
                Dictionary<string, FieldTypeInfo> members = ReadMembers(raw, binaries);
                var spec = new FieldTypeInfo(id, union ? FieldKind.Union : FieldKind.Map)
                {
                    Members = members,
                    Binaries = binaries,
                    ColorChannels = ReadStrings(raw, "x-color") is { Count: 4 } channels ? channels : [],
                };
                return union ? spec : spec with { Default = DefaultOf(raw, spec) };
            }

            case TypeKind.Array:
            {
                if (!raw.TryGetProperty("items", out JsonElement items)
                    || TypeCatalogDocument.RefTarget(items) is not { Length: > 0 } elementId
                    || Resolve(elementId) is not { } element)
                    throw new InvalidDataException($"Array '{id}' has no element schema.");
                return new FieldTypeInfo(id, FieldKind.Array) { Element = element };
            }

            default:
                return new FieldTypeInfo(id, kind switch
                {
                    TypeKind.Float => FieldKind.Float32, TypeKind.Double => FieldKind.Float64,
                    TypeKind.Int => FieldKind.Int, TypeKind.UInt => FieldKind.UInt,
                    TypeKind.Bool => FieldKind.Bool, TypeKind.String => FieldKind.String,
                    TypeKind.Binary => FieldKind.Binary,
                    _ => throw new InvalidDataException($"Unsupported schema kind '{kind}' for '{id}'."),
                });
        }
    }

    /// <summary>名前付きメンバを、名前と型の対応表に変換する</summary>
    /// <param name="binaries">本体フィールド名の格納先。文書上の順序で追加する。</param>
    private Dictionary<string, FieldTypeInfo> ReadMembers(JsonElement owner, List<string> binaries)
    {
        var result = new Dictionary<string, FieldTypeInfo>(StringComparer.Ordinal);
        if (!owner.TryGetProperty("properties", out JsonElement properties) || properties.ValueKind != JsonValueKind.Object)
            return result;

        foreach (JsonProperty member in properties.EnumerateObject())
        {
            if (member.Name.Length == 0 || result.ContainsKey(member.Name))
                continue;
            if (member.Value.ValueKind == JsonValueKind.True
                && TypeCatalogDocument.TextOf(owner, "x-kind") == TypeKind.Union)
            {
                result.Add(member.Name, new FieldTypeInfo(member.Name, FieldKind.Nil));
                continue;
            }
            if (Member(member) is not { } resolved) continue;
            result.Add(member.Name, resolved);
            if (resolved.Kind == FieldKind.Binary) binaries.Add(member.Name);
        }

        return result;
    }

    /// <summary>メンバの型を取得する（解決できない場合は <c>null</c>）</summary>
    private FieldTypeInfo? Member(JsonProperty member)
    {
        try
        {
            if (TypeCatalogDocument.TextOf(member.Value, "x-kind") is not null)
                return Build(member.Name, member.Value);

            return TypeCatalogDocument.RefTarget(member.Value) is { Length: > 0 } typeId ? Resolve(typeId) : null;
        }
        catch (InvalidDataException e)
        {
            _logger.LogWarning(
                "The type of field '{Field}' could not be resolved, so it gets no editor row: {Error}",
                member.Name, e.Message);
            return null;
        }
    }

    /// <summary>列挙メンバの解決</summary>
    private static List<FieldEnumMember> ReadEnumMembers(JsonElement raw)
    {
        var members = new List<FieldEnumMember>();
        if (!raw.TryGetProperty("anyOf", out JsonElement choices) || choices.ValueKind != JsonValueKind.Array)
            return members;

        foreach (JsonElement choice in choices.EnumerateArray())
        {
            if (TypeCatalogDocument.TextOf(choice, "title") is not { Length: > 0 } name)
                continue;

            long value = choice.TryGetProperty("const", out JsonElement number) && number.TryGetInt64(out long parsed)
                ? parsed
                : members.Count;
            members.Add(new FieldEnumMember(name, value));
        }

        return members;
    }

    /// <summary>文字列の配列（型 id もメンバ名も、読み手は中身を解釈せず並びのまま持つ）</summary>
    private static IReadOnlyList<string> ReadStrings(JsonElement owner, string key)
    {
        if (!owner.TryGetProperty(key, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<string>();
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } id)
                result.Add(id);
        }

        return result;
    }
}
