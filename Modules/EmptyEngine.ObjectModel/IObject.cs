using EmptyEngine.Core;

namespace EmptyEngine.ObjectModel;

/// <summary>ゲームオブジェクトの抽象</summary>
public interface IObject
{
    /// <summary>このオブジェクトの親</summary>
    IObject? Parent { get; }

    /// <summary>型指定でのコンポーネント取得</summary>
    T? GetAttachable<T>() where T : class, IAttachable;

    /// <summary>すべてのコンポーネント取得</summary>
    IEnumerable<IAttachable> GetAllAttachables();
}

/// <summary><see cref="IObject"/> にアタッチ可能なコンポーネントの抽象</summary>
public interface IAttachable
{
}

/// <summary>ライフサイクルフックを持つアタッチャブル</summary>
/// <remarks>
/// <see cref="OnCreated"/> / <see cref="OnDestroy"/> はアタッチ／デタッチに紐づき、それぞれ一度だけ呼ばれる。
/// <see cref="OnDeserialized"/> はデシリアライズ（初回ロード・エディタ編集）のたびに、フィールド値が反映され、
/// <see cref="IAssetResolutionHook"/> による参照の解決が済んだ後で呼ばれる。
/// </remarks>
public interface ILifecycleAttachable : IAttachable
{
    /// <summary>シーンに乗ったときに一度だけ呼ばれる</summary>
    void OnCreated(IObject owner);

    /// <summary>フィールド値が反映された後のシリアライザからの呼び出し</summary>
    void OnDeserialized(IObject owner);

    /// <summary>破棄直前のシリアライザからの呼び出し</summary>
    void OnDestroy(IObject owner);
}
