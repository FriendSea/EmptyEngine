using EmptyEngine.Modules.Testing;
using EmptyEngine.Core;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Assets;
using EmptyEngine.Editor.ViewModels;
using EmptyEngine.Editor.ViewModels.Inspector;
using EmptyEngine.ObjectModel;
using EmptyEngine.SceneSource.Editor;
using EmptyEngine.Tests;
using Xunit;

namespace EmptyEngine.SceneSource.Tests;

/// <summary>QuitEvent と同形の <c>.asset</c> の編集保存の検証</summary>
public sealed class QuitEventShapeEditPersistenceTests : IDisposable
{
    private readonly string _assetsRoot = Path.Combine(Path.GetTempPath(), "ee-quitshape-" + Guid.NewGuid().ToString("N"));

    public QuitEventShapeEditPersistenceTests() => Directory.CreateDirectory(_assetsRoot);

    public void Dispose()
    {
        try { Directory.Delete(_assetsRoot, recursive: true); } catch { }
    }

    [Fact]
    public async Task Scene_reference_field_edit_saves_back_to_source()
    {
        CatalogStub.Register<TestQuitLikeEvent>();

        string assetPath = Path.Combine(_assetsRoot, "Quit.asset");
        await File.WriteAllTextAsync(assetPath, """
            { "TypeName": "EmptyEngine.SceneSource.Tests.TestQuitLikeEvent", "Data": {} }
            """);

        var sceneImporter = new SceneAssetImporter(CatalogStub.Schemas);
        var sceneRoot = new HierarchyNode("root", "StageSelect", Array.Empty<HierarchyNode>());
        await sceneImporter.SaveAsync(sceneRoot, Path.Combine(_assetsRoot, "StageSelect.scene"));

        var catalog = new AssetCatalog();
        var service = new AssetImportService(catalog, _assetsRoot, new IAssetImporter[] { new TypedAssetImporter(CatalogStub.Schemas), sceneImporter });
        await service.ImportAllAsync();
        string key = catalog.EnumerateAssets().Single(e => e.DisplayPath.EndsWith("Quit.asset")).Key.Value;

        var logs = new RecordingLogger<EditorViewModel>();
        var vm = EditorFixture.NewEditor(assets: catalog, imports: service, logger: logs);

        vm.SelectAsset(new AssetKey(key));

        Assert.True(vm.Asset.IsEditable);
        FieldViewModel field = vm.Asset.Inspected!.Fields.Single(f => f.Name == "stageSelect");
        Assert.True(field.IsAssetReference);

        IReadOnlyList<AssetCandidate> candidates = vm.GetAssetCandidates(field.AssetTypeConstraint);
        AssetCandidate scene = Assert.Single(candidates);
        Assert.False(string.IsNullOrEmpty(scene.Key.Value));
        field.AssignAssetReference(scene.Key);

        await Task.Delay(800);

        string written = await File.ReadAllTextAsync(assetPath);
        Assert.Contains(scene.Key.Value, written);
        Assert.Contains(logs, l => l.Contains("Saved asset"));
    }
}

/// <summary>QuitEvent と同形のテスト用イベント</summary>
[Asset]
public sealed class TestQuitLikeEvent
{
    public AssetReference<IObject> stageSelect;
}
