using EmptyEngine.Modules.Testing;
using System.Text.Json;
using EmptyEngine.Core;
using EmptyEngine.Storage;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Assets;
using EmptyEngine.Editor.Distribution;
using EmptyEngine.SceneSource.Editor;
using EmptyEngine.World.Editor;
using Xunit;

namespace EmptyEngine.World.Tests;

/// <summary>デプロイ対象シーンリストと起動シーンマニフェストの検証</summary>
public sealed class StartupManifestTests
{
    [Fact]
    public void Find_uses_manifest_only_no_key_order_fallback()
    {
        string root = CreateTempDir();
        try
        {
            File.WriteAllBytes(Path.Combine(root, ImportedAssetsLayout.ArtifactPath("aaaa")), new byte[] { 1 });
            File.WriteAllBytes(Path.Combine(root, ImportedAssetsLayout.ArtifactPath("zzzz")), new byte[] { 2 });
            var store = AssetStorage.FromDirectory(root);

            Assert.Null(store.FindFirstNow());
            Assert.Empty(store.ReadStartupScenesNow());

            WriteManifest(root, "zzzz", "aaaa");
            Assert.Equal("zzzz", store.FindFirstNow()?.Value);
            Assert.Equal(new[] { "zzzz", "aaaa" }, store.ReadStartupScenesNow().Select(r => r.Value).ToArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FindAll_skips_missing_keys_and_keeps_author_order()
    {
        string root = CreateTempDir();
        try
        {
            File.WriteAllBytes(Path.Combine(root, ImportedAssetsLayout.ArtifactPath("present.scene")), new byte[] { 1 });
            var store = AssetStorage.FromDirectory(root);

            WriteManifest(root, "gone.scene", "present.scene");
            Assert.Equal(new[] { "present.scene" }, store.ReadStartupScenesNow().Select(r => r.Value).ToArray());
            Assert.Equal("present.scene", store.FindFirstNow()?.Value);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Deploy_writes_startup_manifest_in_build_scene_list_order()
    {
        string projectDir = CreateTempDir();
        string deployExe = CreateTempDir();
        string dest = Path.Combine(deployExe, "ImportedAssets");
        try
        {
            string assetsRoot = Path.Combine(projectDir, "Assets");
            string outputRoot = Path.Combine(projectDir, ".artifacts", "ImportedAssets");
            Directory.CreateDirectory(assetsRoot);

            var importer = new SceneAssetImporter(CatalogStub.Schemas);
            await importer.SaveAsync(MinimalScene("First"), Path.Combine(assetsRoot, "First.scene"));
            await importer.SaveAsync(MinimalScene("Second"), Path.Combine(assetsRoot, "Second.scene"));

            var catalog = new AssetCatalog();
            var service = new AssetImportService(catalog, assetsRoot, new IAssetImporter[] { importer });
            service.SetArtifacts(TestArtifacts.At(outputRoot));
            await service.ImportAllAsync();

            var keysByName = catalog.EnumerateAssets()
                .ToDictionary(e => Path.GetFileNameWithoutExtension(e.DisplayPath), e => e.Key.Value);
            AssetKey[] deployScenes =
            [
                new AssetKey(keysByName["Second"]),
                new AssetKey(keysByName["First"]),
            ];

            DistributionPipeline.DeployReachable(
                TestArtifacts.At(outputRoot),
                TestArtifacts.At(dest),
                deployScenes);
            new DistributionBuilder().Build(deployExe, deployScenes);

            Assert.False(Directory.Exists(dest));
            using var store = AssetStorage.FromArchive(ImportedAssetsLayout.ResolveDistributionArchive(deployExe));
            Assert.True(store.CanOpenNow(StartupScene.ManifestKey));
            Assert.Equal(
                new[] { keysByName["Second"], keysByName["First"] },
                store.ReadStartupScenesNow().Select(r => r.Value).ToArray());
            Assert.Equal(keysByName["Second"], store.FindFirstNow()?.Value);
        }
        finally
        {
            Directory.Delete(projectDir, recursive: true);
            Directory.Delete(deployExe, recursive: true);
        }
    }

    [Fact]
    public async Task Deploy_bundles_all_listed_scenes_plus_reachables_and_manifest()
    {
        string source = CreateTempDir();
        string deployExe = CreateTempDir();
        string dest = Path.Combine(deployExe, "ImportedAssets");
        try
        {
            var assetSerializer = TestArtifacts.At(source);
            await assetSerializer.SaveAssetAsync(new AssetKey("tex-a.png"), ImporterUtils.FromClr(new TestAsset("a"), CatalogStub.Schemas));
            await assetSerializer.SaveAssetAsync(new AssetKey("tex-b.png"), ImporterUtils.FromClr(new TestAsset("b"), CatalogStub.Schemas));

            File.WriteAllBytes(Path.Combine(source, ImportedAssetsLayout.ArtifactPath("a.scene")), SceneBlob.Encode([SceneReferencing("tex-a.png")]));
            File.WriteAllBytes(Path.Combine(source, ImportedAssetsLayout.ArtifactPath("b.scene")), SceneBlob.Encode([SceneReferencing("tex-b.png")]));
            File.WriteAllBytes(Path.Combine(source, ImportedAssetsLayout.ArtifactPath("unlisted.scene")), SceneBlob.Encode([SceneReferencing("tex-a.png")]));

            AssetKey[] deployScenes = [new AssetKey("a.scene"), new AssetKey("b.scene")];
            IReadOnlyList<string> copied = DistributionPipeline.DeployReachable(
                TestArtifacts.At(source),
                TestArtifacts.At(dest),
                deployScenes);
            new DistributionBuilder().Build(deployExe, deployScenes);

            Assert.Equal(
                new[] { "a.scene", "b.scene", "tex-a.png", "tex-b.png" },
                copied.OrderBy(p => p, StringComparer.Ordinal).ToArray());

            Assert.False(Directory.Exists(dest));
            using var store = AssetStorage.FromArchive(ImportedAssetsLayout.ResolveDistributionArchive(deployExe));
            Assert.False(store.CanOpenNow("unlisted.scene"));
            Assert.True(store.CanOpenNow(StartupScene.ManifestKey));
            Assert.Equal("a.scene", store.FindFirstNow()?.Value);
        }
        finally
        {
            Directory.Delete(source, recursive: true);
            Directory.Delete(deployExe, recursive: true);
        }
    }

    [Fact]
    public void LoadAll_applies_every_listed_scene_in_one_world()
    {
        string root = CreateTempDir();
        try
        {
            File.WriteAllBytes(Path.Combine(root, ImportedAssetsLayout.ArtifactPath("a.scene")), SceneBlob.Encode([RootNamed("A")]));
            File.WriteAllBytes(Path.Combine(root, ImportedAssetsLayout.ArtifactPath("b.scene")), SceneBlob.Encode([RootNamed("B")]));
            File.WriteAllBytes(Path.Combine(root, ImportedAssetsLayout.ArtifactPath("unlisted.scene")), SceneBlob.Encode([RootNamed("Unlisted")]));
            WriteManifest(root, "a.scene", "b.scene");

            using var store = AssetStorage.FromDirectory(root);
            var world = new SceneWorld();
            IReadOnlyList<AssetKey> loaded = world.LoadAllIntoNow(store);

            Assert.Equal(new[] { "a.scene", "b.scene" }, loaded.Select(r => r.Value).ToArray());
            Assert.Equal(new[] { "A", "B" }, world.LoadedSceneNames.ToArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LoadAll_gives_each_loaded_scene_its_own_instance_id()
    {
        string root = CreateTempDir();
        try
        {
            File.WriteAllBytes(Path.Combine(root, ImportedAssetsLayout.ArtifactPath("a.scene")), SceneBlob.Encode([RootNamed("A")]));
            WriteManifest(root, "a.scene", "a.scene");

            using var store = AssetStorage.FromDirectory(root);
            var world = new SceneWorld();
            world.LoadAllIntoNow(store);

            Assert.Equal(2, world.Roots.Count);
            string[] instanceIds = SceneBlob.Decode(world.SerializeScene()).Select(r => r.InstanceId).ToArray();
            Assert.Equal(2, instanceIds.Distinct(StringComparer.Ordinal).Count());
            Assert.DoesNotContain(string.Empty, instanceIds);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LoadAll_without_manifest_leaves_world_empty()
    {
        string root = CreateTempDir();
        try
        {
            File.WriteAllBytes(Path.Combine(root, ImportedAssetsLayout.ArtifactPath("a.scene")), SceneBlob.Encode([RootNamed("A")]));

            using var store = AssetStorage.FromDirectory(root);
            var world = new SceneWorld();

            Assert.Empty(world.LoadAllIntoNow(store));
            Assert.Empty(world.Roots);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static RootInstanceData RootNamed(string name) =>
        new(string.Empty, new ObjectData("root-1", name, [], []));

    private static void WriteManifest(string root, params string[] keys) =>
        File.WriteAllText(Path.Combine(root, ImportedAssetsLayout.ArtifactPath(StartupScene.ManifestKey)), JsonSerializer.Serialize(keys));

    private static HierarchyNode MinimalScene(string name) =>
        new("ignored", name, new[] { new HierarchyNode("obj", "Object") });

    private static RootInstanceData SceneReferencing(string assetKey) => new("startup-inst",
        new ObjectData("obj-1", "Root",
        [
            new ComponentData(typeof(TestSprite).FullName!, new TestSprite { Asset = new AssetReference<TestAsset>(assetKey) }),
        ],
        []));

    private static string CreateTempDir()
    {
        string path = Path.Combine(Path.GetTempPath(), "ee-startup-manifest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
