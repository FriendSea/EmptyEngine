namespace EmptyEngine.Core;

/// <summary>アセットへの参照</summary>
public struct AssetReference<T> where T : class
{
    /// <summary>アセットを特定するキー</summary>
    public string AssetKey;

    public AssetReference(string assetKey) => AssetKey = assetKey;

    public AssetReference(AssetKey key) => AssetKey = key.Value;

    /// <summary><see cref="AssetKey"/> が未設定の場合 <c>true</c></summary>
    public readonly bool IsEmpty => string.IsNullOrWhiteSpace(AssetKey);

    /// <summary>識別子としての表現</summary>
    public readonly AssetKey Key => new(AssetKey ?? string.Empty);

    public static AssetReference<T> FromKey(string assetKey) => new(assetKey);

    public readonly override string ToString() => IsEmpty ? "(none)" : AssetKey;
}
