using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EmptyEngine.Serialization.Generator;

/// <summary>値 1 つの扱い方</summary>
internal enum ValueKind
{
    /// <summary>数値・bool・char・DateTime</summary>
    Primitive,

    /// <summary>文字列（null 可）</summary>
    String,

    /// <summary>列挙</summary>
    Enum,

    /// <summary><c>T?</c>（値型）</summary>
    Nullable,

    /// <summary>1 次元配列</summary>
    Array,

    /// <summary><c>List&lt;T&gt;</c></summary>
    List,

    /// <summary>シリアライズ契約でメンバへ降りる型（class / struct）</summary>
    Contract,

    /// <summary>キー 1 個で指す参照（ワイヤ上は文字列）</summary>
    Reference,
}

/// <summary>シリアライズ対象メンバ 1 つ</summary>
internal sealed class ContractMember
{
    public string Key = string.Empty;

    /// <summary>実体からの読み書きに使う名前</summary>
    public string Name = string.Empty;

    public ITypeSymbol Type = null!;

    public ValuePlan Value = null!;
}

/// <summary>型 1 つの扱い方</summary>
internal sealed class ValuePlan
{
    public ValueKind Kind;

    public ITypeSymbol Type = null!;

    /// <summary><c>global::</c> 付きの完全修飾型名（生成コードに書く形）</summary>
    public string TypeExpression = string.Empty;

    /// <summary>要素の計画</summary>
    public ValuePlan? Element;

    /// <summary>リーダのメソッド名</summary>
    public string ReaderMethod = string.Empty;

    /// <summary>キーを持つメンバの名前（<see cref="ValueKind.Reference"/>）</summary>
    public string ReferenceMember = string.Empty;

    /// <summary>この型のコーデックの生成クラス名</summary>
    public string CodecName = string.Empty;

    /// <summary>契約メンバ（<see cref="ValueKind.Contract"/>）</summary>
    public List<ContractMember> Members = new();

    /// <summary>そのまま写すだけで複製元から独立するか</summary>
    public bool CopiedByValue;

    public bool IsValueType => Type.IsValueType;
}

/// <summary>登録所へ載せる型（コンポーネント／アセット）1 つ分の計画</summary>
internal sealed class TypePlan
{
    public INamedTypeSymbol Symbol = null!;

    /// <summary>ワイヤに載る型名</summary>
    public string WireName = string.Empty;

    public string TypeExpression = string.Empty;

    /// <summary>生成する <c>ITypeSerializer</c> 実装のクラス名</summary>
    public string SerializerName = string.Empty;

    public ValuePlan Value = null!;

    /// <summary>本体（<c>IAssetBinary</c>）メンバ（宣言順）</summary>
    public List<ContractMember> Binaries = new();

    /// <summary>依存関係を解決して呼び出せるコンストラクタの候補</summary>
    public List<ConstructorPlan> Constructors = new();

    public bool IsAttachable;
}

/// <summary>ctor 1 本ぶん</summary>
internal sealed class ConstructorPlan
{
    public List<ParameterPlan> Parameters = new();
}

/// <summary>ctor 引数 1 つ</summary>
internal sealed class ParameterPlan
{
    public string TypeExpression = string.Empty;

    /// <summary>既定値のリテラル</summary>
    public string? DefaultLiteral;

    /// <summary>値型か（コンテナへは問い合わせず既定値を使う）</summary>
    public bool IsValueType;
}

/// <summary>型からシリアライズ対象と値の扱いを記述する計画を作成する</summary>
/// <remarks>扱えない型には <c>null</c> を返す。</remarks>
/// <param name="within">型とメンバへのアクセス可否を判定する基準のアセンブリ。省略時はコンパイル対象のアセンブリ。</param>
internal sealed class ModelBuilder(Compilation compilation, IAssemblySymbol? within = null)
{
    private readonly IAssemblySymbol _within = within ?? compilation.Assembly;

