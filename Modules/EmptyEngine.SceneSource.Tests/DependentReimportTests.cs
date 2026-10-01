using EmptyEngine.Modules.Testing;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Assets;
using EmptyEngine.SceneSource.Editor;
using Xunit;

namespace EmptyEngine.SceneSource.Tests;

/// <summary>依存アセットの波及再インポートの検証</summary>
public sealed class DependentReimportTests : IDisposable
{
    private readonly string _assetsRoot = Path.Combine(Path.GetTempPath(), "ee-dep-" + Guid.NewGuid().ToString("N"));
    private readonly AssetCatalog _catalog = new();

    public DependentReimportTests() => Directory.CreateDirectory(_assetsRoot);

    public void Dispose()
    {
        try { Directory.Delete(_assetsRoot, recursive: true); } catch { }
    }

    [Fact]
    public async Task Variant_is_reimported_when_its_original_changes()
    {
        AssetImportService service = CreateService();
        (string originalGuid, string originalPath) = WriteHealthScene("Original.scene", current: 100);
        await File.WriteAllTextAsync(Path.Combine(_assetsRoot, "Derived.variant"),
            $"{{ \"Original\": \"{originalGuid}\", \"Overrides\": [] }}");

        await service.ImportAllAsync();
        Assert.Equal(100, CurrentOf(_catalog, "Derived.variant"));

        TouchHealthScene(originalPath, current: 25);

        IReadOnlyList<AssetImportResult> results = await service.ImportIfChangedAsync();

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.Success, r.Message));
        Assert.Equal(25, CurrentOf(_catalog, "Derived.variant"));
    }

    [Fact]
    public async Task Host_scene_is_reimported_when_nested_prefab_changes()
    {
        AssetImportService service = CreateService();
        (string prefabGuid, string prefabPath) = WriteHealthScene("Child.scene", current: 100);
        await File.WriteAllTextAsync(Path.Combine(_assetsRoot, "Host.scene"),
            HostSceneJson("host-root", prefabGuid));

        await service.ImportAllAsync();
        Assert.Equal(100, CurrentOf(_catalog, "Host.scene"));

        TouchHealthScene(prefabPath, current: 42);

        IReadOnlyList<AssetImportResult> results = await service.ImportIfChangedAsync();

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.Success, r.Message));
        Assert.Equal(42, CurrentOf(_catalog, "Host.scene"));
    }

    [Fact]
    public async Task Reimport_cascades_transitively_through_bake_chain()
    {
        AssetImportService service = CreateService();
        (string leafGuid, string leafPath) = WriteHealthScene("Leaf.scene", current: 100);

        string midPath = Path.Combine(_assetsRoot, "Mid.scene");
        await File.WriteAllTextAsync(midPath, HostSceneJson("mid-root", leafGuid));
        string midGuid = WriteMeta(midPath);

        await File.WriteAllTextAsync(Path.Combine(_assetsRoot, "Top.scene"),
            HostSceneJson("top-root", midGuid));

        await service.ImportAllAsync();
        Assert.Equal(100, CurrentOf(_catalog, "Top.scene"));

        TouchHealthScene(leafPath, current: 7);

        IReadOnlyList<AssetImportResult> results = await service.ImportIfChangedAsync();

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.True(r.Success, r.Message));
        Assert.Equal(7, CurrentOf(_catalog, "Top.scene"));
    }

    [Fact]
    public async Task Unrelated_sources_are_not_reimported_by_cascade()
    {
        AssetImportService service = CreateService();
        (_, string changedPath) = WriteHealthScene("Changed.scene", current: 100);
        WriteHealthScene("Unrelated.scene", current: 100);

        await service.ImportAllAsync();

        TouchHealthScene(changedPath, current: 1);

        IReadOnlyList<AssetImportResult> results = await service.ImportIfChangedAsync();

        AssetImportResult single = Assert.Single(results);
        Assert.True(single.Success, single.Message);
        Assert.Equal("Changed.scene", single.Asset!.RelativePath);
    }

    [Fact]
    public async Task ImportSingle_cascades_to_dependents()
    {
        AssetImportService service = CreateService();
        (string originalGuid, string originalPath) = WriteHealthScene("Original.scene", current: 100);
        await File.WriteAllTextAsync(Path.Combine(_assetsRoot, "Derived.variant"),
            $"{{ \"Original\": \"{originalGuid}\", \"Overrides\": [] }}");

        await service.ImportAllAsync();

        TouchHealthScene(originalPath, current: 4);
        AssetImportResult result = await service.ImportSingleAsync(originalPath);

        Assert.True(result.Success, result.Message);
        Assert.Equal(4, CurrentOf(_catalog, "Derived.variant"));
    }

    private AssetImportService CreateService()
    {
        string artifacts = Path.Combine(_assetsRoot, ".artifacts");
        var service = new AssetImportService(
            _catalog,
            _assetsRoot,
            new IAssetImporter[] { new SceneAssetImporter(CatalogStub.Schemas), new PrefabVariantImporter(CatalogStub.Schemas) },
            stampRootPath: Path.Combine(artifacts, "import-stamps"));
        service.SetArtifacts(TestArtifacts.At(Path.Combine(artifacts, "store")));
        return service;
    }

    private (string guid, string path) WriteHealthScene(string fileName, int current)
    {
        string path = Path.Combine(_assetsRoot, fileName);
        File.WriteAllText(path, HealthSceneJson(current));
        return (WriteMeta(path), path);
    }

    private static string HealthSceneJson(int current) =>
        SceneJsonCodec.ToJson(new HierarchyNode("obj-root", "Thing", Array.Empty<HierarchyNode>(),
            AuthoringTestHelpers.Components(new TestHealth { Max = 100, Current = current })));

    private static string HostSceneJson(string rootId, string prefabGuid) => $$"""
        {
          "Id": "{{rootId}}",
          "Name": "Host",
          "Components": [],
          "Children": [ { "Id": "leaf1", "Prefab": "{{prefabGuid}}", "Overrides": [] } ]
        }
        """;

    private static string WriteMeta(string sourcePath)
    {
        string guid = Guid.NewGuid().ToString("N");
        File.WriteAllText(sourcePath + ".meta", $"{{\"guid\":\"{guid}\"}}");
        return guid;
    }

    private static void TouchHealthScene(string path, int current)
    {
        File.WriteAllText(path, HealthSceneJson(current));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
    }

    private static int CurrentOf(AssetCatalog catalog, string relativePath)
    {
        ImportedSource asset = catalog.EnumerateAssets()
            .Select(entry =>
            {
                Assert.True(catalog.TryGetImportedAsset(entry.Key.Value, out ImportedSource? found));
                return found!;
            })
            .Single(a => a.RelativePath == relativePath);

        HierarchyNode root = AuthoringTestHelpers.RootOf(asset);
        AuthoringObject? health = FindHealth(root);
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
