using EmptyEngine.Serialization.Generator;
using Microsoft.CodeAnalysis;

/// <summary>シンボルからカタログの型表を組み立てる</summary>
/// <remarks>ランタイムのシリアライズ契約に対応する型とメンバを記述する。</remarks>
internal sealed class CatalogModel
{
    private readonly ModelBuilder _builder;
    private readonly INamedTypeSymbol? _assetReference;
    private readonly INamedTypeSymbol? _componentReference;
    private readonly INamedTypeSymbol? _objectReference;
    private readonly INamedTypeSymbol? _object;
    private readonly INamedTypeSymbol? _colorChannels;

    private readonly Dictionary<string, TypeEntry> _table = new(StringComparer.Ordinal);
    private readonly Dictionary<ITypeSymbol, string?> _described = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<string, ITypeSymbol> _typeIds = new(StringComparer.Ordinal);

    public CatalogModel(Compilation compilation, IAssemblySymbol? within)
    {
        _builder = new ModelBuilder(compilation, within);
        _assetReference = compilation.GetTypeByMetadataName("EmptyEngine.Core.AssetReference`1");
        _componentReference = compilation.GetTypeByMetadataName("EmptyEngine.ObjectModel.ComponentReference`1");
        _objectReference = compilation.GetTypeByMetadataName("EmptyEngine.ObjectModel.ObjectReference");
        _object = compilation.GetTypeByMetadataName("EmptyEngine.ObjectModel.IObject");
        _colorChannels = compilation.GetTypeByMetadataName("EmptyEngine.ObjectModel.ColorChannelsAttribute");
    }

    /// <summary>型 id から記述へ</summary>
    public Dictionary<string, TypeEntry> Table => _table;

    /// <summary>authoring の根 1 個の記述</summary>
    /// <remarks>根は必ず struct 種として載る（参照型でも）。落ちるのは入れ子だけ</remarks>
    /// <returns>型 id。計画が立たない型なら <c>null</c></returns>
    public string? AddRoot(INamedTypeSymbol type, bool attachable)
    {
        if (_builder.Plan(type) is not { } plan)
            return null;

        string id = Claim(type);
        TypeEntry entry = Describe(plan, id)
                          ?? new TypeEntry(id, CatalogFormat.StructKind)
                          {
                              Fields = FieldsOf(plan),
                              ColorChannels = ColorChannelsOf(type),
                          };
        if (entry.Kind == CatalogFormat.StructKind)
            AddBinaries(type, entry);
        entry.DisplayName = CatalogTypeId.DisplayNameOf(type);
        entry.Attachable = attachable;
        entry.AssignableTo = AssignableTypeIds(type);

        _table[id] = entry;
        _described[type] = id;
        return id;
    }

    /// <summary>型 id の確保（重複は形式の破れ）</summary>
    private string Claim(ITypeSymbol type)
    {
        string id = CatalogTypeId.Of(type);
        if (_typeIds.TryGetValue(id, out ITypeSymbol? owner)
            && !SymbolEqualityComparer.Default.Equals(owner, type))
        {
            throw new InvalidOperationException($"Duplicate type id '{id}'. Catalog type ids must be unique.");
        }

        _typeIds[id] = type;
        return id;
    }

    /// <summary>入れ子の型の記述と、その型 id の引き当て</summary>
    private string? Ensure(ValuePlan plan)
    {
        if (_described.TryGetValue(plan.Type, out string? known))
            return known;

        string id = Claim(plan.Type);
        _described[plan.Type] = id;

        TypeEntry? entry = Describe(plan, id);
        if (entry is null)
        {
            _described[plan.Type] = null;
            return null;
        }

        entry.DisplayName = CatalogTypeId.DisplayNameOf(plan.Type);
        _table[id] = entry;
        return id;
    }

    /// <summary>計画 1 個のカタログ上の姿</summary>
    /// <remarks>アセット参照とオブジェクト参照は、参照先のメンバを展開しない葉として扱う。</remarks>
    private TypeEntry? Describe(ValuePlan plan, string id)
    {
        if (IsConstructedFrom(plan.Type, _assetReference))
            return new TypeEntry(id, CatalogFormat.AssetRefKind) { Target = AssetTargetId(plan.Type) };
        if (SymbolEqualityComparer.Default.Equals(plan.Type, _objectReference))
            return new TypeEntry(id, CatalogFormat.ObjectRefKind);
        if (IsConstructedFrom(plan.Type, _componentReference))
            return new TypeEntry(id, CatalogFormat.ObjectRefKind) { Target = ArgumentId(plan.Type) };

        switch (plan.Kind)
        {
            case ValueKind.Enum:
                return new TypeEntry(id, CatalogFormat.EnumKind) { Values = EnumMembersOf(plan.Type) };

            case ValueKind.Array:
                return plan.Element is { } element && Ensure(element) is { } elementId
                    ? new TypeEntry(id, CatalogFormat.ArrayKind) { Element = elementId }
                    : null;

            case ValueKind.Contract:
                return new TypeEntry(id, CatalogFormat.StructKind)
                {
                    Fields = FieldsOf(plan),
                    ColorChannels = ColorChannelsOf(plan.Type),
                };

            default:
                return ScalarKind(plan) is { } kind ? new TypeEntry(id, kind) : null;
        }
    }