    private readonly Dictionary<ITypeSymbol, ValuePlan?> _plans = new(SymbolEqualityComparer.Default);
    private readonly INamedTypeSymbol? _assetBinary =
        compilation.GetTypeByMetadataName("EmptyEngine.Core.IAssetBinary");
    private readonly INamedTypeSymbol? _ignoreMember =
        compilation.GetTypeByMetadataName("MessagePack.IgnoreMemberAttribute");
    private readonly INamedTypeSymbol? _list =
        compilation.GetTypeByMetadataName("System.Collections.Generic.List`1");
    private readonly INamedTypeSymbol? _assetReference =
        compilation.GetTypeByMetadataName("EmptyEngine.Core.AssetReference`1");
    private readonly INamedTypeSymbol? _componentReference =
        compilation.GetTypeByMetadataName("EmptyEngine.ObjectModel.ComponentReference`1");
    private readonly INamedTypeSymbol? _objectReference =
        compilation.GetTypeByMetadataName("EmptyEngine.ObjectModel.ObjectReference");
    private readonly INamedTypeSymbol? _resolveAsset =
        compilation.GetTypeByMetadataName("EmptyEngine.Generators.ResolveAssetAttribute");

    /// <summary>諦めた型とその理由</summary>
    public Dictionary<ITypeSymbol, string> Skipped { get; } = new(SymbolEqualityComparer.Default);

    public TypePlan? BuildType(INamedTypeSymbol type, bool attachable)
    {
        ValuePlan? value = Plan(type);
        if (value is null || value.Kind != ValueKind.Contract)
            return null;

        var plan = new TypePlan
        {
            Symbol = type,
            WireName = WireNameOf(type),
            TypeExpression = Expression(type),
            SerializerName = "Serializer_" + Sanitize(WireNameOf(type)),
            Value = value,
            IsAttachable = attachable,
        };

        if (BinariesOf(type) is not { } binaries)
        {
            Skip(type, "an IAssetBinary member is not publicly assignable");
            return null;
        }

        plan.Binaries.AddRange(binaries);

        if (!TryCollectConstructors(type, plan.Constructors))
        {
            Skip(type, "no constructor can be called from generated code");
            return null;
        }

        return plan;
    }

    /// <summary>型の扱い方の決定</summary>
    public ValuePlan? Plan(ITypeSymbol type)
    {
        if (_plans.TryGetValue(type, out ValuePlan? cached))
            return cached;

        var plan = new ValuePlan { Type = type, TypeExpression = Expression(type) };
        _plans[type] = plan;

        if (!Fill(plan))
        {
            _plans[type] = null;
            return null;
        }

        if (plan.Kind is ValueKind.Array or ValueKind.List or ValueKind.Nullable or ValueKind.Contract
            or ValueKind.Reference)
            plan.CodecName = "Codec_" + Sanitize(type.ToDisplayString());

        return plan;
    }

    /// <summary>組み立て済みの計画（コーデックを生成する対象）</summary>
    public IEnumerable<ValuePlan> Plans => _plans.Values.Where(p => p is not null)!;

    private bool Fill(ValuePlan plan)
    {
        ITypeSymbol type = plan.Type;

        if (type is IArrayTypeSymbol array)
        {
            if (array.Rank != 1)
                return Skip(type, "only single-dimensional arrays are supported");

            plan.Kind = ValueKind.Array;
            plan.Element = Plan(array.ElementType);
            return plan.Element is not null;
        }

        if (type.TypeKind == TypeKind.Enum)
        {
            plan.Kind = ValueKind.Enum;
            plan.CopiedByValue = true;
            plan.ReaderMethod = ReaderFor(((INamedTypeSymbol)type).EnumUnderlyingType!.SpecialType)
                                ?? string.Empty;
            return plan.ReaderMethod.Length > 0;
        }

        if (type.SpecialType == SpecialType.System_String)
        {
            plan.Kind = ValueKind.String;
            plan.CopiedByValue = true;
            return true;
        }

        if (ReaderFor(type.SpecialType) is { } reader)
        {
            plan.Kind = ValueKind.Primitive;
            plan.CopiedByValue = true;
            plan.ReaderMethod = reader;
            return true;
        }

        if (type is not INamedTypeSymbol named)
            return Skip(type, "unsupported type kind");

        if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            plan.Kind = ValueKind.Nullable;
            plan.Element = Plan(named.TypeArguments[0]);
            plan.CopiedByValue = plan.Element?.CopiedByValue ?? false;
            return plan.Element is not null;
        }

