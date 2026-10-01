using EmptyEngine.Core;
using Xunit;

namespace EmptyEngine.Tests;

/// <summary>同期に完了するストアを使うテストのための待ち合わせ</summary>
/// <remarks>完了していなければ失敗させる。</remarks>
public static class SyncTestExtensions
{
    /// <summary>読み込みまで済ませる <see cref="IAssetResolver.TryResolve"/></summary>
    public static bool TryLoad<TAsset>(this IAssetResolver resolver, AssetReference<TAsset> reference, out TAsset? asset)
        where TAsset : class
    {
        ValueTask<TAsset?> loading = resolver.ResolveAsync(reference);
        Assert.True(loading.IsCompleted, "The test store is expected to load synchronously.");
        asset = loading.GetAwaiter().GetResult();
        return asset is not null;
    }

    /// <summary>適用の完了まで済ませる <see cref="ISceneSerializer.DeserializeSceneAsync"/></summary>
    public static void DeserializeSceneNow(this ISceneSerializer serializer, byte[] blob, bool isPlaying)
    {
        Task applying = serializer.DeserializeSceneAsync(blob, isPlaying);
        Assert.True(applying.IsCompleted, "The test scene is expected to apply synchronously.");
        applying.GetAwaiter().GetResult();
    }
}
