using EmptyEngine.Modules.Testing;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Assets;
using EmptyEngine.SceneSource.Editor;
using Xunit;

namespace EmptyEngine.SceneSource.Tests;

/// <summary>ソースを <c>.meta</c> ごと移動したときの差分インポートの検証</summary>
public sealed class MovedSourceImportTests : IDisposable
{
    private readonly string _assetsRoot = Path.Combine(Path.GetTempPath(), "ee-move-" + Guid.NewGuid().ToString("N"));
    private readonly AssetCatalog _catalog = new();

    public MovedSourceImportTests() => Directory.CreateDirectory(_assetsRoot);

    public void Dispose()
    {
        try { Directory.Delete(_assetsRoot, recursive: true); } catch { }
    }

    [Fact]
    public async Task Moving_source_with_meta_updates_catalog_path_without_reconverting()
    {
        AssetImportService service = CreateService();
        string path = WriteHealthScene("Thing.scene");
        await service.ImportAllAsync();

        string key = SingleEntry(_catalog).Key.Value;
        Assert.Equal("Thing.scene", SingleEntry(_catalog).DisplayPath);

        string movedDir = Path.Combine(_assetsRoot, "Moved");
        Directory.CreateDirectory(movedDir);
        string movedPath = Path.Combine(movedDir, "Thing.scene");
        File.Move(path, movedPath);
        File.Move(path + ".meta", movedPath + ".meta");

        IReadOnlyList<AssetImportResult> results = await service.ImportIfChangedAsync();

        Assert.Empty(results);
        Assert.Equal("Moved/Thing.scene", SingleEntry(_catalog).DisplayPath);
        Assert.Equal(key, SingleEntry(_catalog).Key.Value);
        Assert.True(_catalog.TryGetSourcePath(key, out string? sourcePath));
        Assert.Equal(movedPath, sourcePath);
    }

    [Fact]
    public async Task Unmoved_sources_still_early_skip()
    {
        AssetImportService service = CreateService();
        WriteHealthScene("Thing.scene");
        await service.ImportAllAsync();

        Assert.Empty(await service.ImportIfChangedAsync());
        Assert.Empty(await service.ImportIfChangedAsync());
    }

    private AssetImportService CreateService()
    {
        string artifacts = Path.Combine(_assetsRoot, ".artifacts");
        var service = new AssetImportService(_catalog, new ProjectAssetLayout(_assetsRoot).Sources, new IAssetImporter[] { new SceneAssetImporter(CatalogStub.Schemas, _catalog) }, Path.Combine(artifacts, "import-stamps"));
        service.SetArtifacts(TestArtifacts.At(Path.Combine(artifacts, "store")));
        return service;
    }

    private string WriteHealthScene(string fileName)
    {
        string path = Path.Combine(_assetsRoot, fileName);
        File.WriteAllText(path, SceneJsonCodec.ToJson(new HierarchyNode("obj-root", "Thing",
            Array.Empty<HierarchyNode>(), AuthoringTestHelpers.Components(new TestHealth { Max = 100, Current = 1 }))));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(-10));
        return path;
    }

    private static CatalogEntry SingleEntry(AssetCatalog catalog) =>
        Assert.Single(catalog.EnumerateAssets());
}
