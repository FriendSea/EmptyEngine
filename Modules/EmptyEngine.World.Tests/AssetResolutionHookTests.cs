using EmptyEngine.Modules.Testing;
using EmptyEngine.Core;
using EmptyEngine.Editor.Assets;
using EmptyEngine.Storage;
using EmptyEngine.ObjectModel;
using EmptyEngine.Tests;
using Xunit;

namespace EmptyEngine.World.Tests;

/// <summary><see cref="IAssetResolutionHook"/> の発火の契約</summary>
public sealed class AssetResolutionHookTests
{
    [Fact]
    public async Task Materialized_asset_gets_OnResolveAssets_once_and_resolves_nested_reference()
    {
        string root = CreateTempDir();
        try
        {
            var serializer = TestArtifacts.At(root);

            await serializer.SaveAssetAsync(new AssetKey("inner.txt"), ImporterUtils.FromClr(new TestAsset("nested!"), CatalogStub.Schemas));
            var outerRef = new AssetKey("outer.bin");
            await serializer.SaveAssetAsync(
                outerRef,
                ImporterUtils.FromClr(new TestNestedAsset { Inner = AssetReference<TestAsset>.FromKey("inner.txt") }, CatalogStub.Schemas));

            var resolver = new WorldAssetResolver(AssetStorage.FromDirectory(root));

            Assert.True(resolver.TryLoad(new AssetReference<TestNestedAsset>(outerRef), out TestNestedAsset? resolved));
            Assert.NotNull(resolved);
            Assert.Equal(1, resolved!.ResolveCount);
            Assert.NotNull(resolved.Resolved);
            Assert.Equal("nested!", resolved.Resolved!.Text);

            Assert.True(resolver.TryResolve(new AssetReference<TestNestedAsset>(outerRef), out TestNestedAsset? cached));
            Assert.Same(resolved, cached);
            Assert.Equal(1, resolved.ResolveCount);

            await serializer.SaveAssetAsync(new AssetKey("inner.txt"), ImporterUtils.FromClr(new TestAsset("nested again!"), CatalogStub.Schemas));
            resolver.RequestUpdate(new AssetKey("inner.txt"));
            resolver.RequestUpdate(outerRef);

            Assert.True(resolver.TryResolve(new AssetReference<TestNestedAsset>(outerRef), out TestNestedAsset? updated));
            Assert.Same(resolved, updated);
            Assert.Equal(2, resolved.ResolveCount);
            Assert.Equal("nested again!", resolved.Resolved!.Text);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDir()
    {
        string path = Path.Combine(Path.GetTempPath(), "ee-asset-hook-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}

/// <summary>内包参照をフックで解決するテスト用アセット</summary>
[Asset]
public sealed class TestNestedAsset : IAssetResolutionHook
{
    public AssetReference<TestAsset> Inner { get; set; } = new();

    private TestAsset? _resolved;
    private int _resolveCount;

    public TestAsset? Resolved => _resolved;
    public int ResolveCount => _resolveCount;

    public async ValueTask OnResolveAssetsAsync(IAssetResolver resolver)
    {
        _resolveCount++;
        _resolved = await resolver.ResolveAsync(Inner);
    }
}
