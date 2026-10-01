using EmptyEngine.Modules.Testing;
using EmptyEngine.Editor.Authoring;
using EmptyEngine.Core;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Assets;
using EmptyEngine.Editor.Catalog;
using EmptyEngine.Editor.ViewModels;
using EmptyEngine.Editor.ViewModels.Inspector;
using EmptyEngine.SceneSource.Editor;
using Xunit;

namespace EmptyEngine.SceneSource.Tests;

/// <summary>アセット参照フィールドから元のソースファイルへ戻れることの検証</summary>
public sealed class AssetFieldRevealTests : IDisposable
{
    private const string HolderTypeName = "EmptyEngine.Modules.Testing.TestSpriteHolder";

    private readonly string _assetsRoot = Path.Combine(Path.GetTempPath(), "ee-fieldreveal-" + Guid.NewGuid().ToString("N"));

    public AssetFieldRevealTests()
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

    private async Task<(EditorViewModel Editor, AssetCatalog Catalog)> SetupAsync()
    {
        string artifacts = Path.Combine(_assetsRoot, ".artifacts");
        IAssetImporter[] importers = [new SceneAssetImporter(CatalogStub.Schemas), new TypedAssetImporter(CatalogStub.Schemas)];
        var catalog = new AssetCatalog();
        var service = new AssetImportService(
            catalog, _assetsRoot, importers, stampRootPath: Path.Combine(artifacts, "import-stamps"));
        service.SetArtifacts(TestArtifacts.At(Path.Combine(artifacts, "store")));

        var vm = EditorFixture.NewEditor(assets: catalog, imports: service);
        await vm.InitializeAsync();
        return (vm, catalog);
    }

    private static FieldViewModel EmptyAssetField() =>
        new("Sprite", new FieldTypeInfo(string.Empty, FieldKind.AssetReference));

    [Fact]
    public async Task Asset_reference_tracks_empty_assignment_clearing_and_polled_keys()
    {
        string holder = WriteTypedAsset("Holder", HolderTypeName);
        (EditorViewModel vm, AssetCatalog catalog) = await SetupAsync();
        AssetKey key = Assert.Single(vm.GetAssetCandidates(HolderTypeName)).Key;
        FieldViewModel field = EmptyAssetField();
        Assert.Null(field.AssetKey);

        foreach (FieldValue empty in new[] { new FieldValue(), FieldValue.Nil() })
        {
            field.Apply(empty);
            field.AssignAssetReference(key);
            Assert.False(field.Capture().IsNull);
            Assert.Equal(key.Value, field.Capture().ReferenceKey);
            Assert.True(catalog.TryGetSourcePath(field.AssetKey!, out string? sourcePath));
            Assert.Equal(Path.GetFullPath(holder), Path.GetFullPath(sourcePath!));
            field.ClearAssetReference();
            Assert.Null(field.AssetKey);
        }

        field.Apply(new FieldValue { Text = key.Value });
        Assert.Equal(key.Value, field.AssetKey);
    }

    [Fact]
    public async Task A_scene_reference_resolves_back_to_the_scene_source()
    {
        string scene = WriteScene("Main");
        (EditorViewModel vm, AssetCatalog catalog) = await SetupAsync();

        FieldViewModel field = EmptyAssetField();
        field.AssignAssetReference(Assert.Single(vm.GetAssetCandidates(TypeKind.SceneTarget)).Key);

        Assert.True(catalog.TryGetSourcePath(field.AssetKey!, out string? sourcePath));
        Assert.Equal(Path.GetFullPath(scene), Path.GetFullPath(sourcePath!));
    }

}
