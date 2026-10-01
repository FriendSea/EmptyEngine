namespace EmptyEngine.ObjectModel;

/// <summary>同一シーンインスタンス内のオブジェクトが持つ <typeparamref name="T"/> コンポーネントへの参照</summary>
public struct ComponentReference<T> where T : class
{
    /// <summary>参照先コンポーネントを持つ <see cref="IObject"/> のId</summary>
    public string TargetId;

    public ComponentReference(string targetId) => TargetId = targetId;

    /// <summary><see cref="TargetId"/> が未設定の場合 <c>true</c></summary>
    public readonly bool IsEmpty => string.IsNullOrWhiteSpace(TargetId);

    public static ComponentReference<T> FromId(string targetId) => new(targetId);

    public readonly override string ToString() => IsEmpty ? "(none)" : TargetId;
}
