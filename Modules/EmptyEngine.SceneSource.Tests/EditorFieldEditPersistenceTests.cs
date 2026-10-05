using EmptyEngine.Modules.Testing;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Assets;
using EmptyEngine.Editor.ViewModels.Hierarchy;
using EmptyEngine.Editor.ViewModels.Inspector;
using EmptyEngine.SceneSource.Editor;
using Xunit;

namespace EmptyEngine.SceneSource.Tests;

/// <summary>反映が一巡する前のポーリングで編集が消えないことの検証</summary>
public sealed class EditorFieldEditPersistenceTests : IDisposable
{
    private readonly string _assetsRoot = Path.Combine(Path.GetTempPath(), "ee-fieldedit-" + Guid.NewGuid().ToString("N"));
    private readonly AssetCatalog _catalog = new();

    public EditorFieldEditPersistenceTests() => Directory.CreateDirectory(_assetsRoot);

    public void Dispose()
    {
        try { Directory.Delete(_assetsRoot, recursive: true); } catch { }
    }

    private async Task<AssetImportService> ImportServiceWithHealthSceneAsync()
    {
        var importer = new SceneAssetImporter(CatalogStub.Schemas, _catalog);
        var child = new HierarchyNode("obj", "Object", Array.Empty<HierarchyNode>(),
            AuthoringTestHelpers.Components(new TestHealth { Max = 100, Current = 10 }));
        var sceneRoot = new HierarchyNode("ignored", "MyScene", new[] { child });
        await importer.SaveAsync(sceneRoot, Path.Combine(_assetsRoot, "MyScene.scene"));

        return new AssetImportService(_catalog, new ProjectAssetLayout(_assetsRoot).Sources, new IAssetImporter[] { importer }, Path.Combine(_assetsRoot, ".import-stamps"));
    }

    [Fact]
    public async Task Field_edit_survives_stale_poll_and_is_persisted_on_save()
    {
        AssetImportService service = await ImportServiceWithHealthSceneAsync();
        var vm = EditorFixture.NewEditor(assets: _catalog, imports: service, layout: new ProjectAssetLayout(_assetsRoot));

        HierarchyNode? saved = null;
        vm.SceneSaveRequested += (root, _) => saved = root;

        // 送信中の状態を再現するため、購読者が戻る前にガードを立てたままにする。
        vm.ScenePublished += _ => vm.BeginAuthoritativeSend();

        await vm.InitializeAsync();

        HierarchyNodeViewModel childVm = Assert.Single(vm.RootNodes[0].Children);
        vm.SelectedNode = childVm;
        AuthoringObjectViewModel component = Assert.Single(vm.SelectedComponents);
        FieldViewModel currentField = component.Fields.Single(f => f.Name == nameof(TestHealth.Current));
        currentField.NumericValue = 99;

        var stalePoll = new HierarchyNode(vm.RootNodes[0].ObjectId, "MyScene", new[]
        {
            new HierarchyNode("obj", "Object", Array.Empty<HierarchyNode>(),
                AuthoringTestHelpers.Components(new TestHealth { Max = 100, Current = 10 })),
        }, sceneId: vm.RootNodes[0].SceneId);
        vm.UpdateHierarchy(new[] { stalePoll });

        vm.SaveScene();

        Assert.NotNull(saved);
        Assert.Equal(99, AuthoringTestHelpers.GetInt(
            saved!.Children.Single().Components.Single(), nameof(TestHealth.Current)));
    }

    /// <summary>保存ハンドラのインポーター解決の回帰テスト</summary>
    [Fact]
    public async Task Save_resolves_importer_from_source_path_and_rewrites_scene_json()
    {
        AssetImportService service = await ImportServiceWithHealthSceneAsync();
        await service.ImportAllAsync();
        string key = _catalog.EnumerateAssets().Single().Key.Value;

        Assert.Equal(string.Empty, Path.GetExtension(key));
        Assert.False(service.TryGetImporter(Path.GetExtension(key), out _));

        Assert.True(_catalog.TryGetSourcePath(key, out string? sourcePath));
        Assert.NotNull(sourcePath);
        Assert.True(service.TryGetImporter(Path.GetExtension(sourcePath!), out IAssetImporter? importer));
        var sceneImporter = Assert.IsAssignableFrom<ISceneImporter>(importer!);

        var editedRoot = new HierarchyNode("ignored", "MyScene", new[]
        {
            new HierarchyNode("obj", "Object", Array.Empty<HierarchyNode>(),
                AuthoringTestHelpers.Components(new TestHealth { Max = 100, Current = 99 })),
        });
        await sceneImporter.SaveAsync(editedRoot, sourcePath!);
        await service.ImportSingleAsync(sourcePath!);

        Assert.True(_catalog.TryGetImportedAsset(key, out ImportedSource? reloaded));
        HierarchyNode reloadedRoot = AuthoringTestHelpers.RootOf(reloaded);
        Assert.Equal(99, AuthoringTestHelpers.GetInt(
            reloadedRoot.Children.Single().Components.Single(), nameof(TestHealth.Current)));
    }
}
