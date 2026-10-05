using EmptyEngine.Modules.Testing;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Assets;
using EmptyEngine.Editor.State;
using EmptyEngine.Editor.ViewModels;
using EmptyEngine.Editor.ViewModels.Hierarchy;
using EmptyEngine.SceneSource.Editor;
using EmptyEngine.World.Editor;
using Xunit;

namespace EmptyEngine.World.Tests;

/// <summary>起動時の権威ロードの検証</summary>
public sealed class EditorStartupLoadTests : IDisposable
{
    private readonly string _assetsRoot = Path.Combine(Path.GetTempPath(), "ee-startup-" + Guid.NewGuid().ToString("N"));
    private readonly AssetCatalog _catalog = new();

    public EditorStartupLoadTests()
    {
        Directory.CreateDirectory(_assetsRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_assetsRoot, recursive: true); } catch { }
    }

    private async Task<AssetImportService> ImportServiceWithSceneAsync(string sceneName)
    {
        var importer = new SceneAssetImporter(CatalogStub.Schemas, _catalog);
        var sceneRoot = new HierarchyNode("ignored", sceneName, new[] { new HierarchyNode("obj", "Object") });
        await importer.SaveAsync(sceneRoot, Path.Combine(_assetsRoot, sceneName + ".scene"));

        var service = new AssetImportService(_catalog, new ProjectAssetLayout(_assetsRoot).Sources, new IAssetImporter[] { importer }, Path.Combine(_assetsRoot, ".import-stamps"));
        return service;
    }

    [Fact]
    public async Task Initialize_loads_startup_scene_and_makes_it_saveable()
    {
        AssetImportService service = await ImportServiceWithSceneAsync("MyScene");
        var vm = EditorFixture.NewEditor(assets: _catalog, imports: service, layout: new ProjectAssetLayout(_assetsRoot));

        IReadOnlyList<HierarchyNode>? sent = null;
        vm.ScenePublished += p => sent = p.Roots;

        await vm.InitializeAsync();

        HierarchyNodeViewModel root = Assert.Single(vm.RootNodes);
        Assert.Equal("MyScene", root.Name);

        vm.SelectedNode = root;
        Assert.False(vm.IsPlaying);
        Assert.True(vm.CanSaveScene);

        Assert.NotNull(sent);
        Assert.Equal("MyScene", Assert.Single(sent!).Name);
    }

    [Fact]
    public async Task Authoring_without_runtime_replies_supports_undo_play_stop_and_save()
    {
        AssetImportService service = await ImportServiceWithSceneAsync("MyScene");
        var serializer = new HierarchyBlobSerializer(CatalogStub.Schemas);
        var history = new EditHistoryViewModel();
        var vm = EditorFixture.NewEditor(assets: _catalog, imports: service, serializer: serializer, history: history, layout: new ProjectAssetLayout(_assetsRoot));

        // UpdateHierarchy（ランタイムからの返信）は一度も呼ばない。
        await vm.InitializeAsync();
        vm.SelectedNode = Assert.Single(Assert.Single(vm.RootNodes).Children);
        vm.SelectedNodeName = "Authored";
        Assert.True(history.CanUndo);

        history.Undo();
        Assert.Equal("Object", vm.RootNodes[0].Children[0].Name);
        history.Redo();
        Assert.Equal("Authored", vm.RootNodes[0].Children[0].Name);

        vm.SetPlayMode(true);
        vm.SelectedNode = vm.RootNodes[0].Children[0];
        vm.SelectedNodeName = "TemporaryPlayEdit";
        vm.SetPlayMode(false);
        Assert.Equal("Authored", vm.RootNodes[0].Children[0].Name);

        Task? saving = null;
        vm.SceneSaveRequested += (root, key) =>
        {
            Assert.True(_catalog.TryGetSourcePath(key, out string? sourcePath));
            HierarchyNode snapshot = Assert.Single(serializer.Deserialize(serializer.Serialize([root])));
            saving = new SceneAssetImporter(CatalogStub.Schemas, _catalog).SaveAsync(snapshot, sourcePath!);
        };
        vm.SelectedNode = vm.RootNodes[0];
        Assert.True(vm.CanSaveScene);
        vm.SaveScene();
        Assert.NotNull(saving);
        await saving;

        var reopenedCatalog = new AssetCatalog();
        var reopened = EditorFixture.NewEditor(assets: reopenedCatalog, imports: new AssetImportService(reopenedCatalog, new ProjectAssetLayout(_assetsRoot).Sources, [new SceneAssetImporter(CatalogStub.Schemas, _catalog)], Path.Combine(_assetsRoot, ".import-stamps")), serializer: serializer, layout: new ProjectAssetLayout(_assetsRoot));
        await reopened.InitializeAsync();
        Assert.Equal("Authored", Assert.Single(Assert.Single(reopened.RootNodes).Children).Name);
    }

    [Fact]
    public async Task Hierarchy_is_collapsed_by_default_and_restores_expanded_objects()
    {
        AssetImportService service = await ImportServiceWithSceneAsync("MyScene");
        string statePath = Path.Combine(_assetsRoot, "state", "editor-state.json");
        var store = new EditorStateStore(statePath);

        var first = EditorFixture.NewEditor(assets: _catalog, imports: service, state: store, layout: new ProjectAssetLayout(_assetsRoot));
        await first.InitializeAsync();

        HierarchyNodeViewModel firstRoot = Assert.Single(first.RootNodes);
        Assert.False(firstRoot.IsExpanded);

        first.SetHierarchyExpanded(firstRoot, true);
        Assert.True(firstRoot.IsExpanded);

        string sceneKey = _catalog.EnumerateAssets().Single().Key.Value;
        Assert.Equal(new[] { firstRoot.ObjectId }, store.State.ExpandedHierarchyObjects[sceneKey]);

        var restoredStore = new EditorStateStore(statePath);
        var restored = EditorFixture.NewEditor(assets: _catalog, imports: service, state: restoredStore, layout: new ProjectAssetLayout(_assetsRoot));
        await restored.InitializeAsync();

        HierarchyNodeViewModel restoredRoot = Assert.Single(restored.RootNodes);
        Assert.True(restoredRoot.IsExpanded);

        restored.SetHierarchyExpanded(restoredRoot, false);
        Assert.False(restoredRoot.IsExpanded);
        Assert.DoesNotContain(sceneKey, restoredStore.State.ExpandedHierarchyObjects.Keys);
    }

    /// <summary>ランタイムが別のツリーを返しても、どのシーンを開いているかの記録は動かないこと</summary>
    [Fact]
    public async Task Polling_divergence_does_not_corrupt_the_loaded_scene_record()
    {
        AssetImportService service = await ImportServiceWithSceneAsync("MyScene");

        var vm = EditorFixture.NewEditor(assets: _catalog, imports: service, layout: new ProjectAssetLayout(_assetsRoot));

        await vm.InitializeAsync();
        string sceneKey = _catalog.EnumerateAssets().Single().Key.Value;
        Assert.Equal(sceneKey, Assert.Single(vm.GetLoadedScenes()).AssetKey);

        vm.UpdateHierarchy(new[] { new HierarchyNode("runtime-autoload", "MyScene") });

        Assert.Equal(sceneKey, Assert.Single(vm.GetLoadedScenes()).AssetKey);
    }

    [Fact]
    public async Task Scene_artifact_key_is_guid_based_not_path()
    {
        AssetImportService service = await ImportServiceWithSceneAsync("MyScene");
        await service.ImportAllAsync();

        string key = _catalog.EnumerateAssets().Single().Key.Value;

        Assert.DoesNotContain("MyScene", key);
        Assert.DoesNotContain(".scene", key);
        Assert.True(Guid.TryParseExact(key, "N", out _));
    }

    [Fact]
    public async Task Existing_non_hex_artifact_id_is_preserved()
    {
        AssetImportService service = await ImportServiceWithSceneAsync("MyScene");
        string scenePath = Path.Combine(_assetsRoot, "MyScene.scene");
        await File.WriteAllTextAsync(scenePath + ".meta", "{\"guid\":\"scene-id_v2\"}");

        await service.ImportAllAsync();

        Assert.Equal("scene-id_v2", _catalog.EnumerateAssets().Single().Key.Value);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("folder/asset")]
    [InlineData("folder\\asset")]
    [InlineData(".")]
    [InlineData("..")]
    public async Task Unsafe_artifact_id_is_replaced(string unsafeId)
    {
        AssetImportService service = await ImportServiceWithSceneAsync("MyScene");
        string scenePath = Path.Combine(_assetsRoot, "MyScene.scene");
        await File.WriteAllTextAsync(
            scenePath + ".meta",
            System.Text.Json.JsonSerializer.Serialize(new { guid = unsafeId }));

        await service.ImportAllAsync();

        string key = _catalog.EnumerateAssets().Single().Key.Value;
        Assert.NotEqual(unsafeId, key);
        Assert.True(Guid.TryParseExact(key, "N", out _));
    }

    [Fact]
    public async Task Reimport_after_rename_keeps_same_guid_via_meta()
    {
        AssetImportService service = await ImportServiceWithSceneAsync("First");
        await service.ImportAllAsync();
        string keyBefore = _catalog.EnumerateAssets().Single().Key.Value;

        string oldScene = Path.Combine(_assetsRoot, "First.scene");
        string newScene = Path.Combine(_assetsRoot, "Renamed.scene");
        File.Move(oldScene, newScene);
        File.Move(oldScene + ".meta", newScene + ".meta");

        await service.ImportAllAsync();
        string keyAfter = _catalog.EnumerateAssets().Single().Key.Value;

        Assert.Equal(keyBefore, keyAfter);
    }

    [Fact]
    public async Task TryGetSourcePath_resolves_guid_key_back_to_source()
    {
        AssetImportService service = await ImportServiceWithSceneAsync("MyScene");
        await service.ImportAllAsync();
        string key = _catalog.EnumerateAssets().Single().Key.Value;

        Assert.True(_catalog.TryGetSourcePath(key, out string? sourcePath));
        Assert.Equal(Path.Combine(_assetsRoot, "MyScene.scene"), sourcePath);
        Assert.False(_catalog.TryGetSourcePath("nonexistent-guid", out _));
    }

    [Fact]
    public async Task Loaded_scene_controls_identify_add_and_unload_scene_instances()
    {
        AssetImportService service = await ImportServiceWithSceneAsync("MyScene");
        var vm = EditorFixture.NewEditor(assets: _catalog, imports: service, layout: new ProjectAssetLayout(_assetsRoot));
        await vm.InitializeAsync();

        string key = _catalog.EnumerateAssets().Single().Key.Value;
        LoadedSceneInfo startup = Assert.Single(vm.GetLoadedScenes());
        Assert.Equal(key, startup.AssetKey);
        Assert.Equal("MyScene.scene", startup.DisplayPath);

        LoadedSceneInfo loaded = Assert.IsType<LoadedSceneInfo>(vm.TryLoadScene(key));
        Assert.NotEqual(startup.SceneId, loaded.SceneId);
        Assert.Equal(2, vm.GetLoadedScenes().Count);

        Assert.True(vm.TryUnloadScene(loaded.SceneId));
        Assert.Equal(startup.SceneId, Assert.Single(vm.GetLoadedScenes()).SceneId);
        Assert.False(vm.TryUnloadScene(loaded.SceneId));
        Assert.Null(vm.TryLoadScene("not-a-scene"));
    }

    [Fact]
    public async Task Polling_is_ignored_until_initial_load_then_accepted()
    {
        AssetImportService service = await ImportServiceWithSceneAsync("MyScene");
        var vm = EditorFixture.NewEditor(assets: _catalog, imports: service, layout: new ProjectAssetLayout(_assetsRoot));

        await vm.InitializeAsync();
        HierarchyNodeViewModel startupRoot = Assert.Single(vm.RootNodes);

        // 初回ロード後の poll は取り込まれる。オブジェクトの顔ぶれを変えると増減として捨てられるので、
        // 届いたことは同じ顔ぶれのまま名前が変わることで見る。
        vm.UpdateHierarchy(new[]
        {
            new HierarchyNode(
                startupRoot.ObjectId,
                startupRoot.Name,
                new[] { new HierarchyNode(startupRoot.Children[0].ObjectId, "RenamedByRuntime") },
                sceneId: startupRoot.SceneId),
        });

        Assert.Equal("RenamedByRuntime", Assert.Single(vm.RootNodes).Children[0].Name);
    }
}
