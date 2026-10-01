using EmptyEngine.Modules.Testing;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Assets;
using EmptyEngine.Editor.ViewModels;
using EmptyEngine.Editor.ViewModels.Inspector;
using EmptyEngine.SceneSource.Editor;
using Xunit;

namespace EmptyEngine.SceneSource.Tests;

/// <summary>フィールド編集が編集中もランタイムへ流れることの検証</summary>
public sealed class EditorEditStreamingTests : IDisposable
{
    private readonly string _assetsRoot = Path.Combine(Path.GetTempPath(), "ee-editstream-" + Guid.NewGuid().ToString("N"));

    public EditorEditStreamingTests() => Directory.CreateDirectory(_assetsRoot);

    public void Dispose()
    {
        try { Directory.Delete(_assetsRoot, recursive: true); } catch { }
    }

    [Fact]
    public async Task Field_edits_reach_the_runtime_while_the_drag_is_still_going()
    {
        (EditorViewModel vm, FieldViewModel field) = await LoadHealthSceneAsync();

        var gate = new object();
        var sent = new List<int>();
        vm.ScenePublished += p => Record(gate, sent, p.Roots);

        for (int i = 1; i <= 20; i++)
        {
            field.NumericValue = i;
            await Task.Delay(10);
        }

        int duringDrag = Count(gate, sent);
        Assert.True(duringDrag >= 2, $"Edits were not sent during the drag (send count {duringDrag})");
    }

    [Fact]
    public async Task Last_value_of_a_burst_is_delivered()
    {
        (EditorViewModel vm, FieldViewModel field) = await LoadHealthSceneAsync();

        var gate = new object();
        var sent = new List<int>();
        vm.ScenePublished += p => Record(gate, sent, p.Roots);

        for (int i = 1; i <= 5; i++)
            field.NumericValue = i;

        await Task.Delay(300);

        List<int> values = Snapshot(gate, sent);
        Assert.NotEmpty(values);
        Assert.Equal(5, values[^1]);
    }

    private static void Record(object gate, List<int> sink, IReadOnlyList<HierarchyNode> roots)
    {
        int value = AuthoringTestHelpers.GetInt(
            roots[0].Children[0].Components[0], nameof(TestHealth.Current));
        lock (gate) sink.Add(value);
    }

    [Fact]
    public async Task Structural_edit_supersedes_a_pending_field_snapshot()
    {
        (EditorViewModel vm, FieldViewModel field) = await LoadHealthSceneAsync();
        var sent = new System.Collections.Concurrent.ConcurrentQueue<IReadOnlyList<HierarchyNode>>();
        vm.ScenePublished += p => sent.Enqueue(p.Roots);

        field.NumericValue = 11;
        field.NumericValue = 12;
        vm.SetActive(vm.SelectedNode!, false);
        await Task.Delay(300);

        IReadOnlyList<HierarchyNode>[] snapshots = sent.ToArray();
        Assert.True(snapshots.Length >= 2);
        int structural = Array.FindIndex(snapshots, roots => !roots[0].Children[0].Active);
        Assert.True(structural >= 0);
        Assert.All(snapshots.Skip(structural), roots => Assert.False(roots[0].Children[0].Active));
        Assert.Equal(12, AuthoringTestHelpers.GetInt(
            snapshots[^1][0].Children[0].Components[0], nameof(TestHealth.Current)));

        // Keeping a sent tree must retain the field value from that particular edit.
        Assert.Equal(11, AuthoringTestHelpers.GetInt(
            snapshots[0][0].Children[0].Components[0], nameof(TestHealth.Current)));
    }

    private static int Count(object gate, List<int> sink) { lock (gate) return sink.Count; }

    private static List<int> Snapshot(object gate, List<int> sink) { lock (gate) return sink.ToList(); }

    private async Task<(EditorViewModel Vm, FieldViewModel Field)> LoadHealthSceneAsync()
    {
        var importer = new SceneAssetImporter(CatalogStub.Schemas);
        var child = new HierarchyNode("obj", "Object", Array.Empty<HierarchyNode>(),
            AuthoringTestHelpers.Components(new TestHealth { Max = 100, Current = 10 }));
        await importer.SaveAsync(new HierarchyNode("ignored", "MyScene", new[] { child }),
            Path.Combine(_assetsRoot, "MyScene.scene"));

        var catalog = new AssetCatalog();
        var service = new AssetImportService(catalog, _assetsRoot, new IAssetImporter[] { importer });
        var vm = EditorFixture.NewEditor(assets: catalog, imports: service);
        await vm.InitializeAsync();

        vm.SelectedNode = Assert.Single(vm.RootNodes[0].Children);
        AuthoringObjectViewModel component = Assert.Single(vm.SelectedComponents);
        return (vm, component.Fields.Single(f => f.Name == nameof(TestHealth.Current)));
    }
}
