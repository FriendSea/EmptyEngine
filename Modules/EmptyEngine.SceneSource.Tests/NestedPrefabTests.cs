using EmptyEngine.Modules.Testing;
using EmptyEngine.Editor;
using EmptyEngine.ObjectModel;
using EmptyEngine.SceneSource.Editor;
using Xunit;

namespace EmptyEngine.SceneSource.Tests;

/// <summary>ネストプレハブの検証</summary>
public sealed class NestedPrefabTests : IDisposable
{
    private readonly string _assetsRoot = Path.Combine(Path.GetTempPath(), "ee-nested-" + Guid.NewGuid().ToString("N"));

    public NestedPrefabTests() => Directory.CreateDirectory(_assetsRoot);

    public void Dispose()
    {
        try { Directory.Delete(_assetsRoot, recursive: true); } catch { }
    }

    private (string guid, string path) WritePrefab()
    {
        var body = new HierarchyNode("obj-body", "Body", Array.Empty<HierarchyNode>(),
            AuthoringTestHelpers.Components(new TestHealth { Max = 100, Current = 100 }));
        var probe = new HierarchyNode("obj-probe", "Probe", Array.Empty<HierarchyNode>(),
            AuthoringTestHelpers.Components(new ReferenceProbe { Target = ObjectReference.FromId("obj-body") }));
        var root = new HierarchyNode("obj-root", "Enemy", new[] { body, probe });

        string path = Path.Combine(_assetsRoot, "Enemy.scene");
        File.WriteAllText(path, SceneJsonCodec.ToJson(root));

        string guid = Guid.NewGuid().ToString("N");
        File.WriteAllText(path + ".meta", $"{{\"guid\":\"{guid}\"}}");
        return (guid, path);
    }

    private string WriteHost(string prefabGuid)
    {
        string typeName = typeof(TestHealth).FullName!;
        string json = $$"""
        {
          "Id": "host-root",
          "Name": "Level",
          "Components": [],
          "Children": [
            {
              "Id": "leaf-a",
              "Prefab": "{{prefabGuid}}",
              "Overrides": [
                { "Object": "leaf-a:obj-body", "ComponentIndex": 0, "TypeName": "{{typeName}}", "Field": "Current", "Value": 25 }
              ]
            },
            { "Id": "leaf-b", "Prefab": "{{prefabGuid}}", "Overrides": [] }
          ]
        }
        """;
        string path = Path.Combine(_assetsRoot, "Level.scene");
        File.WriteAllText(path, json);
        File.WriteAllText(path + ".meta", $"{{\"guid\":\"{Guid.NewGuid():N}\"}}");
        return path;
    }

    private static AuthoringObject HealthOf(HierarchyNode instanceRoot) =>
        instanceRoot.Children.Single(c => c.Name == "Body").Components.Single();

    private static AuthoringObject ProbeOf(HierarchyNode instanceRoot) =>
        instanceRoot.Children.Single(c => c.Name == "Probe").Components.Single();

    [Fact]
    public void Remap_rewrites_references_inside_arrays()
    {
        var body = new HierarchyNode("obj-body", "Body", Array.Empty<HierarchyNode>(),
            AuthoringTestHelpers.Components(new TestHealth { Max = 100, Current = 100 }));
        var probe = new HierarchyNode("obj-probe", "Probe", Array.Empty<HierarchyNode>(),
            AuthoringTestHelpers.Components(new MultiReferenceProbe
            {
                Targets =
                [
                    ObjectReference.FromId("obj-body"),
                    ObjectReference.FromId("outside"),
                    ObjectReference.FromId("obj-probe"),
                ],
            }));
        var root = new HierarchyNode("obj-root", "Enemy", new[] { body, probe });

        HierarchyNode remapped = NestedPrefabCodec.Remap(root, "leaf-a");

        AuthoringObject targets = remapped.Children.Single(c => c.Name == "Probe").Components.Single();
        IReadOnlyList<string> ids = AuthoringTestHelpers.GetTargetIds(targets, nameof(MultiReferenceProbe.Targets));

        Assert.Equal("leaf-a:obj-body", ids[0]);
        Assert.Equal("leaf-a:obj-probe", ids[2]);
        Assert.Equal("outside", ids[1]);
    }

