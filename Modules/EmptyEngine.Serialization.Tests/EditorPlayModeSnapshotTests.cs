using EmptyEngine.Modules.Testing;
using EmptyEngine.Editor;
using EmptyEngine.Editor.ViewModels;
using EmptyEngine.Tests;
using Xunit;

namespace EmptyEngine.Serialization.Tests;

/// <summary>プレイ／エディット運用の契約検証（エディタ側）</summary>
public sealed class EditorPlayModeSnapshotTests
{
    private static EditorViewModel NewViewModel()
    {
        var vm = EditorFixture.NewEditor();
        return vm;
    }

    private static HierarchyNode SceneWithChild(string childName)
        => new("scene-1", "SceneA", new[] { new HierarchyNode("child-1", childName) });

    [Fact]
    public void Editing_in_edit_mode_marks_scene_dirty_with_marker()
    {
        var vm = NewViewModel();
        vm.UpdateHierarchy(new[] { SceneWithChild("Child") });

        vm.SelectedNode = vm.RootNodes[0].Children[0];
        vm.SelectedNodeName = "Renamed";

        Assert.True(vm.RootNodes[0].IsDirty);
        Assert.Equal("* SceneA", vm.RootNodes[0].DisplayName);
    }

    [Fact]
    public void Entering_play_hides_dirty_marker_and_blocks_save()
    {
        var vm = NewViewModel();
        vm.UpdateHierarchy(new[] { SceneWithChild("Child") });
        vm.SelectedNode = vm.RootNodes[0].Children[0];
        vm.SelectedNodeName = "Renamed";

        vm.SetPlayMode(true);

        Assert.False(vm.RootNodes[0].IsDirty);
        Assert.Equal("SceneA", vm.RootNodes[0].DisplayName);
        Assert.False(vm.CanSaveScene);
    }

    [Fact]
    public void Returning_to_edit_reverts_to_snapshot_and_restores_dirty()
    {
        var vm = NewViewModel();
        vm.UpdateHierarchy(new[] { SceneWithChild("Child") });
        vm.SelectedNode = vm.RootNodes[0].Children[0];
        vm.SelectedNodeName = "Renamed";

        vm.SetPlayMode(true);

        vm.SelectedNode = vm.RootNodes[0];
        vm.AddObject();
        Assert.Equal(2, vm.RootNodes[0].Children.Count);

        vm.SetPlayMode(false);

        Assert.Single(vm.RootNodes[0].Children);
        Assert.Equal("Renamed", vm.RootNodes[0].Children[0].Name);
        Assert.True(vm.RootNodes[0].IsDirty);
    }

    [Fact]
    public void Clean_scene_has_no_marker()
    {
        var vm = NewViewModel();
        vm.UpdateHierarchy(new[] { SceneWithChild("Child") });

        Assert.False(vm.RootNodes[0].IsDirty);
        Assert.Equal("SceneA", vm.RootNodes[0].DisplayName);
    }

    [Fact]
    public void Poll_is_ignored_while_authoritative_send_pending()
    {
        var vm = NewViewModel();
        vm.UpdateHierarchy(new[] { SceneWithChildren(1) });
        Assert.Equal("C0", vm.RootNodes[0].Children[0].Name);

        vm.BeginAuthoritativeSend();
        vm.UpdateHierarchy(new[] { SceneWithChildren(1, "X") });
        Assert.Equal("C0", vm.RootNodes[0].Children[0].Name);

        vm.EndAuthoritativeSend();
        vm.UpdateHierarchy(new[] { SceneWithChildren(1, "X") });
        Assert.Equal("X0", vm.RootNodes[0].Children[0].Name);
    }

    [Fact]
    public void Poll_is_ignored_while_reference_picker_is_open()
    {
        var vm = NewViewModel();
        vm.UpdateHierarchy(new[] { SceneWithChildren(1) });
        Assert.Equal("C0", vm.RootNodes[0].Children[0].Name);

        vm.BeginPickerSuspension();
        vm.UpdateHierarchy(new[] { SceneWithChildren(1, "X") });
        Assert.Equal("C0", vm.RootNodes[0].Children[0].Name);

        vm.ResumePickerSuspension();
        vm.UpdateHierarchy(new[] { SceneWithChildren(1, "X") });
        Assert.Equal("X0", vm.RootNodes[0].Children[0].Name);
    }

