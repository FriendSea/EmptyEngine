using EmptyEngine.Modules.Testing;
using EmptyEngine.Core;
using EmptyEngine.Editor.Assets;
using EmptyEngine.Editor.Distribution;
using EmptyEngine.ObjectModel;
using EmptyEngine.Storage;
using Xunit;

namespace EmptyEngine.World.Tests;

/// <summary><see cref="DistributionPipeline.DeployReachable"/> の依存トレース配布の検証</summary>
public sealed class DeployReachableTests
{
    /// <summary>アセット参照を持つ＝アセット→アセットの辺を作るテスト用アセット</summary>
    [Asset]
    public sealed class TestMaterial
    {
        public AssetReference<TestAsset> Texture { get; set; }
    }

    [Fact]
    public async Task Deploys_only_reachable_assets_following_transitive_references()
    {
        string source = CreateTempDir();
        string dest = CreateTempDir();
        try
        {
            var assetSerializer = TestArtifacts.At(source);

            await assetSerializer.SaveAssetAsync(
                new AssetKey("mat.material"),
                ImporterUtils.FromClr(new TestMaterial { Texture = new AssetReference<TestAsset>("tex.png") }, CatalogStub.Schemas));
            await assetSerializer.SaveAssetAsync(new AssetKey("tex.png"), ImporterUtils.FromClr(new TestAsset("pixels"), CatalogStub.Schemas));
            await assetSerializer.SaveAssetAsync(new AssetKey("orphan.png"), ImporterUtils.FromClr(new TestAsset("unused"), CatalogStub.Schemas));

            byte[] sceneBlob = SceneBlob.Encode([SceneReferencing("mat.material")]);
            File.WriteAllBytes(Path.Combine(source, ImportedAssetsLayout.ArtifactPath("main.scene")), sceneBlob);

            IReadOnlyList<string> copied = DistributionPipeline.DeployReachable(
                assetSerializer,
                TestArtifacts.At(dest),
                new[] { new AssetKey("main.scene") });

            Assert.Equal(
                new[] { "main.scene", "mat.material", "tex.png" },
                copied.OrderBy(p => p).ToArray());

            Assert.True(File.Exists(Path.Combine(dest, ImportedAssetsLayout.ArtifactPath("main.scene"))));
            Assert.True(File.Exists(Path.Combine(dest, ImportedAssetsLayout.ArtifactPath("mat.material"))));
            Assert.True(File.Exists(Path.Combine(dest, ImportedAssetsLayout.ArtifactPath("tex.png"))));
            Assert.False(File.Exists(Path.Combine(dest, ImportedAssetsLayout.ArtifactPath("orphan.png"))));
        }
        finally
        {
            Directory.Delete(source, recursive: true);
            Directory.Delete(dest, recursive: true);
        }
    }

    [Fact]
    public async Task Follows_references_nested_inside_referenced_prefab_scenes()
    {
        string source = CreateTempDir();
        string dest = CreateTempDir();
        try
        {
            var assetSerializer = TestArtifacts.At(source);
            await assetSerializer.SaveAssetAsync(new AssetKey("deep.png"), ImporterUtils.FromClr(new TestAsset("pixels"), CatalogStub.Schemas));

            File.WriteAllBytes(Path.Combine(source, ImportedAssetsLayout.ArtifactPath("prefab.scene")), SceneBlob.Encode([SceneReferencing("deep.png")]));
            File.WriteAllBytes(Path.Combine(source, ImportedAssetsLayout.ArtifactPath("main.scene")), SceneBlob.Encode([SceneReferencing("prefab.scene")]));

            IReadOnlyList<string> copied = DistributionPipeline.DeployReachable(
                assetSerializer,
                TestArtifacts.At(dest),
                new[] { new AssetKey("main.scene") });

            Assert.Equal(
                new[] { "deep.png", "main.scene", "prefab.scene" },
                copied.OrderBy(p => p).ToArray());
        }
        finally
        {
            Directory.Delete(source, recursive: true);
            Directory.Delete(dest, recursive: true);
        }
    }

    [Fact]
    public void Returns_empty_when_no_startup_scene()
    {
        string source = CreateTempDir();
        string dest = CreateTempDir();
        try
        {
            IReadOnlyList<string> copied = DistributionPipeline.DeployReachable(
                TestArtifacts.At(source),
                TestArtifacts.At(dest),
                Array.Empty<AssetKey>());

            Assert.Empty(copied);
        }
        finally
        {
            Directory.Delete(source, recursive: true);
            Directory.Delete(dest, recursive: true);
        }
    }

    private static RootInstanceData SceneReferencing(string assetKey) => new("deploy-inst",
        new ObjectData("obj-1", "Root",
        [
            new ComponentData(typeof(TestSprite).FullName!, new TestSprite { Asset = new AssetReference<TestAsset>(assetKey) }),
        ],
        []));

    private static string CreateTempDir()
    {
        string path = Path.Combine(Path.GetTempPath(), "ee-deploy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
