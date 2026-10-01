using EmptyEngine.ObjectModel;

namespace EmptyEngine.World;

/// <summary><see cref="IObject"/> の有効状態と寿命の操作</summary>
public static class ObjectLifetimeExtensions
{
    /// <summary>サブツリーの擬似破棄による有効化／無効化</summary>
    public static void SetActive(this IObject obj, bool active) => Runtime(obj).SetActive(active);

    /// <summary>フレーム境界でシーンから取り除くよう予約する</summary>
    public static void Destroy(this IObject obj) => Runtime(obj).Destroy();

    /// <summary>このシーンインスタンスのロード時の初期状態への復帰</summary>
    public static void Reload(this IObject obj) => Runtime(obj).Reload();

    private static GameObject Runtime(IObject obj) => obj as GameObject
        ?? throw new ArgumentException("Target must be a runtime object produced by a world.", nameof(obj));
}
