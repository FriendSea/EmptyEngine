using EmptyEngine.Modules.Testing;
using EmptyEngine.Editor;
using EmptyEngine.SceneSource.Editor;
using Xunit;

namespace EmptyEngine.SceneSource.Tests;

/// <summary>プレハブバリアント（<c>.variant</c>）の検証</summary>
public sealed class PrefabVariantTests : IDisposable
{
    private readonly string _assetsRoot = Path.Combine(Path.GetTempPath(), "ee-variant-" + Guid.NewGuid().ToString("N"));

    public PrefabVariantTests() => Directory.CreateDirectory(_assetsRoot);

    public void Dispose()
    {
        try { Directory.Delete(_assetsRoot, recursive: true); } catch { }
    }

    private (string sceneKeyGuid, string scenePath) WriteOriginal()
    {
        var child = new HierarchyNode("obj-player", "Player", Array.Empty<HierarchyNode>(),
            AuthoringTestHelpers.Components(new TestHealth { Max = 100, Current = 100 }));
        var root = new HierarchyNode("obj-root", "Health", new[] { child });

        string scenePath = Path.Combine(_assetsRoot, "Health.scene");
        File.WriteAllText(scenePath, SceneJsonCodec.ToJson(root));

        string guid = Guid.NewGuid().ToString("N");
        File.WriteAllText(scenePath + ".meta", $"{{\"guid\":\"{guid}\"}}");
        return (guid, scenePath);
    }

    [Fact]
    public async Task Import_bakes_original_and_applies_overrides()
    {
        (string guid, _) = WriteOriginal();

        string variantSource = $$"""
        {
          "Original": "{{guid}}",
          "Overrides": [
            { "Object": "obj-player", "ComponentIndex": 0, "TypeName": "EmptyEngine.Modules.Testing.TestHealth", "Field": "Current", "Value": 25 }
          ]
        }
        """;
        string variantPath = Path.Combine(_assetsRoot, "LowHealth.variant");
        await File.WriteAllTextAsync(variantPath, variantSource);

        var importer = new PrefabVariantImporter(CatalogStub.Schemas, TestSources.In(_assetsRoot));
        AssetImportResult result = await importer.ImportAsync(
            new AssetImportRequest(variantPath, "LowHealth.variant"));

        Assert.True(result.Success, result.Message);
        var root = AuthoringTestHelpers.SceneOf(result);
        AuthoringObject health = root.Children.Single().Components.Single();

        Assert.Equal(25, AuthoringTestHelpers.GetInt(health, nameof(TestHealth.Current)));
        Assert.Equal(100, AuthoringTestHelpers.GetInt(health, nameof(TestHealth.Max)));
    }

    [Fact]
    public async Task Save_writes_only_the_differing_field()
    {
        (string guid, _) = WriteOriginal();

        string variantPath = Path.Combine(_assetsRoot, "LowHealth.variant");
        await File.WriteAllTextAsync(variantPath, $"{{ \"Original\": \"{guid}\", \"Overrides\": [] }}");

        var importer = new PrefabVariantImporter(CatalogStub.Schemas, TestSources.In(_assetsRoot));
        await importer.ImportAsync(new AssetImportRequest(variantPath, "LowHealth.variant"));

        var edited = new HierarchyNode("obj-root", "Health", new[]
        {
            new HierarchyNode("obj-player", "Player", Array.Empty<HierarchyNode>(),
                AuthoringTestHelpers.Components(new TestHealth { Max = 100, Current = 25 })),
        });

        await importer.SaveAsync(edited, variantPath);
        string saved = await File.ReadAllTextAsync(variantPath);

        Assert.Contains("\"Original\": \"" + guid + "\"", saved);
        Assert.Contains("\"Field\": \"Current\"", saved);
        Assert.Contains("25", saved);
        Assert.DoesNotContain("\"Field\": \"Max\"", saved);
    }

    [Fact]
    public async Task Import_then_save_then_import_is_stable()
    {
        (string guid, _) = WriteOriginal();

        string variantPath = Path.Combine(_assetsRoot, "LowHealth.variant");
        await File.WriteAllTextAsync(variantPath, $$"""
        {
          "Original": "{{guid}}",
          "Overrides": [
            { "Object": "obj-player", "ComponentIndex": 0, "TypeName": "EmptyEngine.Modules.Testing.TestHealth", "Field": "Current", "Value": 25 }
          ]
        }
        """);

        var importer = new PrefabVariantImporter(CatalogStub.Schemas, TestSources.In(_assetsRoot));
        var request = new AssetImportRequest(variantPath, "LowHealth.variant");

        var imported = AuthoringTestHelpers.SceneOf(await importer.ImportAsync(request));
        await importer.SaveAsync(imported, variantPath);
        var reimported = AuthoringTestHelpers.SceneOf(await importer.ImportAsync(request));

        AuthoringObject health = reimported.Children.Single().Components.Single();
        Assert.Equal(25, AuthoringTestHelpers.GetInt(health, nameof(TestHealth.Current)));
        Assert.Equal(100, AuthoringTestHelpers.GetInt(health, nameof(TestHealth.Max)));
    }

    [Fact]
    public async Task Create_then_import_yields_original_unchanged()
    {
        (string guid, _) = WriteOriginal();

        var importer = new PrefabVariantImporter(CatalogStub.Schemas, TestSources.In(_assetsRoot));
        string variantPath = Path.Combine(_assetsRoot, "HealthVariant.variant");

        await importer.CreateAsync(guid, variantPath);

        string source = await File.ReadAllTextAsync(variantPath);
        Assert.Contains(guid, source);

        var root = AuthoringTestHelpers.SceneOf(await importer.ImportAsync(
            new AssetImportRequest(variantPath, "HealthVariant.variant")));
        AuthoringObject health = root.Children.Single().Components.Single();
        Assert.Equal(100, AuthoringTestHelpers.GetInt(health, nameof(TestHealth.Current)));
        Assert.Equal(100, AuthoringTestHelpers.GetInt(health, nameof(TestHealth.Max)));
    }

    [Fact]
    public async Task Missing_original_fails_import()
    {
        string variantPath = Path.Combine(_assetsRoot, "Orphan.variant");
        await File.WriteAllTextAsync(variantPath, "{ \"Original\": \"deadbeef\", \"Overrides\": [] }");

        AssetImportResult result = await new PrefabVariantImporter(CatalogStub.Schemas, TestSources.In(_assetsRoot)).ImportAsync(
            new AssetImportRequest(variantPath, "Orphan.variant"));

        Assert.False(result.Success);
    }
}
