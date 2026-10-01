using EmptyEngine.Editor;
using EmptyEngine.Editor.ViewModels;
using EmptyEngine.Editor.ViewModels.Hierarchy;
using EmptyEngine.Editor.ViewModels.Utils;
using Xunit;

namespace EmptyEngine.Tests;

public sealed class ReconcileTests
{
    [Theory]
    [InlineData("c,b,a")]
    [InlineData("b,c")]
    [InlineData("d,a,b,c")]
    [InlineData("c,d,a")]
    [InlineData("")]
    public void Structural_changes_publish_only_the_completed_order(string order)
    {
        var a = new HierarchyNodeViewModel("a", "A");
        var b = new HierarchyNodeViewModel("b", "B");
        var c = new HierarchyNodeViewModel("c", "C");
        var collection = new SnapshotCollection<HierarchyNodeViewModel>([a, b, c]);
        IReadOnlyList<HierarchyNodeViewModel> before = collection.Snapshot;
        var published = new List<IReadOnlyList<HierarchyNodeViewModel>>();
        collection.CollectionChanged += (_, _) => published.Add(collection.Snapshot);
        string[] ids = order.Split(',', StringSplitOptions.RemoveEmptyEntries);

        ReconcileNodes(collection, ids);

        foreach (IReadOnlyList<HierarchyNodeViewModel> snapshot in published)
            Assert.Equal(snapshot.Count, snapshot.Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.Equal(ids, Assert.Single(published).Select(node => node.ObjectId));
        Assert.Equal(ids, collection.Select(node => node.ObjectId));
        Assert.Equal(new[] { a, b, c }, before);
        foreach (HierarchyNodeViewModel retained in collection.Where(node => node.ObjectId != "d"))
            Assert.Same(before.Single(node => node.ObjectId == retained.ObjectId), retained);
    }

    [Fact]
    public void Unchanged_order_updates_existing_views_without_publishing_a_new_snapshot()
    {
        var a = new HierarchyNodeViewModel("a", "Old A");
        var b = new HierarchyNodeViewModel("b", "Old B");
        var collection = new SnapshotCollection<HierarchyNodeViewModel>([a, b]);
        IReadOnlyList<HierarchyNodeViewModel> before = collection.Snapshot;
        int notifications = 0;
        collection.CollectionChanged += (_, _) => notifications++;

        ReconcileNodes(collection, ["a", "b"]);

        Assert.Equal(0, notifications);
        Assert.Same(before, collection.Snapshot);
        Assert.Equal("Updated a", a.Name);
        Assert.Equal("Updated b", b.Name);
    }

    [Fact]
    public void Repeated_model_keys_reuse_distinct_views_in_occurrence_order()
    {
        var first = new HierarchyNodeViewModel("a", "First");
        var second = new HierarchyNodeViewModel("a", "Second");
        var b = new HierarchyNodeViewModel("b", "B");
        var collection = new SnapshotCollection<HierarchyNodeViewModel>([first, b, second]);

        ReconcileNodes(collection, ["b", "a", "a", "a"]);

        Assert.Equal(new[] { b, first, second }, collection.Take(3));
        Assert.Equal(4, collection.Snapshot.Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.Equal("a", collection[3].ObjectId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Hierarchy_reordering_preserves_selection_and_expansion_without_duplicate_row_keys(bool roots)
    {
        var editor = EditorFixture.NewEditor();
        editor.UpdateHierarchy(Hierarchy(["a", "b", "c"]));
        SnapshotCollection<HierarchyNodeViewModel> siblings = roots
            ? editor.RootNodes
            : Assert.Single(editor.RootNodes).Children;
        HierarchyNodeViewModel a = siblings[0];
        HierarchyNodeViewModel b = siblings[1];
        HierarchyNodeViewModel c = siblings[2];
        editor.SelectedNode = b;
        a.IsExpanded = true;
        var published = new List<IReadOnlyList<HierarchyNodeViewModel>>();
        siblings.CollectionChanged += (_, _) => published.Add(siblings.Snapshot);

        editor.UpdateHierarchy(Hierarchy(["c", "a", "b"]));

        foreach (IReadOnlyList<HierarchyNodeViewModel> snapshot in published)
            Assert.Equal(snapshot.Count, snapshot.Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.Equal(new[] { c, a, b }, Assert.Single(published));
        Assert.Same(b, editor.SelectedNode);
        Assert.True(a.IsExpanded);

        HierarchyNode[] Hierarchy(string[] ids)
        {
            HierarchyNode[] nodes = ids.Select(id => new HierarchyNode(id, id, sceneId: roots ? id : "scene")).ToArray();
            return roots ? nodes : [new HierarchyNode("root", "Root", nodes, sceneId: "scene")];
        }
    }

    private static void ReconcileNodes(SnapshotCollection<HierarchyNodeViewModel> collection, string[] ids) =>
        Reconcile.Into(collection, ids, node => node.ObjectId, id => id,
            (node, id) => node.Name = "Updated " + id,
            id => new HierarchyNodeViewModel(id, "Created " + id));
}
