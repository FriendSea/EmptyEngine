using EmptyEngine.Modules.Testing;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Assets;
using EmptyEngine.Editor.ViewModels;
using EmptyEngine.Editor.ViewModels.Hierarchy;
using EmptyEngine.Editor.ViewModels.Inspector;
using EmptyEngine.SceneSource.Editor;
using Xunit;

namespace EmptyEngine.SceneSource.Tests;

/// <summary>再インポートに続くロード済みシーンの差し替えの検証</summary>
public sealed class EditorSceneReloadTests : IDisposable
{
    private readonly string _assetsRoot = Path.Combine(Path.GetTempPath(), "ee-reload-" + Guid.NewGuid().ToString("N"));
    private readonly AssetCatalog _catalog = new();

    public EditorSceneReloadTests() => Directory.CreateDirectory(_assetsRoot);

    public void Dispose()
    {
        try { Directory.Delete(_assetsRoot, recursive: true); } catch { }
    }

    [Fact]
    public async Task Loaded_scene_is_reloaded_when_its_source_changes()
    {
        AssetImportService service = CreateService();
        (_, string scenePath) = WriteHealthScene("Main.scene", current: 100);

        EditorViewModel vm = await SetupInitializedViewModelAsync(service);
        string sceneIdBefore = Assert.Single(vm.RootNodes).SceneId;
        Assert.Equal(100, CurrentOfHierarchy(vm));

        TouchHealthScene(scenePath, current: 25);
        await vm.ImportAssetsIfChangedAsync();

        Assert.Equal(25, CurrentOfHierarchy(vm));
        Assert.Equal(sceneIdBefore, Assert.Single(vm.RootNodes).SceneId);
    }

    [Fact]
    public async Task Loaded_host_scene_is_reloaded_when_nested_prefab_changes()
    {
        AssetImportService service = CreateService();
        (string childGuid, string childPath) = WriteHealthScene("Child.scene", current: 100);
        await File.WriteAllTextAsync(Path.Combine(_assetsRoot, "AHost.scene"), HostSceneJson("host-root", childGuid));

        EditorViewModel vm = await SetupInitializedViewModelAsync(service);
        Assert.Equal("AHost", Assert.Single(vm.RootNodes).Name);
        Assert.Equal(100, CurrentOfHierarchy(vm));

        TouchHealthScene(childPath, current: 42);
        await vm.ImportAssetsIfChangedAsync();

        Assert.Equal(42, CurrentOfHierarchy(vm));
    }

    [Fact]
    public async Task ImportSingle_reloads_dependent_loaded_scene()
    {
        AssetImportService service = CreateService();
        (string childGuid, string childPath) = WriteHealthScene("Child.scene", current: 100);
        await File.WriteAllTextAsync(Path.Combine(_assetsRoot, "AHost.scene"), HostSceneJson("host-root", childGuid));

        EditorViewModel vm = await SetupInitializedViewModelAsync(service);
        Assert.Equal(100, CurrentOfHierarchy(vm));

        TouchHealthScene(childPath, current: 7);
        AssetImportResult result = await service.ImportSingleAsync(childPath);

        Assert.True(result.Success, result.Message);
        Assert.Equal(7, CurrentOfHierarchy(vm));
    }

    [Fact]
    public async Task Dirty_scene_is_not_reloaded()
    {
        AssetImportService service = CreateService();
        (_, string scenePath) = WriteHealthScene("Main.scene", current: 100);

        EditorViewModel vm = await SetupInitializedViewModelAsync(service);

        vm.SelectedNode = vm.RootNodes[0];
        vm.SelectedNodeName = "Renamed";
        Assert.True(vm.RootNodes[0].IsDirty);

        TouchHealthScene(scenePath, current: 25);
        await vm.ImportAssetsIfChangedAsync();

        Assert.Equal(100, CurrentOfHierarchy(vm));
        Assert.Equal("Renamed", vm.RootNodes[0].Name);
        Assert.True(vm.RootNodes[0].IsDirty);
    }

    [Fact]
    public async Task Imports_are_deferred_while_playing_and_resume_after_stop()
    {
        AssetImportService service = CreateService();
        (_, string scenePath) = WriteHealthScene("Main.scene", current: 100);

        EditorViewModel vm = await SetupInitializedViewModelAsync(service);
        int artifactsChangedCount = 0;
        service.ArtifactsChanged += _ => artifactsChangedCount++;

        vm.SetPlayMode(true);
        TouchHealthScene(scenePath, current: 25);
        await vm.ImportAssetsIfChangedAsync();

        Assert.Equal(0, artifactsChangedCount);
        Assert.Equal(100, CurrentOfHierarchy(vm));

        vm.SetPlayMode(false);
        await vm.ImportAssetsIfChangedAsync();

        Assert.Equal(1, artifactsChangedCount);
        Assert.Equal(25, CurrentOfHierarchy(vm));
    }

    private AssetImportService CreateService()
    {
        string artifacts = Path.Combine(_assetsRoot, ".artifacts");
        var service = new AssetImportService(_catalog, new ProjectAssetLayout(_assetsRoot).Sources, new IAssetImporter[] { new SceneAssetImporter(CatalogStub.Schemas, _catalog), new PrefabVariantImporter(CatalogStub.Schemas, _catalog) }, Path.Combine(artifacts, "import-stamps"));
        service.SetArtifacts(TestArtifacts.At(Path.Combine(artifacts, "store")));
        return service;
    }

    private async Task<EditorViewModel> SetupInitializedViewModelAsync(AssetImportService service)
    {
        var vm = EditorFixture.NewEditor(assets: _catalog, imports: service, layout: new ProjectAssetLayout(_assetsRoot));
        await vm.InitializeAsync();
        service.ArtifactsChanged += keys => vm.ReloadImportedScenes(keys);
        return vm;
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
          "Name": "AHost",
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

    private static int CurrentOfHierarchy(EditorViewModel vm)
    {
        object? health = FindHealth(Assert.Single(vm.RootNodes));
        Assert.NotNull(health);
        return AuthoringTestHelpers.GetInt(health, nameof(TestHealth.Current));
    }

    private static object? FindHealth(HierarchyNodeViewModel node)
    {
        foreach (AuthoringObjectViewModel component in node.Components)
        {
            if (AuthoringTestHelpers.IsType<TestHealth>(component.Capture())) return component.Capture();
        }
        foreach (HierarchyNodeViewModel child in node.Children)
        {
            if (FindHealth(child) is { } health) return health;
        }
        return null;
    }
}
