using EmptyEngine.Editor;
using EmptyEngine.Editor.ViewModels.Hierarchy;
using Xunit;

namespace EmptyEngine.Tests;

public sealed class InspectorReferenceSelectionTests
{
    [Fact]
    public void Referenced_object_is_selected_and_its_ancestors_are_expanded()
    {
        var target = new HierarchyNode("target", "Target");
        var branch = new HierarchyNode("branch", "Branch", [target]);
        var holder = new HierarchyNode("holder", "Holder");
        var vm = EditorFixture.NewEditor();
        vm.UpdateHierarchy([new HierarchyNode("root", "Root", [branch, holder])]);

        HierarchyNodeViewModel rootVm = Assert.Single(vm.RootNodes);
        HierarchyNodeViewModel branchVm = rootVm.Children[0];
        vm.SelectedNode = rootVm.Children[1];

        Assert.True(vm.SelectReferencedObject("target"));
        Assert.Equal("target", vm.SelectedNode?.ObjectId);
        Assert.True(rootVm.IsExpanded);
        Assert.True(branchVm.IsExpanded);
    }

    [Fact]
    public void Missing_reference_does_not_change_the_hierarchy_selection()
    {
        var vm = EditorFixture.NewEditor();
        vm.UpdateHierarchy([new HierarchyNode("root", "Root", [new HierarchyNode("holder", "Holder")])]);
        vm.SelectedNode = vm.RootNodes[0].Children[0];
        HierarchyNodeViewModel before = vm.SelectedNode;

        Assert.False(vm.SelectReferencedObject("missing"));
        Assert.Same(before, vm.SelectedNode);
    }
}
