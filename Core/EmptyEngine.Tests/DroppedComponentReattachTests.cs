using EmptyEngine.Editor;
using EmptyEngine.Editor.Authoring;
using EmptyEngine.Editor.ViewModels;
using EmptyEngine.Editor.ViewModels.Inspector;
using Xunit;

namespace EmptyEngine.Tests;

/// <summary>ランタイムが返さなかったコンポーネントの、エディタ側での付け直しの検証</summary>
public sealed class DroppedComponentReattachTests
{
    private const string KnownType = "Probe.Known";
    private const string GoneType = "Probe.Gone";

    private static AuthoringObject Component(string typeName, double value)
    {
        var data = new FieldValue();
        data.Add("Value", new FieldValue() { Real = value });
        return new AuthoringObject(TestSchemas.Object(typeName, ("Value", TestSchemas.Scalar("System.Single"))), data);
    }

    /// <summary>コンポーネント 2 つを持つオブジェクト 1 つのシーン</summary>
    private static HierarchyNode Scene(params AuthoringObject[] components)
        => new("root", "Root", [new HierarchyNode("obj", "Object", null, components)], sceneId: "scene-1");

    private static HierarchyNode Object(HierarchyNode root) => root.Children[0];

    private static double ValueOf(AuthoringObject component)
        => component.Data.Get("Value") is { IsNull: false } scalar ? scalar.Real : double.NaN;

    private static EditorViewModel Loaded(HierarchyNode scene)
    {
        var vm = EditorFixture.NewEditor();
        vm.UpdateHierarchy([scene]);
        return vm;
    }

    [Fact]
    public void Missing_component_keeps_its_values_and_card_until_the_runtime_recovers()
    {
        EditorViewModel vm = Loaded(Scene(Component(GoneType, 9), Component(KnownType, 1)));
        vm.SelectedNode = vm.RootNodes[0].Children[0];
        AuthoringObjectViewModel component = vm.SelectedComponents[0];
        int refreshes = 0;
        component.Refreshed += () => refreshes++;

        HierarchyNode polled = Scene(Component(KnownType, 1));
        vm.UpdateHierarchy([polled]);

        Assert.Equal([GoneType, KnownType], Object(polled).Components.Select(c => c.TypeName));
        Assert.Equal(9d, ValueOf(Object(polled).Components[0]));
        Assert.Equal([GoneType, KnownType], vm.SelectedComponents.Select(c => c.TypeName));
        Assert.Same(component, vm.SelectedComponents[0]);
        Assert.True(component.MissingInRuntime);
        Assert.False(vm.SelectedComponents[1].MissingInRuntime);
        Assert.True(refreshes > 0);

        refreshes = 0;
        vm.UpdateHierarchy([Scene(Component(GoneType, 9), Component(KnownType, 1))]);
        Assert.False(component.MissingInRuntime);
        Assert.True(refreshes > 0);
    }

    [Fact]
    public void Only_the_missing_one_of_two_components_of_the_same_type_comes_back()
    {
        EditorViewModel vm = Loaded(Scene(Component(KnownType, 1), Component(KnownType, 2)));

        HierarchyNode polled = Scene(Component(KnownType, 1));
        vm.UpdateHierarchy([polled]);

        Assert.Equal(2, Object(polled).Components.Count);
        Assert.Equal(1d, ValueOf(Object(polled).Components[0]));
        Assert.Equal(2d, ValueOf(Object(polled).Components[1]));
    }

    [Fact]
    public void Play_mode_keeps_a_component_the_game_destroyed()
    {
        EditorViewModel vm = Loaded(Scene(Component(GoneType, 9), Component(KnownType, 1)));
        vm.SetPlayMode(true);

        HierarchyNode polled = Scene(Component(KnownType, 1));
        vm.UpdateHierarchy([polled]);

        Assert.Single(Object(polled).Components);
    }

    [Fact]
    public void A_component_the_editor_deleted_is_not_resurrected()
    {
        EditorViewModel vm = Loaded(Scene(Component(GoneType, 9), Component(KnownType, 1)));
        vm.SelectedNode = vm.RootNodes[0].Children[0];
        vm.RemoveComponent(vm.SelectedComponents[0]);

        HierarchyNode polled = Scene(Component(KnownType, 1));
        vm.UpdateHierarchy([polled]);

        Assert.Single(Object(polled).Components);
        Assert.Equal(KnownType, Object(polled).Components[0].TypeName);
    }
}