    [Fact]
    public async Task Import_bakes_two_instances_with_independent_prefixed_ids()
    {
        (string guid, _) = WritePrefab();
        string hostPath = WriteHost(guid);

        var importer = new SceneAssetImporter(CatalogStub.Schemas);
        AssetImportResult result = await importer.ImportAsync(new AssetImportRequest(hostPath, "Level.scene", _assetsRoot));

        Assert.True(result.Success, result.Message);
        var root = AuthoringTestHelpers.SceneOf(result);

        HierarchyNode a = root.Children[0];
        HierarchyNode b = root.Children[1];

        Assert.Equal("leaf-a:obj-root", a.ObjectId);
        Assert.Equal("leaf-b:obj-root", b.ObjectId);
        Assert.Equal("leaf-a:obj-body", a.Children[0].ObjectId);
        Assert.Equal("leaf-b:obj-body", b.Children[0].ObjectId);

        Assert.Equal(25, AuthoringTestHelpers.GetInt(HealthOf(a), nameof(TestHealth.Current)));
        Assert.Equal(100, AuthoringTestHelpers.GetInt(HealthOf(a), nameof(TestHealth.Max)));
        Assert.Equal(100, AuthoringTestHelpers.GetInt(HealthOf(b), nameof(TestHealth.Current)));

        Assert.Equal("leaf-a:obj-body", AuthoringTestHelpers.GetTargetId(ProbeOf(a), nameof(ReferenceProbe.Target)));
        Assert.Equal("leaf-b:obj-body", AuthoringTestHelpers.GetTargetId(ProbeOf(b), nameof(ReferenceProbe.Target)));
    }

    [Fact]
    public async Task Import_bakes_variant_prefab_before_applying_instance_overrides()
    {
        (string originalGuid, _) = WritePrefab();
        string variantGuid = Guid.NewGuid().ToString("N");
        string typeName = typeof(TestHealth).FullName!;
        string variantPath = Path.Combine(_assetsRoot, "ArmoredEnemy.variant");
        await File.WriteAllTextAsync(variantPath, $$"""
        {
          "Original": "{{originalGuid}}",
          "Overrides": [
            { "Object": "obj-body", "ComponentIndex": 0, "TypeName": "{{typeName}}", "Field": "Max", "Value": 200 }
          ]
        }
        """);
        await File.WriteAllTextAsync(variantPath + ".meta", $"{{\"guid\":\"{variantGuid}\"}}");

        string hostPath = WriteHost(variantGuid);
        AssetImportResult result = await new SceneAssetImporter(CatalogStub.Schemas).ImportAsync(
            new AssetImportRequest(hostPath, "Level.scene", _assetsRoot));

        Assert.True(result.Success, result.Message);
        var root = AuthoringTestHelpers.SceneOf(result);
        Assert.Equal(200, AuthoringTestHelpers.GetInt(HealthOf(root.Children[0]), nameof(TestHealth.Max)));
        Assert.Equal(25, AuthoringTestHelpers.GetInt(HealthOf(root.Children[0]), nameof(TestHealth.Current)));
        Assert.Equal(200, AuthoringTestHelpers.GetInt(HealthOf(root.Children[1]), nameof(TestHealth.Max)));
        Assert.Equal(100, AuthoringTestHelpers.GetInt(HealthOf(root.Children[1]), nameof(TestHealth.Current)));
        Assert.Contains(originalGuid, result.Dependencies);
        Assert.Contains(variantGuid, result.Dependencies);
    }

