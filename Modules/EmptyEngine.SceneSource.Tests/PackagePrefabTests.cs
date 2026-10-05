using EmptyEngine.Modules.Testing;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Assets;
using EmptyEngine.SceneSource.Editor;
using Xunit;

namespace EmptyEngine.SceneSource.Tests;

/// <summary>パッケージが同梱するプレハブを、プロジェクトのシーンから参照する取り込み</summary>
public sealed class PackagePrefabTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ee-pkg-prefab-" + Guid.NewGuid().ToString("N"));
    private readonly string _assetsRoot;
    private readonly string _packageRoot;
    private readonly AssetCatalog _catalog = new();

    public PackagePrefabTests()
    {
        _assetsRoot = Path.Combine(_root, "Assets");
        _packageRoot = Path.Combine(_root, "Package");
        Directory.CreateDirectory(_assetsRoot);
        Directory.CreateDirectory(_packageRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Project_scene_nests_a_prefab_shipped_by_a_package()
    {
        string prefabGuid = WritePackagePrefab(current: 30);
        await File.WriteAllTextAsync(Path.Combine(_assetsRoot, "Host.scene"), $$"""
            {
              "Id": "host-root",
              "Name": "Host",
              "Components": [],
              "Children": [ { "Id": "leaf1", "Prefab": "{{prefabGuid}}", "Overrides": [] } ]
            }
            """);

        IReadOnlyList<AssetImportResult> results = await CreateService().ImportAllAsync();

        Assert.All(results, r => Assert.True(r.Success, r.Message));
        Assert.Equal(30, CurrentOf("Host.scene"));
    }

    [Fact]
    public async Task Project_variant_derives_from_a_prefab_shipped_by_a_package()
    {
        string prefabGuid = WritePackagePrefab(current: 30);
        await File.WriteAllTextAsync(Path.Combine(_assetsRoot, "Derived.variant"),
            $"{{ \"Original\": \"{prefabGuid}\", \"Overrides\": [] }}");

        IReadOnlyList<AssetImportResult> results = await CreateService().ImportAllAsync();

        Assert.All(results, r => Assert.True(r.Success, r.Message));
        Assert.Equal(30, CurrentOf("Derived.variant"));
    }

    private AssetImportService CreateService()
    {
        string artifacts = Path.Combine(_root, ".artifacts");
        var service = new AssetImportService(
            _catalog,
            new ProjectAssetLayout(_assetsRoot, [ProjectAssetLayout.Package("Probe.Pack", _packageRoot)]).Sources,
            new IAssetImporter[] { new SceneAssetImporter(CatalogStub.Schemas, _catalog), new PrefabVariantImporter(CatalogStub.Schemas, _catalog) },
            Path.Combine(artifacts, "import-stamps"));
        service.SetArtifacts(TestArtifacts.At(Path.Combine(artifacts, "store")));
        return service;
    }

    private string WritePackagePrefab(int current)
    {
        string path = Path.Combine(_packageRoot, "Thing.scene");
        File.WriteAllText(path, SceneJsonCodec.ToJson(new HierarchyNode("obj-root", "Thing", Array.Empty<HierarchyNode>(),
            AuthoringTestHelpers.Components(new TestHealth { Max = 100, Current = current }))));

        string guid = Guid.NewGuid().ToString("N");
        File.WriteAllText(path + ".meta", $"{{\"guid\":\"{guid}\"}}");
        return guid;
    }

    private int CurrentOf(string relativePath)
    {
        ImportedSource asset = _catalog.EnumerateAssets()
            .Select(entry =>
            {
                Assert.True(_catalog.TryGetImportedAsset(entry.Key.Value, out ImportedSource? found));
                return found!;
            })
            .Single(a => a.RelativePath == relativePath);

        AuthoringObject? health = FindHealth(AuthoringTestHelpers.RootOf(asset));
        Assert.NotNull(health);
        return AuthoringTestHelpers.GetInt(health, nameof(TestHealth.Current));
    }

    private static AuthoringObject? FindHealth(HierarchyNode node)
    {
        foreach (AuthoringObject component in node.Components)
        {
            if (AuthoringTestHelpers.IsType<TestHealth>(component)) return component;
        }
        foreach (HierarchyNode child in node.Children)
        {
            if (FindHealth(child) is { } health) return health;
        }
        return null;
    }
}
