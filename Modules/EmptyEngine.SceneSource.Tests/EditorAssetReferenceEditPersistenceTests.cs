using EmptyEngine.Modules.Testing;
using EmptyEngine.Core;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Assets;
using EmptyEngine.Editor.ViewModels;
using EmptyEngine.Editor.ViewModels.Inspector;
using EmptyEngine.SceneSource.Editor;
using EmptyEngine.Tests;
using Xunit;

namespace EmptyEngine.SceneSource.Tests;

/// <summary><c>.asset</c> のアセット参照編集がソースへ保存されることの検証</summary>
public sealed class EditorAssetReferenceEditPersistenceTests : IDisposable
{
    private readonly string _assetsRoot = Path.Combine(Path.GetTempPath(), "ee-assetedit-" + Guid.NewGuid().ToString("N"));

    public EditorAssetReferenceEditPersistenceTests() => Directory.CreateDirectory(_assetsRoot);

    public void Dispose()
    {
        try { Directory.Delete(_assetsRoot, recursive: true); } catch { }
    }

    [Fact]
    public async Task Asset_reference_field_edit_saves_back_to_source()
    {
        CatalogStub.Register<TestSpriteHolder>();

        string assetPath = Path.Combine(_assetsRoot, "Holder.asset");
        await File.WriteAllTextAsync(assetPath, """
            { "TypeName": "EmptyEngine.Modules.Testing.TestSpriteHolder" }
            """);

        var catalog = new AssetCatalog();
        var service = new AssetImportService(catalog, _assetsRoot, new IAssetImporter[] { new TypedAssetImporter(CatalogStub.Schemas) });
        await service.ImportAllAsync();
        string key = catalog.EnumerateAssets().Single().Key.Value;

        var logs = new RecordingLogger<EditorViewModel>();
        var vm = EditorFixture.NewEditor(assets: catalog, imports: service, logger: logs);

        vm.SelectAsset(new AssetKey(key));

        Assert.True(vm.Asset.IsEditable);
        FieldViewModel idle = vm.Asset.Inspected!.Fields.Single(f => f.Name == "idle");
        Assert.True(idle.IsAssetReference);

        idle.AssignAssetReference(new AssetKey("sprite-key"));

        await Task.Delay(800);

        string written = await File.ReadAllTextAsync(assetPath);
        Assert.Contains("\"Data\"", written);
        Assert.Contains("sprite-key", written);
        Assert.Contains(logs, l => l.Contains("Saved asset"));
    }

    /// <summary>コピーした値のアセットへの貼り付け</summary>
    /// <remarks>アセットの表示中はそのアセットへ貼り付ける。</remarks>
    [Fact]
    public async Task Copied_values_paste_into_the_inspected_asset()
    {
        CatalogStub.Register<TestSpriteHolder>();

        foreach (string name in new[] { "Source", "Target" })
        {
            await File.WriteAllTextAsync(Path.Combine(_assetsRoot, name + ".asset"), """
                { "TypeName": "EmptyEngine.Modules.Testing.TestSpriteHolder" }
                """);
        }

        var catalog = new AssetCatalog();
        var service = new AssetImportService(catalog, _assetsRoot, new IAssetImporter[] { new TypedAssetImporter(CatalogStub.Schemas) });
        await service.ImportAllAsync();

        string SourceKey(string fileName) =>
            catalog.EnumerateAssets().Single(a => a.DisplayPath.Contains(fileName)).Key.Value;

        var vm = EditorFixture.NewEditor(assets: catalog, imports: service);

        vm.SelectAsset(new AssetKey(SourceKey("Source")));
        vm.Asset.Inspected!.Fields.Single(f => f.Name == "idle").AssignAssetReference(new AssetKey("copied-sprite"));
        vm.CopyComponent(vm.Asset.Inspected!);

        vm.SelectAsset(new AssetKey(SourceKey("Target")));
        Assert.True(vm.CanPasteComponent);
        vm.PasteComponent();

        Assert.Equal(
            "copied-sprite",
            vm.Asset.Inspected!.Fields.Single(f => f.Name == "idle").AssetKey);

        await Task.Delay(300);
        Assert.Contains("copied-sprite", await File.ReadAllTextAsync(Path.Combine(_assetsRoot, "Target.asset")));
    }
}