    [Fact]
    public async Task Save_folds_instances_back_to_leaves_with_only_diffs()
    {
        (string guid, _) = WritePrefab();
        string hostPath = WriteHost(guid);

        var importer = new SceneAssetImporter(CatalogStub.Schemas);
        var root = AuthoringTestHelpers.SceneOf(await importer.ImportAsync(
            new AssetImportRequest(hostPath, "Level.scene", _assetsRoot)));

        HierarchyNode bodyB = root.Children[1].Children.Single(c => c.Name == "Body");
        AuthoringTestHelpers.SetNumber(bodyB.Components[0], nameof(TestHealth.Current), 42);

        await importer.SaveAsync(root, hostPath);
        string saved = await File.ReadAllTextAsync(hostPath);

        Assert.Equal(2, saved.Split("\"Prefab\"").Length - 1);
        Assert.Contains(guid, saved);
        Assert.DoesNotContain("\"Name\": \"Enemy\"", saved);
        Assert.DoesNotContain("\"Field\": \"Max\"", saved);

        Assert.Contains("\"Object\": \"leaf-a:obj-body\"", saved);
        Assert.Contains("25", saved);
        Assert.Contains("\"Object\": \"leaf-b:obj-body\"", saved);
        Assert.Contains("42", saved);

        var reimported = AuthoringTestHelpers.SceneOf(await importer.ImportAsync(
            new AssetImportRequest(hostPath, "Level.scene", _assetsRoot)));
        Assert.Equal(25, AuthoringTestHelpers.GetInt(HealthOf(reimported.Children[0]), nameof(TestHealth.Current)));
        Assert.Equal(42, AuthoringTestHelpers.GetInt(HealthOf(reimported.Children[1]), nameof(TestHealth.Current)));
        Assert.Equal(100, AuthoringTestHelpers.GetInt(HealthOf(reimported.Children[1]), nameof(TestHealth.Max)));
    }

    [Fact]
    public async Task InstantiateNested_grafts_a_fresh_instance_that_folds_on_save()
    {
        (string guid, string prefabPath) = WritePrefab();

        var importer = new SceneAssetImporter(CatalogStub.Schemas);

        HierarchyNode instance = await importer.InstantiateNestedAsync(guid, prefabPath);
        Assert.EndsWith(":obj-root", instance.ObjectId);
        Assert.Equal(100, AuthoringTestHelpers.GetInt(HealthOf(instance), nameof(TestHealth.Current)));

        var host = new HierarchyNode("host-root", "Level", new[] { instance });
        string hostPath = Path.Combine(_assetsRoot, "Built.scene");
        await importer.SaveAsync(host, hostPath);

        string saved = await File.ReadAllTextAsync(hostPath);
        Assert.Contains("\"Prefab\": \"" + guid + "\"", saved);
        Assert.DoesNotContain("\"Name\": \"Enemy\"", saved);

        File.WriteAllText(hostPath + ".meta", $"{{\"guid\":\"{Guid.NewGuid():N}\"}}");
        var reimported = AuthoringTestHelpers.SceneOf(await importer.ImportAsync(
            new AssetImportRequest(hostPath, "Built.scene", _assetsRoot)));
        Assert.Equal(instance.ObjectId, reimported.Children.Single().ObjectId);
        Assert.Equal(100, AuthoringTestHelpers.GetInt(HealthOf(reimported.Children.Single()), nameof(TestHealth.Current)));
    }

    [Fact]
    public async Task Two_instances_of_same_prefab_have_disjoint_id_spaces()
    {
        (string guid, string prefabPath) = WritePrefab();

        var importer = new SceneAssetImporter(CatalogStub.Schemas);
        HierarchyNode first = await importer.InstantiateNestedAsync(guid, prefabPath);
        HierarchyNode second = await importer.InstantiateNestedAsync(guid, prefabPath);

        var firstIds = CollectIds(first);
        var secondIds = CollectIds(second);
        Assert.Empty(firstIds.Intersect(secondIds));
    }

