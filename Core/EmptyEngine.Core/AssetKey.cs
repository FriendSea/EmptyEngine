namespace EmptyEngine.Core;

/// <summary>アセットを一意に識別するキー</summary>
public readonly record struct AssetKey
{
    private readonly string? _value;

    public AssetKey(string value) => _value = value;

    /// <summary>キー文字列</summary>
    public string Value => _value ?? string.Empty;

    /// <summary>キーが未設定の場合 <c>true</c></summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(_value);

    public override string ToString() => IsEmpty ? "(none)" : Value;
}