    [Fact]
    public void Poll_asked_before_the_latest_edit_is_ignored_after_send_completes()
    {
        var vm = NewViewModel();
        int editSequence = 1;
        vm.SetEditSequenceSource(() => editSequence);

        vm.UpdateHierarchy(new[] { SceneWithChildren(1) }, 1);
        Assert.Equal("C0", vm.RootNodes[0].Children[0].Name);

        editSequence = 2;
        vm.UpdateHierarchy(new[] { SceneWithChildren(1, "X") }, 1);
        Assert.Equal("C0", vm.RootNodes[0].Children[0].Name);

        vm.UpdateHierarchy(new[] { SceneWithChildren(1, "X") }, 2);
        Assert.Equal("X0", vm.RootNodes[0].Children[0].Name);
    }

    [Fact]
    public void Poll_asked_before_a_mode_switch_is_ignored()
    {
        var vm = NewViewModel();
        int editSequence = 1;
        vm.SetEditSequenceSource(() => editSequence);

        vm.UpdateHierarchy(new[] { SceneWithChildren(1) }, 1);
        Assert.Single(vm.RootNodes[0].Children);

        vm.SetPlayMode(true);
        editSequence = 2;
        vm.UpdateHierarchy(new[] { SceneWithChildren(7) }, 1);
        Assert.Single(vm.RootNodes[0].Children);

        // プレイ中はランタイムが権威なので、オブジェクトの増減もそのまま取り込む。
        vm.UpdateHierarchy(new[] { SceneWithChildren(9) }, 2);
        Assert.Equal(9, vm.RootNodes[0].Children.Count);

        // 停止でスナップショット（子 1 個）へ巻き戻るので、以降の poll も子 1 個で返る。
        vm.SetPlayMode(false);
        editSequence = 3;
        vm.UpdateHierarchy(new[] { SceneWithChildren(1, "X") }, 2);
        Assert.Equal("C0", vm.RootNodes[0].Children[0].Name);

        vm.UpdateHierarchy(new[] { SceneWithChildren(1, "X") }, 3);
        Assert.Equal("X0", vm.RootNodes[0].Children[0].Name);
    }

    [Fact]
    public void Object_dropped_by_the_runtime_is_reported_and_the_answer_is_discarded()
    {
        var logged = new RecordingLogger<EditorViewModel>();
        var vm = EditorFixture.NewEditor(logger: logged);

        vm.UpdateHierarchy(new[] { SceneWithChildren(2) });

        vm.UpdateHierarchy(new[] { SceneWithChildren(1, "X") });

        // 権威ツリーはそのまま＝落ちた分も、届いた名前も入らない。
        Assert.Equal(2, vm.RootNodes[0].Children.Count);
        Assert.Equal("C0", vm.RootNodes[0].Children[0].Name);
        Assert.Contains("child-1", Assert.Single(logged));

        // 同じ食い違いが続く間は言い直さない（poll ごとに出さない）。
        vm.UpdateHierarchy(new[] { SceneWithChildren(1, "X") });
        Assert.Single(logged);
    }

    [Fact]
    public void Object_added_by_the_runtime_is_reported_too()
    {
        var logged = new RecordingLogger<EditorViewModel>();
        var vm = EditorFixture.NewEditor(logger: logged);

        vm.UpdateHierarchy(new[] { SceneWithChildren(1) });
        vm.UpdateHierarchy(new[] { SceneWithChildren(2) });

        Assert.Single(vm.RootNodes[0].Children);
        Assert.Contains("child-1", Assert.Single(logged));
    }

    /// <summary>指定した個数と名前の接頭辞を持つ子オブジェクトでシーンを作成する</summary>
    private static HierarchyNode SceneWithChildren(int count, string prefix = "C")
    {
        var children = new HierarchyNode[count];
        for (int i = 0; i < count; i++)
            children[i] = new HierarchyNode($"child-{i}", $"{prefix}{i}");
        return new HierarchyNode("scene-1", "SceneA", children);
    }
}
