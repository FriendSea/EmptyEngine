using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace EmptyEngine.Editor.Authoring;

/// <summary>スキーマが指定するフィールドの種別</summary>
public enum FieldKind
{
    Nil, Bool, Int, UInt, Float32, Float64, String, Binary, Map, Array,
    AssetReference, ObjectReference, Enum, Union,
}

/// <summary>ノードの宣言型と種別</summary>
/// <param name="TypeName">
/// 型 id。参照（<see cref="FieldKind.AssetReference"/> / <see cref="FieldKind.ObjectReference"/>）では、
/// 参照自身ではなく指せる型の id
/// </param>
/// <param name="Kind">値の種別</param>
public sealed record FieldTypeInfo(string TypeName, FieldKind Kind)
{
    public IReadOnlyList<FieldEnumMember> EnumMembers { get; init; } = [];
    public FieldTypeInfo? Element { get; init; }
    public IReadOnlyDictionary<string, FieldTypeInfo>? Members { get; init; }

    /// <summary>本体データの保存順に並べた <see cref="FieldKind.Binary"/> のメンバ名</summary>
    public IReadOnlyList<string> Binaries { get; init; } = [];

    /// <summary>RGBA チャンネルにあたるメンバ名（R, G, B, A の順）。色として扱えない型では空</summary>
    public IReadOnlyList<string> ColorChannels { get; init; } = [];

    /// <summary>カタログが焼いたこの型の既定値（struct のみ。無ければ <c>null</c>）</summary>
    internal FieldValue? Default { get; init; }

    public bool IsArray => Kind == FieldKind.Array;

    public FieldTypeInfo Member(string name) => TryMember(name, out FieldTypeInfo? member)
        ? member
        : throw new InvalidDataException($"Schema '{TypeName}' does not declare field '{name}'.");

    /// <summary>スキーマで宣言されたメンバの型を取得する</summary>
    public bool TryMember(string name, [NotNullWhen(true)] out FieldTypeInfo? member)
    {
        if (Members is { } members) return members.TryGetValue(name, out member);
        member = null;
        return false;
    }

    public long EnumValueOf(string? name)
    {
        foreach (FieldEnumMember member in EnumMembers)
            if (member.Name == name) return member.Value;
        return EnumMembers.Count > 0 ? EnumMembers[0].Value : 0;
    }

    public string EnumNameOf(long value)
    {
        foreach (FieldEnumMember member in EnumMembers)
            if (member.Value == value) return member.Name;
        return value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>宣言型に従った既定値のノード</summary>
    public FieldValue CreateDefault()
    {
        var value = new FieldValue();
        switch (Kind)
        {
            case FieldKind.Nil: return FieldValue.Nil();
            case FieldKind.Enum: value.Integer = EnumMembers.FirstOrDefault()?.Value ?? 0; break;
            case FieldKind.String:
            case FieldKind.AssetReference:
            case FieldKind.ObjectReference: value.Text = string.Empty; break;
            case FieldKind.Map:
                if (Members is { } members)
                    foreach (var member in members) value.Add(member.Key, member.Value.CreateDefault());
                break;
            case FieldKind.Union:
                if (Members is { Count: > 0 })
                {
                    var variant = Members.First();
                    value.Add(variant.Key, variant.Value.CreateDefault());
                }
                break;
        }
        return value;
    }

    /// <summary>参照を葉として扱い、スキーマに従って子ノードを訪問する</summary>
    public void Visit(FieldValue value, Action<FieldValue, FieldTypeInfo> visitor)
    {
        if (value.IsNull) return;
        visitor(value, this);
        if (Kind == FieldKind.Array)
            foreach (FieldValue item in value.Items) Element!.Visit(item, visitor);
        else if (Kind is FieldKind.Map or FieldKind.Union)
            foreach (FieldEntry entry in value.Entries)
                if (TryMember(entry.Key, out FieldTypeInfo? member)) member.Visit(entry.Value, visitor);
    }

    /// <summary><paramref name="defaults"/> へ <paramref name="source"/> を重ねた写し</summary>
    /// <remarks>map はメンバごとに重ね、宣言されていないキーは捨てる。map 以外は <paramref name="source"/> がそのまま勝つ。</remarks>
    public FieldValue Overlay(FieldValue defaults, FieldValue? source)
    {
        if (source is null) return defaults.Clone();
        if (Kind != FieldKind.Map || source.IsNull || defaults.IsNull) return source.Clone();
        FieldValue result = defaults.Clone();
        foreach (FieldEntry entry in source.Entries)
        {
            if (!TryMember(entry.Key, out FieldTypeInfo? member)) continue;
            FieldEntry? existing = result.Entries.Find(e => e.Key == entry.Key);
            if (existing is null) result.Add(entry.Key, entry.Value.Clone());
            else existing.Value = member.Overlay(existing.Value, entry.Value);
        }
        return result;
    }

    /// <summary><paramref name="value"/> に無いメンバだけを <paramref name="defaults"/> で埋める</summary>
    /// <remarks>既にある値は書き換えない。配列の要素は要素型の既定値で埋める。</remarks>
    internal void FillMissing(FieldValue value, FieldValue? defaults)
    {
        if (value.IsNull) return;
        if (Kind == FieldKind.Array)
        {
            foreach (FieldValue item in value.Items) Element!.FillMissing(item, Element.Default);
            return;
        }

        if (Kind != FieldKind.Map || Members is not { } members) return;
        foreach (var (name, member) in members)
        {
            FieldValue? fallback = defaults?.Get(name);
            if (value.Get(name) is { } present) member.FillMissing(present, fallback ?? member.Default);
            else if (fallback is not null) value.Add(name, fallback.Clone());
        }
    }
}

public sealed record FieldEnumMember(string Name, long Value);
