using EmptyEngine.ObjectModel;

namespace EmptyEngine.World;

/// <summary><see cref="IObject"/> にシーングラフ上の関係を問い合わせる拡張</summary>
public static class ObjectHierarchyExtensions
{
    /// <summary>このオブジェクトの直下の子</summary>
    public static IReadOnlyList<IObject> GetChildren(this IObject obj)
        => obj is GameObject node ? node.Children : Array.Empty<IObject>();

    /// <summary>まだ世界につながっている生きたオブジェクトか</summary>
    public static bool IsAlive(this IObject obj) => obj is GameObject { IsAlive: true };

    /// <summary>このオブジェクトが破棄されるまで有効なトークン</summary>
    public static CancellationToken Lifetime(this IObject obj)
        => obj is GameObject node ? node.Lifetime : new CancellationToken(canceled: true);

    /// <summary><paramref name="owner"/> と同一シーンインスタンス内での <see cref="ObjectReference"/> の解決</summary>
    public static IObject? FindObject(this IObject owner, ObjectReference reference)
        => reference.IsEmpty ? null : owner.FindObject(reference.TargetId);

    /// <summary><see cref="FindObject(IObject, ObjectReference)"/> の Id 直接指定版</summary>
    public static IObject? FindObject(this IObject owner, string id)
        => owner is GameObject node ? node.Find(id) : null;

    /// <summary>コンポーネント参照（<see cref="ComponentReference{T}"/>）の解決</summary>
    public static T? FindComponent<T>(this IObject owner, ComponentReference<T> reference) where T : class, IAttachable
        => reference.IsEmpty ? null : owner.FindObject(reference.TargetId)?.GetAttachable<T>();
}
