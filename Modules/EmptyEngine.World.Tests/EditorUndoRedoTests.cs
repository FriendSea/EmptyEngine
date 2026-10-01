using EmptyEngine.Modules.Testing;
using EmptyEngine.Editor;
using EmptyEngine.Editor.ViewModels;
using EmptyEngine.Editor.ViewModels.Hierarchy;
using EmptyEngine.Editor.ViewModels.Inspector;
using EmptyEngine.World.Editor;
using Xunit;

namespace EmptyEngine.World.Tests;

/// <summary>undo/redo の契約検証</summary>
public sealed class EditorUndoRedoTests
{
    private static (EditorViewModel Vm, EditHistoryViewModel History) NewViewModel()
    {
        var history = new EditHistoryViewModel();
        return (EditorFixture.NewEditor(history: history), history);
    }

    private static HierarchyNode SceneWithChild(string childName)
        => new("scene-1", "SceneA", new[] { new HierarchyNode("child-1", childName) });

    [Fact]
    public void Nothing_to_undo_before_any_edit()
    {
        var (vm, history) = NewViewModel();
        vm.UpdateHierarchy(new[] { SceneWithChild("Child") });

        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void History_and_publication_capture_the_same_vm_state()
    {
        var (vm, history) = NewViewModel();
        vm.UpdateHierarchy([SceneWithChild("Child")]);
        HierarchyNodeViewModel child = vm.RootNodes[0].Children[0];
        vm.SelectedNode = child;
        IReadOnlyList<HierarchyNode>? published = null;
        vm.ScenePublished += p => published = p.Roots;
        EmptyEngine.Editor.State.EditHistory? session = null;
        history.HistoryChanged += changed => session = changed;

        vm.AddComponent(new ComponentTypeOption(typeof(TestHealth).FullName!, "Health",
            () => AuthoringTestHelpers.Authoring(new TestHealth { Current = 10, Max = 100 })));
        AuthoringObjectViewModel component = Assert.Single(child.Components);
        component.Fields.Single(field => field.Name == nameof(TestHealth.Current)).NumericValue = 42;
        vm.SetActive(child, false);

        var serializer = new HierarchyBlobSerializer(CatalogStub.Schemas);
        Assert.Equal(session!.Steps[session.Cursor].Snapshot.Blob, serializer.Serialize(published!));
        Assert.Same(child, vm.SelectedNode);
        Assert.Same(component, Assert.Single(child.Components));

        history.Undo();
        Assert.True(child.Active);
        Assert.Equal(42, AuthoringTestHelpers.GetInt(component.Capture(), nameof(TestHealth.Current)));
        history.Undo();
        Assert.Equal(10, AuthoringTestHelpers.GetInt(component.Capture(), nameof(TestHealth.Current)));
        history.Redo();
        history.Redo();
        Assert.False(child.Active);
        Assert.Equal(42, AuthoringTestHelpers.GetInt(component.Capture(), nameof(TestHealth.Current)));
    }

    [Fact]
    public void Undo_reverts_structural_edit_and_redo_reapplies_it()
    {
        var (vm, history) = NewViewModel();
        vm.UpdateHierarchy(new[] { SceneWithChild("Child") });

        vm.SelectedNode = vm.RootNodes[0];
        vm.AddObject();
        Assert.Equal(2, vm.RootNodes[0].Children.Count);
        Assert.True(history.CanUndo);

        history.Undo();
        Assert.Single(vm.RootNodes[0].Children);
        Assert.False(history.CanUndo);
        Assert.True(history.CanRedo);

        history.Redo();
        Assert.Equal(2, vm.RootNodes[0].Children.Count);
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void Undo_reverts_rename_and_restores_dirty_state()
    {
        var (vm, history) = NewViewModel();
        vm.UpdateHierarchy(new[] { SceneWithChild("Child") });
        Assert.False(vm.RootNodes[0].IsDirty);

        vm.SelectedNode = vm.RootNodes[0].Children[0];
        vm.SelectedNodeName = "Renamed";
        Assert.True(vm.RootNodes[0].IsDirty);

        history.Undo();

        Assert.Equal("Child", vm.RootNodes[0].Children[0].Name);
        Assert.False(vm.RootNodes[0].IsDirty);
    }

    [Fact]
    public void New_edit_after_undo_discards_the_redo_branch()
    {
        var (vm, history) = NewViewModel();
        vm.UpdateHierarchy(new[] { SceneWithChild("Child") });

        vm.SelectedNode = vm.RootNodes[0];
        vm.AddObject();
        history.Undo();
        Assert.True(history.CanRedo);

        vm.SelectedNode = vm.RootNodes[0].Children[0];
        vm.SelectedNodeName = "Renamed";

        Assert.False(history.CanRedo);
        Assert.Equal("Renamed", vm.RootNodes[0].Children[0].Name);
    }

    [Fact]
    public void Play_round_trip_leaves_history_untouched()
    {
        var (vm, history) = NewViewModel();
        vm.UpdateHierarchy(new[] { SceneWithChild("Child") });

        vm.SelectedNode = vm.RootNodes[0];
        vm.AddObject();
        Assert.Equal(2, vm.RootNodes[0].Children.Count);

        vm.SetPlayMode(true);
        Assert.False(history.CanUndo);

        vm.SelectedNode = vm.RootNodes[0];
        vm.AddObject();
        vm.SetPlayMode(false);

        Assert.Equal(2, vm.RootNodes[0].Children.Count);
        Assert.True(history.CanUndo);

        history.Undo();
        Assert.Single(vm.RootNodes[0].Children);
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void Undo_is_blocked_while_playing()
    {
        var (vm, history) = NewViewModel();
        vm.UpdateHierarchy(new[] { SceneWithChild("Child") });
        vm.SelectedNode = vm.RootNodes[0];
        vm.AddObject();

        vm.SetPlayMode(true);
        history.Undo();

        Assert.Equal(2, vm.RootNodes[0].Children.Count);
    }

    [Fact]
    public void Consecutive_edits_to_the_same_field_collapse_into_one_step()
    {
        var (vm, history) = NewViewModel();
        vm.UpdateHierarchy(new[] { SceneWithChild("Child") });

        vm.SelectedNode = vm.RootNodes[0].Children[0];
        vm.SelectedNodeName = "A";
        vm.SelectedNodeName = "AB";
        vm.SelectedNodeName = "ABC";

        history.Undo();

        Assert.Equal("Child", vm.RootNodes[0].Children[0].Name);
        Assert.False(history.CanUndo);
    }

}
