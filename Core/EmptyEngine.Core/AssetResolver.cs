namespace EmptyEngine.Core;

/// <summary><see cref="AssetReference{T}"/> からアセット実体への解決</summary>
public interface IAssetResolver
{
    /// <summary>参照の行き先のアセット実体の読み込みと解決</summary>
    /// <returns>行き先が無いか型が合わなければ <c>null</c></returns>
    ValueTask<TAsset?> ResolveAsync<TAsset>(AssetReference<TAsset> assetReference, CancellationToken cancellationToken = default)
        where TAsset : class;

    /// <summary>読み込み済みのアセット実体の取得</summary>
    /// <returns>まだ読み込まれていなければ <c>false</c>（読み込みは <see cref="ResolveAsync"/>）</returns>
    bool TryResolve<TAsset>(AssetReference<TAsset> assetReference, out TAsset? asset) where TAsset : class;
}

/// <summary>実体が持つアセット参照を解決させるフック</summary>
/// <remarks>実体に値が入るたび、その実体が使われる前に呼ばれる。使われないまま終わる実体の上でも呼ばれうるので、参照の解決だけを行う。</remarks>
public interface IAssetResolutionHook
{
    /// <summary>内包する参照の解決</summary>
    ValueTask OnResolveAssetsAsync(IAssetResolver resolver);
}
