namespace EmptyEngine.Editor.Authoring;

/// <summary>1 型分の authoring スキーマ</summary>
/// <remarks>
/// 型 id ごとに 1 つの実体を配り続ける。カタログを読み直すと宣言だけが入れ替わるので、
/// この実体を持ち続ける authoring 値と VM は、参照を持ったまま新しい欄を見る。
/// </remarks>
public sealed class ObjectSchema
{
    /// <summary>ひとまとまりで差し替える宣言</summary>
    private sealed record Declaration(
        string DisplayName,
        IReadOnlyDictionary<string, FieldTypeInfo> Fields,
        IReadOnlyList<string> BinaryFields,
        IReadOnlyCollection<string> AssignableTypeNames,
        FieldValue? Defaults);

    private static readonly Declaration Blank = new(
        string.Empty, new Dictionary<string, FieldTypeInfo>(StringComparer.Ordinal), [], [], null);

    private Declaration _declared = Blank;

    /// <summary>実体側の完全修飾型名（カタログのキー）</summary>
    public required string TypeName { get; init; }

    /// <summary>インスペクタ／Add Component の表示名</summary>
    public required string DisplayName
    {
        get => Declared.DisplayName;
        init => Declared = Declared with { DisplayName = value };
    }

    /// <summary>トップレベルのシリアライズ対象フィールド名 → 宣言型</summary>
    public IReadOnlyDictionary<string, FieldTypeInfo> Fields
    {
        get => Declared.Fields;
        init => Declared = Declared with { Fields = value };
    }

    /// <summary>保存順に並べた本体データのフィールド名</summary>
    public IReadOnlyList<string> BinaryFields
    {
        get => Declared.BinaryFields;
        init => Declared = Declared with { BinaryFields = value };
    }

    public FieldTypeInfo Root
    {
        get
        {
            Declaration declared = Declared;
            return new(TypeName, FieldKind.Map) { Members = declared.Fields, Binaries = declared.BinaryFields };
        }
    }

    public IReadOnlyCollection<string> AssignableTypeNames
    {
        get => Declared.AssignableTypeNames;
        init => Declared = Declared with { AssignableTypeNames = value };
    }

    /// <summary>カタログが焼いたトップレベルの既定値（無ければ <c>null</c>）</summary>
    internal FieldValue? Defaults
    {
        get => Declared.Defaults;
        init => Declared = Declared with { Defaults = value };
    }

    /// <summary>今の宣言の世代（読み直しで別の実体になる）</summary>
    internal object Generation => Declared;

    /// <summary><paramref name="data"/> に無いキーだけを既定値で埋め、埋めた宣言の世代を返す</summary>
    internal object FillMissing(FieldValue data)
    {
        Declaration declared = Declared;
        new FieldTypeInfo(TypeName, FieldKind.Map) { Members = declared.Fields }.FillMissing(data, declared.Defaults);
        return declared;
    }

    /// <summary><paramref name="typeName"/> 型の変数へこの型を代入できるか</summary>
    public bool IsAssignableTo(string typeName) => Declared.AssignableTypeNames.Contains(typeName);

    /// <summary>同じ型 id の新しい宣言の取り込み</summary>
    internal void Adopt(ObjectSchema declaration) => Declared = declaration.Declared;

    /// <summary>常に 1 世代ぶんをまとめて見る宣言</summary>
    private Declaration Declared
    {
        get => Volatile.Read(ref _declared);
        set => Volatile.Write(ref _declared, value);
    }

    /// <summary>完全修飾型名からの表示用の末尾セグメント</summary>
    /// <remarks><c>::</c> 区切り（Rust など）も受ける</remarks>
    public static string ShortNameOf(string qualifiedName)
    {
        int index = qualifiedName.LastIndexOf("::", StringComparison.Ordinal);
        if (index < 0) index = qualifiedName.LastIndexOf('.');
        return index < 0 ? qualifiedName : qualifiedName[(index + (qualifiedName[index] == ':' ? 2 : 1))..];
    }
}
