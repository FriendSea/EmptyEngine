using EmptyEngine.Modules.Testing;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Assets;
using EmptyEngine.Editor.State;
using EmptyEngine.Editor.ViewModels;
using EmptyEngine.Editor.ViewModels.Hierarchy;
using EmptyEngine.SceneSource.Editor;
using Xunit;

namespace EmptyEngine.World.Tests;

/// <summary>建て直し後にディスクのセッションから世界と undo 履歴を取り戻す経路の検証</summary>
public sealed class EditorSessionHandoverTests : IDisposable
{
    private readonly string _assetsRoot = Path.Combine(Path.GetTempPath(), "ee-session-" + Guid.NewGuid().ToString("N"));
    private readonly string _sessionRoot = Path.Combine(Path.GetTempPath(), "ee-handover-" + Guid.NewGuid().ToString("N"));
    private readonly AssetCatalog _catalog = new();

    public EditorSessionHandoverTests()
    {
        Directory.CreateDirectory(_assetsRoot);
        Directory.CreateDirectory(_sessionRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_assetsRoot, recursive: true); } catch { }
        try { Directory.Delete(_sessionRoot, recursive: true); } catch { }
    }

    private async Task<AssetImportService> ImportServiceWithSceneAsync(string sceneName)
    {
        var importer = new SceneAssetImporter(CatalogStub.Schemas);
        var sceneRoot = new HierarchyNode("ignored", sceneName, new[] { new HierarchyNode("obj", "Object") });
        await importer.SaveAsync(sceneRoot, Path.Combine(_assetsRoot, sceneName + ".scene"));
        return new AssetImportService(_catalog, _assetsRoot, new IAssetImporter[] { importer });
    }

    private (EditorViewModel Vm, EditHistoryViewModel History, List<EditHistory> Committed) NewEditor(
        AssetImportService service, EditorStateStore? store = null)
    {
        var history = new EditHistoryViewModel();
        var vm = EditorFixture.NewEditor(assets: _catalog, imports: service, state: store, history: history);

        var committed = new List<EditHistory>();
        history.HistoryChanged += committed.Add;
        return (vm, history, committed);
    }

    private EditHistory Handover(EditHistory history)
    {
        EditHistoryStore store = NewStore();
        store.SaveAsync(history).GetAwaiter().GetResult();
        return store.TryLoad()!;
    }

    private EditHistoryStore NewStore()
    {
        string root = Path.Combine(_sessionRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new EditHistoryStore(root);
    }

    private static void AddObjectAtRoot(EditorViewModel vm)
    {
        vm.SelectedNode = vm.RootNodes[0];
        vm.AddObject();
    }

    [Fact]
    public async Task Unsaved_edits_survive_an_editor_restart()
    {
        AssetImportService service = await ImportServiceWithSceneAsync("MyScene");

        (EditorViewModel before, _, List<EditHistory> committed) = NewEditor(service);
        await before.InitializeAsync();
        before.AddObject();

        HierarchyNodeViewModel rootBefore = Assert.Single(before.RootNodes);
        Assert.Equal(2, rootBefore.Children.Count);

        (EditorViewModel after, _, _) = NewEditor(service);
        IReadOnlyList<HierarchyNode>? sent = null;
        after.ScenePublished += p => sent = p.Roots;

        await after.InitializeAsync(Handover(committed[^1]));

        HierarchyNodeViewModel rootAfter = Assert.Single(after.RootNodes);
        Assert.Equal("MyScene", rootAfter.Name);
        Assert.Equal(2, rootAfter.Children.Count);
        Assert.Contains(rootAfter.Children, c => c.Name == "GameObject");

        Assert.NotNull(sent);
        Assert.Equal(2, Assert.Single(sent!).Children.Count);
    }

    [Fact]
    public async Task Undo_history_survives_an_editor_restart()
    {
        AssetImportService service = await ImportServiceWithSceneAsync("MyScene");

        (EditorViewModel before, _, List<EditHistory> committed) = NewEditor(service);
        await before.InitializeAsync();
        AddObjectAtRoot(before);
        AddObjectAtRoot(before);

        (EditorViewModel after, EditHistoryViewModel afterHistory, _) = NewEditor(service);
        await after.InitializeAsync(Handover(committed[^1]));

        Assert.Equal(3, Assert.Single(after.RootNodes).Children.Count);

        Assert.True(afterHistory.CanUndo);
        afterHistory.Undo();
        Assert.Equal(2, Assert.Single(after.RootNodes).Children.Count);
        afterHistory.Undo();
        Assert.Single(Assert.Single(after.RootNodes).Children);

        Assert.False(afterHistory.CanUndo);

        Assert.True(afterHistory.CanRedo);
        afterHistory.Redo();
        Assert.Equal(2, Assert.Single(after.RootNodes).Children.Count);
    }

    [Fact]
    public async Task Restart_after_undo_lands_where_the_cursor_was()
    {
        AssetImportService service = await ImportServiceWithSceneAsync("MyScene");

        (EditorViewModel before, EditHistoryViewModel beforeHistory, List<EditHistory> committed) = NewEditor(service);
        await before.InitializeAsync();
        AddObjectAtRoot(before);
        AddObjectAtRoot(before);
        beforeHistory.Undo();

        (EditorViewModel after, EditHistoryViewModel afterHistory, _) = NewEditor(service);
        await after.InitializeAsync(Handover(committed[^1]));

        Assert.Equal(2, Assert.Single(after.RootNodes).Children.Count);
        Assert.True(afterHistory.CanRedo);
        afterHistory.Redo();
        Assert.Equal(3, Assert.Single(after.RootNodes).Children.Count);
    }

    [Fact]
    public async Task Restored_session_keeps_the_scene_saveable_and_dirty()
    {
        AssetImportService service = await ImportServiceWithSceneAsync("MyScene");

        (EditorViewModel before, _, List<EditHistory> committed) = NewEditor(service);
        await before.InitializeAsync();
        before.AddObject();

        (EditorViewModel after, _, _) = NewEditor(service);
        await after.InitializeAsync(Handover(committed[^1]));

        HierarchyNodeViewModel root = Assert.Single(after.RootNodes);
        after.SelectedNode = root;

        Assert.True(after.CanSaveScene);
        Assert.True(root.IsDirty);
    }

    [Fact]
    public async Task Play_mode_is_not_handed_over()
    {
        AssetImportService service = await ImportServiceWithSceneAsync("MyScene");

        (EditorViewModel before, _, List<EditHistory> committed) = NewEditor(service);
        await before.InitializeAsync();
        before.AddObject();
        int commitsBeforePlay = committed.Count;

        before.SetPlayMode(true);
        before.AddObject();
        Assert.Equal(commitsBeforePlay, committed.Count);

        (EditorViewModel after, _, _) = NewEditor(service);
        await after.InitializeAsync(Handover(committed[^1]));

        Assert.False(after.IsPlaying);
        HierarchyNodeViewModel root = Assert.Single(after.RootNodes);
        Assert.Equal(2, root.Children.Count);
    }

    [Fact]
    public async Task Unreadable_session_falls_back_to_the_normal_startup_path()
    {
        AssetImportService service = await ImportServiceWithSceneAsync("MyScene");

        var unreadable = new EditHistory(
            [
                new EditHistoryStep(1, new SceneSnapshot(
                    Blob: new byte[] { 9, 9, 9, 9 },
                    LoadedSceneKeys: new Dictionary<string, string>(StringComparer.Ordinal),
                    DirtySceneIds: new HashSet<string>(StringComparer.Ordinal))),
            ],
            Cursor: 0);

        (EditorViewModel vm, _, _) = NewEditor(service);
        await vm.InitializeAsync(Handover(unreadable));

        HierarchyNodeViewModel root = Assert.Single(vm.RootNodes);
        Assert.Equal("MyScene", root.Name);
        Assert.Single(root.Children);
    }
}
