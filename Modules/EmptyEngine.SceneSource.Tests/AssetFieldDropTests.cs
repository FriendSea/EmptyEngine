using EmptyEngine.Modules.Testing;
using EmptyEngine.Core;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Assets;
using EmptyEngine.Editor.Catalog;
using EmptyEngine.Editor.ViewModels;
using EmptyEngine.SceneSource.Editor;
using EmptyEngine.Tests;
using EmptyEngine.World.Editor;
using Xunit;

namespace EmptyEngine.SceneSource.Tests;

/// <summary>アセット参照フィールドへ外から落としたときの解決の検証</summary>
public sealed class AssetFieldDropTests : IDisposable
{
    private const string HolderTypeName = "EmptyEngine.Modules.Testing.TestSpriteHolder";

    private readonly string _assetsRoot = Path.Combine(Path.GetTempPath(), "ee-fielddrop-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingLogger<EditorViewModel> _logs = new();

    public AssetFieldDropTests()
    {
        Directory.CreateDirectory(_assetsRoot);
        CatalogStub.Register<TestSpriteHolder>();
    }

    public void Dispose()
    {
        try { Directory.Delete(_assetsRoot, recursive: true); } catch { }
    }

    private string WriteScene(string name)
    {
        var root = new HierarchyNode(name.ToLowerInvariant() + "-root", name);
        string path = Path.Combine(_assetsRoot, name + ".scene");
        File.WriteAllText(path, SceneJsonCodec.ToJson(root));
        return path;
    }

    private string WriteTypedAsset(string name, string typeName)
    {
        string path = Path.Combine(_assetsRoot, name + ".asset");
        File.WriteAllText(path, $$"""{ "TypeName": "{{typeName}}" }""");
        return path;
    }

    private async Task<EditorViewModel> SetupAsync()
    {
        string artifacts = Path.Combine(_assetsRoot, ".artifacts");
        IAssetImporter[] importers = [new SceneAssetImporter(CatalogStub.Schemas), new TypedAssetImporter(CatalogStub.Schemas)];
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

    private static string OnlyCandidateKey(EditorViewModel vm, string? constraint) =>
        Assert.Single(vm.GetAssetCandidates(constraint)).Key.Value;

    [Fact]
    public async Task Dropping_an_asset_of_the_field_type_assigns_it()
    {
        WriteScene("Main");
        string holder = WriteTypedAsset("Holder", HolderTypeName);
        EditorViewModel vm = await SetupAsync();

        Assert.True(vm.TryResolveDroppedAsset([VsCodeUri(holder)], HolderTypeName, out AssetKey? reference));
        Assert.Equal(OnlyCandidateKey(vm, HolderTypeName), reference!.Value.Value);
    }

    [Fact]
    public async Task Dropping_a_scene_on_a_scene_field_assigns_it_as_a_prefab_reference()
    {
        WriteScene("Main");
        EditorViewModel vm = await SetupAsync();

        Assert.True(vm.TryResolveDroppedAsset(["Main.scene"], TypeKind.SceneTarget, out AssetKey? reference));
        Assert.Equal(OnlyCandidateKey(vm, TypeKind.SceneTarget), reference!.Value.Value);
    }

    [Fact]
    public async Task Dropping_by_file_name_alone_still_assigns()
    {
        WriteScene("Main");
        WriteTypedAsset("Holder", HolderTypeName);
        EditorViewModel vm = await SetupAsync();

        Assert.True(vm.TryResolveDroppedAsset(["Holder.asset"], HolderTypeName, out AssetKey? reference));
        Assert.Equal(OnlyCandidateKey(vm, HolderTypeName), reference!.Value.Value);
    }

    [Fact]
    public async Task Dropping_an_asset_of_another_type_is_refused()
    {
        WriteScene("Main");
        string holder = WriteTypedAsset("Holder", HolderTypeName);
        EditorViewModel vm = await SetupAsync();

        string constraint = typeof(TestAsset).FullName!;
        Assert.Empty(vm.GetAssetCandidates(constraint));
        Assert.False(vm.TryResolveDroppedAsset([holder], constraint, out AssetKey? reference));
        Assert.Null(reference);
        Assert.Contains(_logs, l => l.Contains("Holder.asset") && l.Contains("is not a TestAsset"));
    }

    [Fact]
    public async Task Dropping_a_scene_on_a_typed_field_is_refused()
    {
        WriteScene("Main");
        EditorViewModel vm = await SetupAsync();

        Assert.False(vm.TryResolveDroppedAsset(["Main.scene"], HolderTypeName, out AssetKey? reference));
        Assert.Null(reference);
        Assert.Contains(_logs, l => l.Contains("Main.scene") && l.Contains("is not a TestSpriteHolder"));
    }

    [Fact]
    public async Task Dropping_a_file_that_is_not_an_asset_is_refused()
    {
        WriteScene("Main");
        string notes = Path.Combine(_assetsRoot, "Notes.txt");
        await File.WriteAllTextAsync(notes, "not an asset");
        EditorViewModel vm = await SetupAsync();

        Assert.False(vm.TryResolveDroppedAsset([notes], HolderTypeName, out AssetKey? reference));
        Assert.Null(reference);
        Assert.Contains(_logs, l => l.Contains("Notes.txt") && l.Contains("not an imported asset"));
    }

    [Fact]
    public async Task A_field_without_a_type_constraint_takes_any_imported_asset()
    {
        WriteScene("Main");
        string holder = WriteTypedAsset("Holder", HolderTypeName);
        EditorViewModel vm = await SetupAsync();

        Assert.True(vm.TryResolveDroppedAsset([holder], constraintTypeName: null, out AssetKey? reference));
        Assert.False(string.IsNullOrEmpty(reference!.Value.Value));
    }

    [Fact]
    public async Task Dropping_several_files_assigns_the_first_one_that_fits()
    {
        WriteScene("Main");
        string holder = WriteTypedAsset("Holder", HolderTypeName);
        EditorViewModel vm = await SetupAsync();

        Assert.True(vm.TryResolveDroppedAsset(["Main.scene", holder], HolderTypeName, out AssetKey? reference));
        Assert.Equal(OnlyCandidateKey(vm, HolderTypeName), reference!.Value.Value);
    }
}
