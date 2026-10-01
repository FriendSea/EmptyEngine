using EmptyEngine.Core;

namespace EmptyEngine.Editor;

/// <summary>シーンをランタイムが読める成果物として読み書きする永続化境界</summary>
/// <remarks>シーンの保存先はキーで識別する。</remarks>
public interface ISceneArtifactStore
{
    /// <summary>シーンのランタイム成果物としての永続化</summary>
    Task SaveAsync(AssetKey scene, IReadOnlyList<HierarchyNode> roots, CancellationToken cancellationToken = default);

    /// <summary>指定されたキーの永続化済み成果物を削除する</summary>
    void Delete(AssetKey scene);

    /// <summary>永続化済みシーン成果物の <see cref="HierarchyNode"/> ルート群への復元</summary>
    /// <returns>そのキーがシーンでなければ <c>null</c></returns>
    IReadOnlyList<HierarchyNode>? Load(AssetKey scene);
}
