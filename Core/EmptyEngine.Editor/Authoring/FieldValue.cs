using EmptyEngine.Core;

namespace EmptyEngine.Editor.Authoring;

/// <summary>スキーマに従って解釈するフィールド値</summary>
public sealed class FieldValue
{
    public bool IsNull;
    public long Integer;
    public ulong Unsigned;
    public double Real;
    public bool Bool;
    public string? Text;

    /// <summary>必要になった時点で読み込む本体データへの参照</summary>
    public IAssetBinary? Binary;
    private List<FieldEntry>? _entries;
    private List<FieldValue>? _items;

    /// <summary>struct のフィールド、または Union の選択名とペイロード</summary>
    public List<FieldEntry> Entries => _entries ??= [];
    public List<FieldValue> Items => _items ??= [];
    public static FieldValue Nil() => new() { IsNull = true };
    public FieldValue? Get(string key) => _entries?.Find(entry => entry.Key == key)?.Value;
    public void Add(string key, FieldValue value) => Entries.Add(new FieldEntry(key, value));

    /// <summary>参照が指しているキー（アセットキー、またはオブジェクトの Id。空なら未設定）</summary>
    public string ReferenceKey => Text ?? string.Empty;

    public void SetString(string value) { IsNull = false; Text = value; }

    /// <summary>本体データへの参照を共有し、子ノードを独立させた複製を作成する</summary>
    public FieldValue Clone()
    {
        var copy = (FieldValue)MemberwiseClone();
        copy._entries = _entries?.Select(entry => new FieldEntry(entry.Key, entry.Value.Clone())).ToList();
        copy._items = _items?.Select(item => item.Clone()).ToList();
        return copy;
    }

    /// <summary>この節の中身を <paramref name="source"/> の写しで置き換える</summary>
    /// <remarks>このノードへの参照は維持される。</remarks>
    public void CopyFrom(FieldValue source)
    {
        FieldValue copy = source.Clone();
        IsNull = copy.IsNull;
        Integer = copy.Integer;
        Unsigned = copy.Unsigned;
        Real = copy.Real;
        Bool = copy.Bool;
        Text = copy.Text;
        Binary = copy.Binary;
        _entries = copy._entries;
        _items = copy._items;
    }
}

public sealed class FieldEntry(string key, FieldValue value)
{
    public string Key { get; } = key;
    public FieldValue Value { get; set; } = value;
}
