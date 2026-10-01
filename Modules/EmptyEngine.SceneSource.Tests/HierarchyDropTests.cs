using EmptyEngine.Modules.Testing;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Assets;
using EmptyEngine.Editor.ViewModels;
using EmptyEngine.Editor.ViewModels.Hierarchy;
using EmptyEngine.SceneSource.Editor;
using EmptyEngine.Tests;
using EmptyEngine.World.Editor;
using Xunit;

namespace EmptyEngine.SceneSource.Tests;

/// <summary>外側のファイルツリーからヒエラルキーへ落としたファイルの行き先の検証</summary>
public sealed class HierarchyDropTests : IDisposable
{
    private readonly string _assetsRoot = Path.Combine(Path.GetTempPath(), "ee-drop-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingLogger<EditorViewModel> _logs = new();

    public HierarchyDropTests() => Directory.CreateDirectory(_assetsRoot);

    public void Dispose()
    {
        try { Directory.Delete(_assetsRoot, recursive: true); } catch { }
    }

    private string WriteScene(string name, params HierarchyNode[] children)
    {
        var root = new HierarchyNode(name.ToLowerInvariant() + "-root", name, children);
        string path = Path.Combine(_assetsRoot, name + ".scene");
        File.WriteAllText(path, SceneJsonCodec.ToJson(root));
        return path;
    }

    private async Task<EditorViewModel> SetupAsync(params IAssetImporter[] extraImporters)
    {
        string artifacts = Path.Combine(_assetsRoot, ".artifacts");
        IAssetImporter[] importers = [new SceneAssetImporter(CatalogStub.Schemas), .. extraImporters];
        var catalog = new AssetCatalog();
        var service = new AssetImportService(
            catalog, _assetsRoot, importers, stampRootPath: Path.Combine(artifacts, "import-stamps"));
        service.SetArtifacts(TestArtifacts.At(Path.Combine(artifacts, "store")));

        var vm = EditorFixture.NewEditor(
            assets: catalog,
            imports: service,
            logger: _logs,
            serializer: new HierarchyBlobSerializer(CatalogStub.Schemas));
        await vm.InitializeAsync();
        return vm;
    }

    private static string VsCodeUri(string path) =>
        "file:///" + path.Replace('\\', '/').Replace(":", "%3A").Replace(" ", "%20");

    [Fact]
    public async Task Dropping_a_scene_on_empty_space_loads_it_as_a_root()
    {
        WriteScene("Main", new HierarchyNode("obj-enemy", "Enemy"));
        string piece = WriteScene("Piece");
        EditorViewModel vm = await SetupAsync();

        HierarchyNodeViewModel host = Assert.Single(vm.RootNodes);
        Assert.Equal("Main", host.Name);

        await vm.DropFilesAsync([piece]);

        Assert.Equal(2, vm.RootNodes.Count);
        HierarchyNodeViewModel added = vm.RootNodes[1];
        Assert.Equal("Piece", added.Name);
        Assert.True(added.IsSceneRoot);
    }

    [Fact]
    public async Task Dropping_a_scene_on_an_object_nests_it_as_a_prefab_child()
    {
        WriteScene("Main", new HierarchyNode("obj-enemy", "Enemy"));
        string piece = WriteScene("Piece");
        EditorViewModel vm = await SetupAsync();

        HierarchyNodeViewModel enemy = Assert.Single(Assert.Single(vm.RootNodes).Children);
        Assert.Equal("Enemy", enemy.Name);

        await vm.DropFilesAsync([VsCodeUri(piece)], enemy);

        Assert.Single(vm.RootNodes);
        HierarchyNodeViewModel nested = Assert.Single(enemy.Children);
        Assert.Equal("Piece", nested.Name);
        Assert.True(nested.IsPrefabRoot);
        Assert.EndsWith(":piece-root", nested.ObjectId);
    }

    [Fact]
    public async Task Dropping_by_file_name_alone_still_resolves()
    {
        WriteScene("Main");
        WriteScene("Piece");
        EditorViewModel vm = await SetupAsync();

        await vm.DropFilesAsync(["Piece.scene"]);

        Assert.Equal(2, vm.RootNodes.Count);
        Assert.Equal("Piece", vm.RootNodes[1].Name);
    }

    [Fact]
    public async Task Dropping_a_non_scene_asset_changes_nothing()
    {
        CatalogStub.Register<TestSpriteHolder>();
        WriteScene("Main");
        string assetPath = Path.Combine(_assetsRoot, "Holder.asset");
        await File.WriteAllTextAsync(assetPath, """
            { "TypeName": "EmptyEngine.Modules.Testing.TestSpriteHolder" }
            """);
        EditorViewModel vm = await SetupAsync(new TypedAssetImporter(CatalogStub.Schemas));

        await vm.DropFilesAsync([assetPath]);

        Assert.Single(vm.RootNodes);
        Assert.Contains(_logs, l => l.Contains("Holder.asset") && l.Contains("not a scene"));
    }

    [Fact]
    public async Task Dropping_a_file_that_is_not_an_asset_changes_nothing()
    {
        WriteScene("Main");
        string notes = Path.Combine(_assetsRoot, "Notes.txt");
        await File.WriteAllTextAsync(notes, "not an asset");
        EditorViewModel vm = await SetupAsync();

        await vm.DropFilesAsync([notes]);

        Assert.Single(vm.RootNodes);
        Assert.Contains(_logs, l => l.Contains("Notes.txt") && l.Contains("not an imported asset"));
    }

    [Fact]
    public async Task Dropping_a_same_named_file_from_outside_does_not_resolve_to_the_inside_one()
    {
        WriteScene("Main");
        WriteScene("Piece");
        EditorViewModel vm = await SetupAsync();

        string outside = Path.Combine(Path.GetTempPath(), "ee-drop-outside-" + Guid.NewGuid().ToString("N"), "Piece.scene");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        await File.WriteAllTextAsync(outside, "{}");

        try
        {
            await vm.DropFilesAsync([outside]);

            Assert.Single(vm.RootNodes);
            Assert.Contains(_logs, l => l.Contains("Piece.scene") && l.Contains("not an imported asset"));
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(outside)!, recursive: true); } catch { }
        }
    }
}