    [Fact]
    public async Task Nested_cycle_fails_import()
    {
        string guidA = Guid.NewGuid().ToString("N");
        string guidB = Guid.NewGuid().ToString("N");
        string pathA = Path.Combine(_assetsRoot, "A.scene");
        string pathB = Path.Combine(_assetsRoot, "B.scene");

        File.WriteAllText(pathA, $$"""{ "Id": "a", "Name": "A", "Components": [], "Children": [ { "Id": "la", "Prefab": "{{guidB}}", "Overrides": [] } ] }""");
        File.WriteAllText(pathA + ".meta", $"{{\"guid\":\"{guidA}\"}}");
        File.WriteAllText(pathB, $$"""{ "Id": "b", "Name": "B", "Components": [], "Children": [ { "Id": "lb", "Prefab": "{{guidA}}", "Overrides": [] } ] }""");
        File.WriteAllText(pathB + ".meta", $"{{\"guid\":\"{guidB}\"}}");

        AssetImportResult result = await new SceneAssetImporter(CatalogStub.Schemas).ImportAsync(
            new AssetImportRequest(pathA, "A.scene", _assetsRoot));

        Assert.False(result.Success);
        Assert.Contains("cycle", result.Message);
    }

    [Fact]
    public async Task Missing_nested_prefab_fails_import()
    {
        string hostPath = Path.Combine(_assetsRoot, "Orphan.scene");
        File.WriteAllText(hostPath, """{ "Id": "h", "Name": "H", "Components": [], "Children": [ { "Id": "lx", "Prefab": "deadbeef", "Overrides": [] } ] }""");
        File.WriteAllText(hostPath + ".meta", $"{{\"guid\":\"{Guid.NewGuid():N}\"}}");

        AssetImportResult result = await new SceneAssetImporter(CatalogStub.Schemas).ImportAsync(
            new AssetImportRequest(hostPath, "Orphan.scene", _assetsRoot));

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Save_after_rehydrate_restart_still_folds_leaves()
    {
        (string guid, _) = WritePrefab();
        string hostPath = WriteHost(guid);

        var importSession = new SceneAssetImporter(CatalogStub.Schemas);
        var root = AuthoringTestHelpers.SceneOf(await importSession.ImportAsync(
            new AssetImportRequest(hostPath, "Level.scene", _assetsRoot)));

        HierarchyNode bodyB = root.Children[1].Children.Single(c => c.Name == "Body");
        AuthoringTestHelpers.SetNumber(bodyB.Components[0], nameof(TestHealth.Current), 42);

        var restartedSession = new SceneAssetImporter(CatalogStub.Schemas);
        await restartedSession.SaveAsync(root, hostPath);
        string saved = await File.ReadAllTextAsync(hostPath);

        Assert.Equal(2, saved.Split("\"Prefab\"").Length - 1);
        Assert.DoesNotContain("\"Name\": \"Enemy\"", saved);
        Assert.Contains("\"Object\": \"leaf-a:obj-body\"", saved);
        Assert.Contains("42", saved);

        var reimported = AuthoringTestHelpers.SceneOf(await new SceneAssetImporter(CatalogStub.Schemas).ImportAsync(
            new AssetImportRequest(hostPath, "Level.scene", _assetsRoot)));
        Assert.Equal(25, AuthoringTestHelpers.GetInt(HealthOf(reimported.Children[0]), nameof(TestHealth.Current)));
        Assert.Equal(42, AuthoringTestHelpers.GetInt(HealthOf(reimported.Children[1]), nameof(TestHealth.Current)));
    }

    [Fact]
    public void IsNestedInstanceRoot_marks_only_the_boundary()
    {
        var importer = new SceneAssetImporter(CatalogStub.Schemas);

        Assert.True(importer.IsNestedInstanceRoot("leaf-a:obj-root", "host-root"));
        Assert.False(importer.IsNestedInstanceRoot("leaf-a:obj-body", "leaf-a:obj-root"));
        Assert.False(importer.IsNestedInstanceRoot("host-child", "host-root"));
        Assert.False(importer.IsNestedInstanceRoot("host-root", null));

        Assert.True(importer.IsNestedInstanceRoot("outer:inner:obj-root", "outer:obj-root"));
        Assert.False(importer.IsNestedInstanceRoot("outer:inner:obj-body", "outer:inner:obj-root"));
    }

    private static HashSet<string> CollectIds(HierarchyNode node)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal) { node.ObjectId };
        foreach (HierarchyNode child in node.Children)
            ids.UnionWith(CollectIds(child));
        return ids;
    }
}
