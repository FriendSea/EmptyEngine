using EmptyEngine.Modules.Testing;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Authoring;
using EmptyEngine.Editor.ViewModels;
using EmptyEngine.Editor.ViewModels.Inspector;
using Xunit;

namespace EmptyEngine.Serialization.Tests;

/// <summary>連続した編集を undo 1 段に畳む単位が、欄の作られ方で変わらないこと</summary>
/// <remarks>同じ編集キーへの連続した編集は 1 段にまとまり、キーのない編集は新しい段になる。</remarks>
public sealed class EditorFieldUndoKeyTests
{
    /// <param name="field">
    /// <c>A</c> は最初から在る欄、<c>B</c> は 2 回目のポーリングでシェイプが増えて生えた欄。
    /// どちらも同じ畳み方になること
    /// </param>
    [Theory]
    [InlineData("A")]
    [InlineData("B")]
    public void Repeated_edits_of_one_field_make_a_single_undo_step(string field)
    {
        var history = new EditHistoryViewModel();
        var vm = EditorFixture.NewEditor(history: history);

        vm.UpdateHierarchy([Scene("A")]);
        vm.SelectedNode = vm.RootNodes[0].Children[0];
        AuthoringObjectViewModel component = Assert.Single(vm.SelectedComponents);

        vm.UpdateHierarchy([Scene("A", "B")]);
        Assert.Same(component, Assert.Single(vm.SelectedComponents));

        FieldViewModel edited = Assert.Single(component.Fields, f => f.Name == field);
        edited.NumericValue = 1;
        edited.NumericValue = 2;

        Assert.True(history.CanUndo);
        history.Undo();

        // 2 段積んでいたら、1 回戻しても途中の値がまだ残っている
        Assert.False(history.CanUndo);
    }

    private sealed class UndoFields { public float A { get; set; } public float B { get; set; } }

    private static HierarchyNode Scene(params string[] fields)
    {
        var data = new FieldValue();
        foreach (string field in fields)
            data.Add(field, new FieldValue() { Real = 0 });

        return new HierarchyNode(
            "scene-1", "SceneA",
            [new HierarchyNode("obj-1", "Object", components: [new AuthoringObject(CatalogStub.Schema(typeof(UndoFields)), data)],
                sceneId: "scene-1")],
            sceneId: "scene-1");
    }
}
