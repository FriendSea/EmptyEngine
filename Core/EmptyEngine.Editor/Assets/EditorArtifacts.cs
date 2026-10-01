using EmptyEngine.Core;

namespace EmptyEngine.Editor.Assets;

/// <summary>シーンとアセットをキーで読み書きする。</summary>
/// <remarks>シーンとして読み込めるかは <see cref="LoadScene"/> の戻り値で判定する。</remarks>
public sealed class EditorArtifacts(ISceneArtifactStore scenes, IAssetArtifactStore assets)
{
    /// <summary>シーンのランタイム成果物としての永続化</summary>
    public Task SaveSceneAsync(AssetKey scene, IReadOnlyList<HierarchyNode> roots, CancellationToken cancellationToken = default) =>
        scenes.SaveAsync(scene, roots, cancellationToken);

    /// <summary>永続化済みシーン成果物の <see cref="HierarchyNode"/> ルート群への復元</summary>
    /// <returns>そのキーがシーンでなければ <c>null</c></returns>
    public IReadOnlyList<HierarchyNode>? LoadScene(AssetKey scene) => scenes.Load(scene);

    /// <summary>非シーンアセットの永続化</summary>
    public Task SaveAssetAsync(AssetKey asset, AuthoringObject value, CancellationToken cancellationToken = default) =>
        assets.SaveAsync(asset, value, cancellationToken);

    /// <summary>永続化済み非シーンアセット成果物の <see cref="AuthoringObject"/> への復元</summary>
    /// <returns>そのキーがアセットでなければ <c>null</c></returns>
    public AuthoringObject? LoadAsset(AssetKey asset) => assets.Load(asset);

    /// <summary>指定キーに対応するシーンと非シーンアセットの成果物を削除する</summary>
    public void Delete(AssetKey asset)
    {
        scenes.Delete(asset);
        assets.Delete(asset);
    }
}
