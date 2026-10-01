using EmptyEngine.Modules.Testing;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Assets;
using EmptyEngine.Editor.ViewModels;
using EmptyEngine.Editor.ViewModels.Hierarchy;
using EmptyEngine.Editor.ViewModels.Inspector;
using EmptyEngine.SceneSource.Editor;
using Xunit;

namespace EmptyEngine.SceneSource.Tests;

/// <summary>ヒエラルキーのオブジェクト／コンポーネントのコピー&amp;ペーストの検証</summary>
public sealed class EditorCopyPasteTests : IDisposable
{
    private readonly string _assetsRoot = Path.Combine(Path.GetTempPath(), "ee-copypaste-" + Guid.NewGuid().ToString("N"));

    public EditorCopyPasteTests() => Directory.CreateDirectory(_assetsRoot);

    public void Dispose()
    {
        try { Directory.Delete(_assetsRoot, recursive: true); } catch { }
    }

    private async Task<EditorViewModel> LoadedEditorAsync()
    {
        var importer = new SceneAssetImporter(CatalogStub.Schemas);
        var child = new HierarchyNode("obj", "Object", Array.Empty<HierarchyNode>(),
            AuthoringTestHelpers.Components(new TestHealth { Max = 100, Current = 10 }));
        var sceneRoot = new HierarchyNode("ignored", "MyScene", new[] { child });
        await importer.SaveAsync(sceneRoot, Path.Combine(_assetsRoot, "MyScene.scene"));

        var catalog = new AssetCatalog();
        var service = new AssetImportService(catalog, _assetsRoot, new IAssetImporter[] { importer });
        var vm = EditorFixture.NewEditor(assets: catalog, imports: service);
        await vm.InitializeAsync();
        return vm;
    }

    [Fact]
    public async Task Paste_object_creates_independent_copy_with_fresh_id()
    {
        EditorViewModel vm = await LoadedEditorAsync();

        IReadOnlyList<HierarchyNode>? published = null;
        vm.ScenePublished += p => published = p.Roots;

        HierarchyNodeViewModel child = Assert.Single(vm.RootNodes[0].Children);
        vm.SelectedNode = child;
        vm.CopySelectedObject();
        Assert.True(vm.CanPasteObject);

        vm.SelectedNode = vm.RootNodes[0];
        vm.PasteObject();

        HierarchyNode sceneRoot = Assert.Single(published!);
        Assert.Equal(2, sceneRoot.Children.Count);

        HierarchyNode original = sceneRoot.Children[0];
        HierarchyNode pasted = sceneRoot.Children[1];

        Assert.NotEqual(original.ObjectId, pasted.ObjectId);
        Assert.Equal(10, AuthoringTestHelpers.GetInt(pasted.Components.Single(), nameof(TestHealth.Current)));
        Assert.NotSame(original.Components.Single(), pasted.Components.Single());
    }

    [Fact]
    public async Task Paste_component_adds_independent_copy_to_selected_object()
    {
        EditorViewModel vm = await LoadedEditorAsync();

        IReadOnlyList<HierarchyNode>? published = null;
        vm.ScenePublished += p => published = p.Roots;

        HierarchyNodeViewModel child = Assert.Single(vm.RootNodes[0].Children);
        vm.SelectedNode = child;
        AuthoringObjectViewModel component = Assert.Single(vm.SelectedComponents);

        vm.CopyComponent(component);
        Assert.True(vm.CanPasteComponent);

        vm.PasteComponent();

        HierarchyNode childNode = Assert.Single(published!).Children.Single();
        Assert.Equal(2, childNode.Components.Count);
        Assert.All(childNode.Components, c => Assert.Equal(10, AuthoringTestHelpers.GetInt(c, nameof(TestHealth.Current))));
        Assert.NotSame(childNode.Components[0], childNode.Components[1]);
    }
}
