using EmptyEngine.Modules.Testing;
using System.Text.Json;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Assets;
using EmptyEngine.Editor.ViewModels;
using EmptyEngine.Editor.ViewModels.Hierarchy;
using EmptyEngine.ObjectModel;
using EmptyEngine.SceneSource.Editor;
using Xunit;

namespace EmptyEngine.SceneSource.Tests;

/// <summary>Extract Prefab の検証</summary>
public sealed class ExtractPrefabTests : IDisposable
{
    private readonly string _assetsRoot = Path.Combine(Path.GetTempPath(), "ee-extract-" + Guid.NewGuid().ToString("N"));
    private readonly AssetCatalog _catalog = new();

    public ExtractPrefabTests() => Directory.CreateDirectory(_assetsRoot);

    public void Dispose()
    {
        try { Directory.Delete(_assetsRoot, recursive: true); } catch { }
    }

    private string WriteHostScene()
    {
        var body = new HierarchyNode("obj-body", "Body", Array.Empty<HierarchyNode>(),
            AuthoringTestHelpers.Components(new TestHealth { Max = 50, Current = 50 }));
        var enemy = new HierarchyNode("obj-enemy", "Enemy", new[] { body },
            AuthoringTestHelpers.Components(new TestHealth { Max = 100, Current = 60 }));
        var turret = new HierarchyNode("obj-turret", "Turret", Array.Empty<HierarchyNode>(),
            AuthoringTestHelpers.Components(new ReferenceProbe { Target = ObjectReference.FromId("obj-body") }));
        var root = new HierarchyNode("host-root", "Main", new[] { enemy, turret });

        string path = Path.Combine(_assetsRoot, "Main.scene");
        File.WriteAllText(path, SceneJsonCodec.ToJson(root));
        return path;
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
        vm.SceneSaveRequested += (root, key) =>
        {
            if (!_catalog.TryGetSourcePath(key, out string? sourcePath) || sourcePath is null) return;
            if (!service.TryGetImporter(Path.GetExtension(sourcePath), out IAssetImporter? importer)
                || importer is not ISceneImporter sceneImporter) return;
            sceneImporter.SaveAsync(root, sourcePath).GetAwaiter().GetResult();
        };
        return vm;
    }

    private static string GuidOfMeta(string sourcePath)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(sourcePath + ".meta"));
        return doc.RootElement.GetProperty("guid").GetString()!;
    }

    private static object HealthOf(HierarchyNodeViewModel vm) =>
        Assert.Single(vm.Components, c => AuthoringTestHelpers.IsType<TestHealth>(c.Capture())).Capture()!;

    [Fact]
    public async Task Extract_writes_prefab_source_and_replaces_subtree_with_nested_instance()
    {
        WriteHostScene();
        AssetImportService service = CreateService();
        EditorViewModel vm = await SetupInitializedViewModelAsync(service);

        HierarchyNodeViewModel host = Assert.Single(vm.RootNodes);
        vm.SelectedNode = host.Children[0];
        Assert.True(vm.CanExtractPrefab);

        await vm.ExtractPrefabAsync("EnemyPrefab");

        string prefabPath = Path.Combine(_assetsRoot, "EnemyPrefab.scene");
        Assert.True(File.Exists(prefabPath));
        HierarchyNode prefabRoot = SceneJsonCodec.FromJson(File.ReadAllText(prefabPath), CatalogStub.Schemas);
        Assert.Equal("obj-enemy", prefabRoot.ObjectId);
        Assert.Equal("Enemy", prefabRoot.Name);
        Assert.Equal("obj-body", Assert.Single(prefabRoot.Children).ObjectId);

        HierarchyNodeViewModel instance = host.Children[0];
        Assert.True(instance.IsPrefabRoot);
        Assert.EndsWith(":obj-enemy", instance.ObjectId);
        Assert.Equal(60, AuthoringTestHelpers.GetInt(HealthOf(instance), nameof(TestHealth.Current)));
        HierarchyNodeViewModel bodyVm = Assert.Single(instance.Children);
        Assert.EndsWith(":obj-body", bodyVm.ObjectId);
        Assert.Equal(50, AuthoringTestHelpers.GetInt(HealthOf(bodyVm), nameof(TestHealth.Current)));

        object probe = Assert.Single(host.Children[1].Components).Capture()!;
        Assert.Equal(bodyVm.ObjectId, AuthoringTestHelpers.GetTargetId(probe, nameof(ReferenceProbe.Target)));

        Assert.True(host.IsDirty);
    }

    [Fact]
    public async Task Saved_host_keeps_only_a_reference_leaf_and_round_trips()
    {
        string hostPath = WriteHostScene();
        AssetImportService service = CreateService();
        EditorViewModel vm = await SetupInitializedViewModelAsync(service);

        HierarchyNodeViewModel host = Assert.Single(vm.RootNodes);
        vm.SelectedNode = host.Children[0];
        await vm.ExtractPrefabAsync("EnemyPrefab");
        string instanceRootId = host.Children[0].ObjectId;
        string instanceBodyId = Assert.Single(host.Children[0].Children).ObjectId;

        vm.SaveScene();

        string saved = await File.ReadAllTextAsync(hostPath);
        string prefabGuid = GuidOfMeta(Path.Combine(_assetsRoot, "EnemyPrefab.scene"));
        Assert.Contains($"\"Prefab\": \"{prefabGuid}\"", saved);
        Assert.DoesNotContain("\"Name\": \"Enemy\"", saved);
        Assert.DoesNotContain("\"Name\": \"Body\"", saved);
        Assert.Contains(instanceBodyId, saved);

        var reimported = AuthoringTestHelpers.SceneOf(await new SceneAssetImporter(CatalogStub.Schemas, _catalog).ImportAsync(
            new AssetImportRequest(hostPath, "Main.scene")));
        HierarchyNode instance = reimported.Children[0];
        Assert.Equal(instanceRootId, instance.ObjectId);
        Assert.Equal(60, AuthoringTestHelpers.GetInt(instance.Components.Single(), nameof(TestHealth.Current)));
        Assert.Equal(50, AuthoringTestHelpers.GetInt(instance.Children.Single().Components.Single(), nameof(TestHealth.Current)));
        AuthoringObject probe = reimported.Children[1].Components.Single();
        Assert.Equal(instanceBodyId, AuthoringTestHelpers.GetTargetId(probe, nameof(ReferenceProbe.Target)));
    }

    [Fact]
    public async Task Extract_refuses_scene_root_and_nested_instance_nodes()
    {
        WriteHostScene();
        AssetImportService service = CreateService();
        EditorViewModel vm = await SetupInitializedViewModelAsync(service);
        HierarchyNodeViewModel host = Assert.Single(vm.RootNodes);

        vm.SelectedNode = host;
        Assert.False(vm.CanExtractPrefab);

        vm.SelectedNode = host.Children[0];
        Assert.True(vm.CanExtractPrefab);
        await vm.ExtractPrefabAsync("EnemyPrefab");

        Assert.True(Assert.Single(vm.RootNodes).Children[0].IsPrefabRoot);
        Assert.False(vm.CanExtractPrefab);
        vm.SelectedNode = host.Children[0].Children[0];
        Assert.False(vm.CanExtractPrefab);

        vm.SelectedNode = host.Children[1];
        Assert.True(vm.CanExtractPrefab);
        vm.SetPlayMode(true);
        Assert.False(vm.CanExtractPrefab);
    }
}
