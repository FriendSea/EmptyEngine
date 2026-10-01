namespace EmptyEngine.ObjectModel;

/// <summary>同一シーンインスタンス内の <see cref="IObject"/> への参照。</summary>
/// <remarks>複製後も各シーンインスタンス内の同じ Id を指す。シーンを跨ぐ参照は表現できない。</remarks>
public struct ObjectReference
{
    /// <summary>参照先 <see cref="IObject"/> のId</summary>
    public string TargetId;

    public ObjectReference(string targetId) => TargetId = targetId;

    /// <summary><see cref="TargetId"/> が未設定の場合 <c>true</c></summary>
    public readonly bool IsEmpty => string.IsNullOrWhiteSpace(TargetId);

    public static ObjectReference FromId(string targetId) => new(targetId);

    public readonly override string ToString() => IsEmpty ? "(none)" : TargetId;
}
