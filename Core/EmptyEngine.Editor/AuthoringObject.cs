using EmptyEngine.Core;
using EmptyEngine.Editor.Authoring;

namespace EmptyEngine.Editor;

/// <summary>読み書き・通信・undo で運ぶ、スキーマと値の組</summary>
public sealed class AuthoringObject
{
    private readonly FieldValue _data;
    private object? _filledFor;

    public AuthoringObject(ObjectSchema schema, FieldValue data)
    {
        ArgumentNullException.ThrowIfNull(schema);
        Schema = schema;
        _data = data.IsNull ? new FieldValue() : data;
    }

    public string TypeName => Schema.TypeName;
    public ObjectSchema Schema { get; }

    /// <summary>値の木</summary>
    /// <remarks>宣言が変わっていれば、無いキーだけをカタログの既定値で埋めてから返す。</remarks>
    public FieldValue Data
    {
        get
        {
            if (!ReferenceEquals(Volatile.Read(ref _filledFor), Schema.Generation))
            {
                lock (_data)
                {
                    if (!ReferenceEquals(_filledFor, Schema.Generation))
                        Volatile.Write(ref _filledFor, Schema.FillMissing(_data));
                }
            }

            return _data;
        }
    }
    public AuthoringObject Clone() => new(Schema, Data.Clone());

    /// <summary>この値が持つアセット参照の走査</summary>
    public IEnumerable<AssetKey> EnumerateAssetReferences()
    {
        var result = new List<AssetKey>();
        Schema.Root.Visit(Data, (value, type) =>
        {
            if (type.Kind == FieldKind.AssetReference && value.ReferenceKey is { Length: > 0 } key)
                result.Add(new AssetKey(key));
        });
        return result;
    }
}