    /// <summary>本体（<c>IAssetBinary</c>）の名前つきフィールドとしての追加</summary>
    /// <remarks>フィールドの順序は blob 内の本体データの順序と一致する。</remarks>
    private void AddBinaries(INamedTypeSymbol type, TypeEntry entry)
    {
        if (_builder.BinariesOf(type) is not { Count: > 0 } binaries)
            return;

        foreach (ContractMember binary in binaries)
            entry.Fields.Add(new FieldEntry(binary.Key, EnsureBinary(binary.Type)));
    }

    private string EnsureBinary(ITypeSymbol type)
    {
        if (_described.TryGetValue(type, out string? known) && known is not null)
            return known;

        string id = Claim(type);
        _described[type] = id;
        _table[id] = new TypeEntry(id, CatalogFormat.BinaryKind)
        {
            DisplayName = CatalogTypeId.DisplayNameOf(type),
        };
        return id;
    }

    /// <summary>名前つきメンバ（記述できない型のメンバは落ちる）</summary>
    private List<FieldEntry> FieldsOf(ValuePlan plan)
    {
        var result = new List<FieldEntry>();
        foreach (ContractMember member in plan.Members)
        {
            if (Ensure(member.Value) is { } id)
                result.Add(new FieldEntry(member.Key, id));
        }

        return result;
    }

    /// <summary><c>[ColorChannels]</c> が名乗る RGBA メンバ名（宣言の無い型は空）</summary>
    private List<string> ColorChannelsOf(ITypeSymbol type)
    {
        if (_colorChannels is null)
            return new List<string>();

        foreach (AttributeData attribute in type.GetAttributes())
        {
            if (!SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, _colorChannels)
                || attribute.ConstructorArguments.Length != 4)
            {
                continue;
            }

            var names = attribute.ConstructorArguments.Select(argument => argument.Value as string).ToList();
            if (names.All(name => !string.IsNullOrEmpty(name)))
                return names!;
        }

        return new List<string>();
    }

    private static string? ScalarKind(ValuePlan plan) => plan.Type.SpecialType switch
    {
        SpecialType.System_Single => CatalogFormat.FloatKind,
        SpecialType.System_Double => CatalogFormat.DoubleKind,
        SpecialType.System_SByte or SpecialType.System_Int16
            or SpecialType.System_Int32 or SpecialType.System_Int64 => CatalogFormat.IntKind,
        SpecialType.System_Byte or SpecialType.System_UInt16
            or SpecialType.System_UInt32 or SpecialType.System_UInt64 => CatalogFormat.UIntKind,
        SpecialType.System_Boolean => CatalogFormat.BoolKind,
        SpecialType.System_String => CatalogFormat.StringKind,
        _ => null,
    };

    private static bool IsConstructedFrom(ITypeSymbol type, INamedTypeSymbol? definition)
        => definition is not null
           && type is INamedTypeSymbol { TypeArguments.Length: 1 } named
           && SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, definition);

    /// <summary>アセット参照の参照先。オブジェクトを指すものはシーン参照</summary>
    private string? AssetTargetId(ITypeSymbol type) =>
        type is INamedTypeSymbol { TypeArguments: [var target] } && SymbolEqualityComparer.Default.Equals(target, _object)
            ? CatalogFormat.SceneTarget
            : ArgumentId(type);

    private static string? ArgumentId(ITypeSymbol type)
        => type is INamedTypeSymbol { TypeArguments.Length: 1 } named ? CatalogTypeId.Of(named.TypeArguments[0]) : null;

    private static List<(string Name, long Value)> EnumMembersOf(ITypeSymbol type)
    {
        var members = new List<(string, long)>();
        foreach (IFieldSymbol field in type.GetMembers().OfType<IFieldSymbol>())
        {
            if (field is { IsConst: true, ConstantValue: { } value })
                members.Add((field.Name, ToLong(value)));
        }

        return members;

        static long ToLong(object value)
        {
            try { return Convert.ToInt64(value); }
            catch (OverflowException) { return unchecked((long)Convert.ToUInt64(value)); }
        }
    }

    private static List<string> AssignableTypeIds(INamedTypeSymbol type)
    {
        var ids = new List<string>();
        for (INamedTypeSymbol? current = type;
             current is not null && current.SpecialType != SpecialType.System_Object;
             current = current.BaseType)
        {
            ids.Add(CatalogTypeId.Of(current));
        }

        foreach (INamedTypeSymbol contract in type.AllInterfaces)
            ids.Add(CatalogTypeId.Of(contract));

        return ids;
    }
}

/// <summary>カタログ上の型 id</summary>
/// <remarks><see cref="Type.FullName"/> と一致する型 ID を使う。入れ子型は <c>Ns.Outer+Inner</c>、ジェネリック型の実引数はアセンブリ修飾名で表す。</remarks>
internal static class CatalogTypeId
{
    /// <summary>表示名（CLR の <c>Type.Name</c> に揃える＝ジェネリクスは <c>`1</c> 付き）</summary>
    public static string DisplayNameOf(ITypeSymbol type)
        => type is IArrayTypeSymbol array
            ? $"{DisplayNameOf(array.ElementType)}[{new string(',', array.Rank - 1)}]"
            : type.MetadataName;

    public static string Of(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol array)
            return $"{Of(array.ElementType)}[{new string(',', array.Rank - 1)}]";

        if (type is not INamedTypeSymbol named)
            return type.Name;

        if (named.TypeArguments.Length == 0)
            return ModelBuilder.WireNameOf(named);

        string definition = ModelBuilder.WireNameOf(named.OriginalDefinition);
        IEnumerable<string> arguments = named.TypeArguments
            .Select(a => $"{Of(a)}, {a.ContainingAssembly?.Name}");
        return $"{definition}[[{string.Join("],[", arguments)}]]";
    }
}
