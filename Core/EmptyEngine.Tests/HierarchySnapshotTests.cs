using EmptyEngine.Editor;
using EmptyEngine.Editor.Authoring;
using EmptyEngine.Editor.ViewModels.Hierarchy;
using EmptyEngine.Editor.ViewModels.Inspector;
using Xunit;

namespace EmptyEngine.Tests;

public sealed class HierarchySnapshotTests
{
    private static AuthoringObject Component(double value)
    {
        var data = new FieldValue();
        data.Add("Value", new FieldValue { Real = value });
        return new AuthoringObject(
            TestSchemas.Object("SnapshotProbe", ("Value", TestSchemas.Scalar("System.Double"))), data);
    }

    private static HierarchyNode Scene() => new("root", "Scene",
        [new HierarchyNode("child", "Child", components: [Component(10), Component(20)], active: false)],
        sceneId: "scene-instance");

    [Fact]
    public void Captured_tree_preserves_values_and_is_independent_of_later_edits()
    {
        var editor = EditorFixture.NewEditor();
        HierarchyNode input = Scene();
        editor.UpdateHierarchy([input]);
        HierarchyNodeViewModel root = Assert.Single(editor.RootNodes);
        HierarchyNodeViewModel child = Assert.Single(root.Children);
        root.IsExpanded = true;
        root.IsDirty = true;

        HierarchyNode snapshot = root.Capture();
        Assert.Equal("scene-instance", snapshot.SceneId);
        Assert.Equal("root", snapshot.ObjectId);
        Assert.Equal("Scene", snapshot.Name);
        Assert.True(snapshot.Active);
        HierarchyNode capturedChild = Assert.Single(snapshot.Children);
        Assert.Equal("child", capturedChild.ObjectId);
        Assert.Equal("Child", capturedChild.Name);
        Assert.False(capturedChild.Active);
        Assert.Equal([10d, 20d], capturedChild.Components.Select(c => c.Data.Get("Value")!.Real));

        child.Name = "Edited";
        child.Active = true;
        child.Components[0].Fields[0].NumericValue = 42;
        child.Components.RemoveAt(1);
        root.Children.Clear();

        Assert.Equal("Child", capturedChild.Name);
        Assert.False(capturedChild.Active);
        Assert.Equal([10d, 20d], capturedChild.Components.Select(c => c.Data.Get("Value")!.Real));
        Assert.Single(snapshot.Children);
        Assert.Equal(10d, input.Children[0].Components[0].Data.Get("Value")!.Real);
    }

    [Fact]
    public void A_published_snapshot_cannot_change_the_live_tree_or_another_publication()
    {
        var editor = EditorFixture.NewEditor();
        editor.UpdateHierarchy([Scene()]);
        HierarchyNodeViewModel child = editor.RootNodes[0].Children[0];
        var published = new List<IReadOnlyList<HierarchyNode>>();
        editor.ScenePublished += p => published.Add(p.Roots);

        editor.SetActive(child, true);
        editor.SetActive(child, false);

        HierarchyNode first = published[0][0].Children[0];
        Assert.True(first.Active);
        Assert.False(published[1][0].Children[0].Active);
        first.Name = "Changed by consumer";
        first.Components[0].Data.Get("Value")!.Real = 99;
        Assert.Equal("Child", child.Name);
        Assert.Equal(10d, child.Components[0].Fields[0].NumericValue);
        Assert.Equal(10d, published[1][0].Children[0].Components[0].Data.Get("Value")!.Real);
    }

    [Fact]
    public void Local_edits_need_only_the_live_vm_tree()
    {
        var editor = EditorFixture.NewEditor();
        var root = new HierarchyNodeViewModel("root", "Scene") { SceneId = "instance", IsSceneRoot = true };
        editor.RootNodes.Add(root);
        editor.SelectedNode = root;
        IReadOnlyList<HierarchyNode>? published = null;
        editor.ScenePublished += p => published = p.Roots;

        editor.AddObject();
        HierarchyNodeViewModel child = Assert.Single(root.Children);
        editor.AddComponent(new ComponentTypeOption("SnapshotProbe", "Probe", () => Component(5)));
        editor.SetActive(child, false);

        HierarchyNode saved = Assert.Single(Assert.Single(published!).Children);
        Assert.Equal(child.ObjectId, saved.ObjectId);
        Assert.False(saved.Active);
        Assert.Equal(5d, Assert.Single(saved.Components).Data.Get("Value")!.Real);
        Assert.True(root.IsDirty);
    }

    [Fact]
    public void Applying_a_snapshot_retains_vm_identity_and_expansion()
    {
        var editor = EditorFixture.NewEditor();
        editor.UpdateHierarchy([Scene()]);
        HierarchyNodeViewModel root = editor.RootNodes[0];
        HierarchyNodeViewModel child = root.Children[0];
        AuthoringObjectViewModel component = child.Components[0];
        root.IsExpanded = true;
        editor.SelectedNode = child;
        HierarchyNode snapshot = root.Capture();
        snapshot.Children[0].Name = "Received";
        snapshot.Children[0].Active = true;
        snapshot.Children[0].Components[0].Data.Get("Value")!.Real = 35;

        editor.UpdateHierarchy([snapshot]);

        Assert.Same(root, editor.RootNodes[0]);
        Assert.Same(child, editor.SelectedNode);
        Assert.Same(component, child.Components[0]);
        Assert.True(root.IsExpanded);
        Assert.Equal("Received", child.Name);
        Assert.True(child.Active);
        Assert.Equal(35d, component.Fields[0].NumericValue);
        component.Fields[0].NumericValue = 50;
        Assert.Equal(35d, snapshot.Children[0].Components[0].Data.Get("Value")!.Real);
    }

    [Fact]
    public void Play_restore_and_clipboard_are_independent_even_without_a_blob_serializer()
    {
        var editor = EditorFixture.NewEditor();
        editor.UpdateHierarchy([Scene()]);
        editor.SelectedNode = editor.RootNodes[0].Children[0];
        editor.CopySelectedObject();
        editor.SetPlayMode(true);
        editor.SelectedComponents[0].Fields[0].NumericValue = 70;
        editor.SetActive(editor.SelectedNode!, true);
        editor.SetPlayMode(false);

        HierarchyNodeViewModel restored = editor.RootNodes[0].Children[0];
        Assert.False(restored.Active);
        Assert.Equal(10d, restored.Components[0].Capture().Data.Get("Value")!.Real);

        editor.SelectedNode = editor.RootNodes[0];
        editor.PasteObject();
        HierarchyNodeViewModel pasted = editor.SelectedNode!;
        pasted.Components[0].Fields[0].NumericValue = 80;
        editor.SelectedNode = editor.RootNodes[0];
        editor.PasteObject();
        Assert.Equal(10d, editor.SelectedComponents[0].Fields[0].NumericValue);
        Assert.NotEqual(restored.ObjectId, pasted.ObjectId);
        Assert.NotEqual(pasted.ObjectId, editor.SelectedNode!.ObjectId);
    }
}
