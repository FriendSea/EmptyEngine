using EmptyEngine.Core;

namespace EmptyEngine.Editor;

/// <summary>非シーンアセットをランタイムが読める成果物として読み書きする永続化境界</summary>
/// <remarks>アセットの保存先はキーで識別する。</remarks>
public interface IAssetArtifactStore
{
    /// <summary>非シーンアセットの永続化</summary>
    Task SaveAsync(AssetKey asset, AuthoringObject value, CancellationToken cancellationToken = default);

    /// <summary>指定されたキーの永続化済み成果物を削除する</summary>
    void Delete(AssetKey asset);

    /// <summary>永続化済み非シーンアセット成果物の <see cref="AuthoringObject"/> への復元</summary>
    /// <returns>そのキーがアセットでなければ <c>null</c></returns>
    AuthoringObject? Load(AssetKey asset);
}