        if (_list is not null && SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, _list))
        {
            plan.Kind = ValueKind.List;
            plan.Element = Plan(named.TypeArguments[0]);
            return plan.Element is not null;
        }

        if (ReferenceMemberOf(named) is { } referenceMember)
        {
            plan.Kind = ValueKind.Reference;
            plan.ReferenceMember = referenceMember;
            plan.CopiedByValue = true;
            return true;
        }

        if (named.TypeKind is TypeKind.Interface or TypeKind.Delegate || named.IsAbstract)
            return Skip(type, "abstract and interface members have no single concrete shape on the wire");

        if (named.TypeKind is not (TypeKind.Class or TypeKind.Struct))
            return Skip(type, "unsupported type kind");

        if (!IsAccessible(named))
            return Skip(type, "the type is not accessible from the referencing assembly");

        plan.Kind = ValueKind.Contract;

        if (!TryCollectMembers(named, plan.Members))
            return false;

        plan.CopiedByValue = named.IsValueType && plan.Members.All(m => m.Value.CopiedByValue);

        return true;
    }

    /// <summary>ワイヤ上は文字列 1 個で往復する参照型か</summary>
    /// <returns>キーを持つメンバの名前。参照でなければ <c>null</c></returns>
    private string? ReferenceMemberOf(INamedTypeSymbol type)
    {
        INamedTypeSymbol definition = type.OriginalDefinition;
        if (!SymbolEqualityComparer.Default.Equals(definition, _assetReference)
            && !SymbolEqualityComparer.Default.Equals(definition, _componentReference)
            && !SymbolEqualityComparer.Default.Equals(definition, _objectReference))
        {
            return null;
        }

        foreach (IFieldSymbol field in type.GetMembers().OfType<IFieldSymbol>())
        {
            if (field is { IsStatic: false, Type.SpecialType: SpecialType.System_String })
                return field.Name;
        }

        return null;
    }

    private bool TryCollectMembers(INamedTypeSymbol type, List<ContractMember> members)
    {
        var seen = new HashSet<string>();

        for (INamedTypeSymbol? t = type; t is not null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
        {
            foreach (ISymbol member in t.GetMembers())
            {
                (string Name, ITypeSymbol Type)? candidate = Candidate(member);
                if (candidate is null)
                    continue;

                if (IsAssetBinary(candidate.Value.Type))
                    continue;

                if (!seen.Add(candidate.Value.Name))
                    continue;

                ValuePlan? value = Plan(candidate.Value.Type);
                if (value is null)
                    return Skip(type, $"member '{candidate.Value.Name}' has an unsupported type " +
                                      $"'{candidate.Value.Type.ToDisplayString()}'");

                members.Add(new ContractMember
                {
                    Key = candidate.Value.Name,
                    Name = candidate.Value.Name,
                    Type = candidate.Value.Type,
                    Value = value,
                });
            }

            if (!TryCollectResolveAssetMembers(t, type, members, seen))
                return false;
        }

        return true;
    }

    /// <summary><c>[ResolveAsset]</c> フィールドから生成される参照プロパティの取り込み</summary>
    private bool TryCollectResolveAssetMembers(
        INamedTypeSymbol declaring, INamedTypeSymbol root, List<ContractMember> members, HashSet<string> seen)
    {
        if (_resolveAsset is null || _assetReference is null || declaring.DeclaringSyntaxReferences.Length == 0)
            return true;

        foreach (IFieldSymbol field in declaring.GetMembers().OfType<IFieldSymbol>())
        {
            AttributeData? attribute = field.GetAttributes()
                .FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, _resolveAsset));
            if (attribute is null)
                continue;

            string name = attribute.ConstructorArguments.Length > 0
                          && attribute.ConstructorArguments[0].Value is string explicitName
                          && !string.IsNullOrWhiteSpace(explicitName)
                ? explicitName
                : DerivePropertyName(field.Name);

            if (!seen.Add(name))
                continue;

            ITypeSymbol reference = _assetReference.Construct(
                field.Type.WithNullableAnnotation(NullableAnnotation.NotAnnotated));

            ValuePlan? value = Plan(reference);
            if (value is null)
                return Skip(root, $"generated reference '{name}' has an unsupported type");

            members.Add(new ContractMember { Key = name, Name = name, Type = reference, Value = value });
        }

        return true;
    }

    /// <summary>フィールド名からの参照プロパティ名の導出</summary>
    private static string DerivePropertyName(string fieldName)
    {
        string name = fieldName;
        if (name.StartsWith("m_", System.StringComparison.Ordinal))
            name = name.Substring(2);
        name = name.TrimStart('_');
        if (name.Length == 0)
            name = fieldName;
        name = char.ToUpperInvariant(name[0]) + name.Substring(1);
        return name == fieldName ? name + "Reference" : name;
    }

    /// <summary>シリアライズ契約に載るメンバの名前と型</summary>
    private (string Name, ITypeSymbol Type)? Candidate(ISymbol member)
    {
        if (member.IsStatic || HasIgnoreMember(member))
            return null;

        switch (member)
        {
            case IFieldSymbol field:
                if (field.AssociatedSymbol is not null || field.IsConst || field.IsReadOnly || field.IsImplicitlyDeclared)
                    return null;
                if (field.DeclaredAccessibility != Accessibility.Public)
                    return null;
                return (field.Name, field.Type);

            case IPropertySymbol property:
                if (property.IsIndexer || !IsAutoProperty(property))
                    return null;
                if (property.GetMethod is not { DeclaredAccessibility: Accessibility.Public })
                    return null;
                if (property.SetMethod is not { DeclaredAccessibility: Accessibility.Public } setter || setter.IsInitOnly)
                    return null;
                return (property.Name, property.Type);

            default:
                return null;
        }
    }

    /// <summary>auto-property か</summary>
    private static bool IsAutoProperty(IPropertySymbol property)
    {
        if (property.DeclaringSyntaxReferences.Length > 0)
        {
            foreach (SyntaxReference reference in property.DeclaringSyntaxReferences)
            {
                if (reference.GetSyntax() is not PropertyDeclarationSyntax declaration)
                    continue;
                if (declaration.ExpressionBody is not null)
                    return false;
                return declaration.AccessorList is { } accessors
                       && accessors.Accessors.All(a => a.Body is null && a.ExpressionBody is null);
            }

            return false;
        }

        return property.GetMethod is { } getter
               && getter.GetAttributes().Any(a => a.AttributeClass?.Name == "CompilerGeneratedAttribute");
    }

    private bool HasIgnoreMember(ISymbol member)
        => _ignoreMember is not null
           && member.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, _ignoreMember));

    private bool IsAssetBinary(ITypeSymbol type)
        => _assetBinary is not null
           && (SymbolEqualityComparer.Default.Equals(type, _assetBinary)
               || type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, _assetBinary)));

    /// <summary>本体（<c>IAssetBinary</c>）メンバの宣言順での収集</summary>
    /// <returns>割り当てられない本体メンバを持つ型なら <c>null</c></returns>
    public List<ContractMember>? BinariesOf(INamedTypeSymbol type)
    {
        var binaries = new List<ContractMember>();
        return TryCollectBinaries(type, binaries) ? binaries : null;
    }

    private bool TryCollectBinaries(INamedTypeSymbol type, List<ContractMember> binaries)
    {
        var seen = new HashSet<string>();
        for (INamedTypeSymbol? t = type; t is not null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
        {
            foreach (ISymbol member in t.GetMembers())
            {
                ITypeSymbol? memberType = member switch
                {
                    IFieldSymbol f when f.AssociatedSymbol is null && !f.IsStatic && !f.IsImplicitlyDeclared => f.Type,
                    IPropertySymbol p when !p.IsStatic && !p.IsIndexer && IsAutoProperty(p) => p.Type,
                    _ => null,
                };

                if (memberType is null || !IsAssetBinary(memberType) || !seen.Add(member.Name))
                    continue;

                bool assignable = member switch
                {
                    IFieldSymbol f => f is { DeclaredAccessibility: Accessibility.Public, IsReadOnly: false },
                    IPropertySymbol p => p is { GetMethod.DeclaredAccessibility: Accessibility.Public }
                                         && p.SetMethod is { DeclaredAccessibility: Accessibility.Public, IsInitOnly: false },
                    _ => false,
                };

                if (!assignable)
                    return false;

                binaries.Add(new ContractMember { Key = member.Name, Name = member.Name, Type = memberType });
            }
        }

        return true;
    }

    /// <summary>呼べる ctor の引数が多い順での収集</summary>
    private bool TryCollectConstructors(INamedTypeSymbol type, List<ConstructorPlan> plans)
    {
        if (type.IsValueType && type.InstanceConstructors.All(c => c.Parameters.Length == 0))
        {
            plans.Add(new ConstructorPlan());
            return true;
        }

        foreach (IMethodSymbol constructor in type.InstanceConstructors
                     .Where(c => c.DeclaredAccessibility == Accessibility.Public && !c.IsStatic)
                     .OrderByDescending(c => c.Parameters.Length))
        {
            var plan = new ConstructorPlan();
            bool usable = true;
            foreach (IParameterSymbol parameter in constructor.Parameters)
            {
                string? defaultLiteral = parameter.HasExplicitDefaultValue
                    ? Literal(parameter.ExplicitDefaultValue)
                    : null;

                if (!IsAccessible(parameter.Type)
                    || (parameter.Type.IsValueType && defaultLiteral is null)
                    || parameter.RefKind != RefKind.None)
                {
                    usable = false;
                    break;
                }

                plan.Parameters.Add(new ParameterPlan
                {
                    TypeExpression = Expression(parameter.Type),
                    DefaultLiteral = defaultLiteral,
                    IsValueType = parameter.Type.IsValueType,
                });
            }

            if (usable)
                plans.Add(plan);
        }

        return plans.Count > 0;
    }

    private static string? Literal(object? value) => value switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        string s => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"",
        char c => "'" + c + "'",
        float f => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "f",
        double d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "d",
        _ => System.Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
    };

    private bool Skip(ITypeSymbol type, string reason)
    {
        if (!Skipped.ContainsKey(type))
            Skipped[type] = reason;
        return false;
    }

    /// <summary>生成コードから名指しできる型か</summary>
    private bool IsAccessible(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol array)
            return IsAccessible(array.ElementType);

        if (!compilation.IsSymbolAccessibleWithin(type, _within))
            return false;

        return type is not INamedTypeSymbol named || named.TypeArguments.All(IsAccessible);
    }

    private static string? ReaderFor(SpecialType type) => type switch
    {
        SpecialType.System_Boolean => "ReadBoolean",
        SpecialType.System_SByte => "ReadSByte",
        SpecialType.System_Byte => "ReadByte",
        SpecialType.System_Int16 => "ReadInt16",
        SpecialType.System_UInt16 => "ReadUInt16",
        SpecialType.System_Int32 => "ReadInt32",
        SpecialType.System_UInt32 => "ReadUInt32",
        SpecialType.System_Int64 => "ReadInt64",
        SpecialType.System_UInt64 => "ReadUInt64",
        SpecialType.System_Single => "ReadSingle",
        SpecialType.System_Double => "ReadDouble",
        SpecialType.System_Char => "ReadChar",
        SpecialType.System_DateTime => "ReadDateTime",
        _ => null,
    };

    public static string Expression(ITypeSymbol type)
        => type.WithNullableAnnotation(NullableAnnotation.NotAnnotated)
            .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    /// <summary>ワイヤに載る型名</summary>
    public static string WireNameOf(INamedTypeSymbol type)
    {
        string name = type.MetadataName;
        for (INamedTypeSymbol? outer = type.ContainingType; outer is not null; outer = outer.ContainingType)
            name = outer.MetadataName + "+" + name;

        return type.ContainingNamespace is { IsGlobalNamespace: false } ns
            ? ns.ToDisplayString() + "." + name
            : name;
    }

    public static string Sanitize(string name)
    {
        var buffer = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
            buffer.Append(char.IsLetterOrDigit(c) ? c : '_');
        return buffer.ToString();
    }
}
