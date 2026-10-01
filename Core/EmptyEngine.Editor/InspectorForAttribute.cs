namespace EmptyEngine.Editor;

/// <summary>この Razor コンポーネントが専用インスペクタとして受け持つ型</summary>
/// <remarks>対象には具象型とインターフェイスを指定できる。型名の完全一致を優先し、該当しなければカタログのスキーマ上の派生関係から選ぶ。</remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class InspectorForAttribute : Attribute
{
    /// <param name="typeName">受け持つ型のオーサリング側の型名</param>
    public InspectorForAttribute(string typeName) => TypeName = typeName;

    /// <param name="type">受け持つ型。型名はその <see cref="Type.FullName"/> を使う</param>
    public InspectorForAttribute(Type type)
        : this(type.FullName ?? throw new ArgumentException("The type has no full name.", nameof(type)))
    {
    }

    /// <summary>受け持つ型の名前</summary>
    public string TypeName { get; }
}
