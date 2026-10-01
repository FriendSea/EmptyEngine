using EmptyEngine.Core;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Assets;
using EmptyEngine.Editor.Authoring;
using Xunit;

namespace EmptyEngine.Tests.Contracts;

/// <summary><see cref="IAssetArtifactStore"/> と <see cref="IAssetResolver"/> が対であることの契約</summary>
/// <typeparam name="TAsset">永続化・解決の対象となるアセット型。</typeparam>
public abstract class AssetRoundTripContract<TAsset> where TAsset : class
{
    protected abstract IAssetArtifactStore CreateSerializer(string rootPath);

    protected abstract IAssetResolver CreateResolver(string rootPath);

    protected abstract TAsset CreateSampleAsset();

    protected abstract void AssertEquivalent(TAsset expected, TAsset actual);

    protected virtual string AssetKey => "round-trip-asset.bin";

    /// <summary>取り込んだ実体を写し取るときに引くカタログ</summary>
    protected abstract ISchemaSource Schemas { get; }

    [Fact]
    public async Task Saved_asset_resolves_back_with_same_value()
    {
        string root = CreateTempRoot();
        try
        {
            IAssetArtifactStore serializer = CreateSerializer(root);
            IAssetResolver resolver = CreateResolver(root);
            Assert.False(resolver.TryLoad(new AssetReference<TAsset>("does-not-exist.bin"), out TAsset? missing));
            Assert.Null(missing);
            TAsset asset = CreateSampleAsset();
            var key = new AssetKey(AssetKey);

            await serializer.SaveAsync(key, ImporterUtils.FromClr(asset, Schemas));

            bool resolved = resolver.TryLoad(new AssetReference<TAsset>(key), out TAsset? actual);

            Assert.True(resolved, "saved asset must be resolvable");
            Assert.NotNull(actual);
            AssertEquivalent(asset, actual!);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempRoot()
    {
        string path = Path.Combine(Path.GetTempPath(), "ee-asset-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
