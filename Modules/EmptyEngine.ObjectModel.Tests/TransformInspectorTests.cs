using EmptyEngine.Tests;
using System.Numerics;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Authoring;
using EmptyEngine.Editor.Components.Inspector;
using EmptyEngine.Editor.Hosting;
using EmptyEngine.Editor.Inspection;
using EmptyEngine.Editor.ViewModels.Inspector;
using EmptyEngine.ObjectModel.Editor;
using Xunit;

namespace EmptyEngine.ObjectModel.Tests;

/// <summary>専用インスペクタの割り当てと、Transform の TRS 編集</summary>
public sealed class TransformInspectorTests
{
    private static AuthoringObjectViewModel Component(AuthoringObject authoring, Action<string?>? onEdited = null) =>
        new(authoring.TypeName, authoring.TypeName, authoring, onEdited ?? (_ => { }));

    private static AuthoringObject Transform(Matrix4x4 matrix)
    {
        var map = new FieldValue();
        var matrixMap = new FieldValue();
        float[] values =
        [
            matrix.M11, matrix.M12, matrix.M13, matrix.M14,
            matrix.M21, matrix.M22, matrix.M23, matrix.M24,
            matrix.M31, matrix.M32, matrix.M33, matrix.M34,
            matrix.M41, matrix.M42, matrix.M43, matrix.M44,
        ];
        string[] keys =
        [
            "M11", "M12", "M13", "M14",
            "M21", "M22", "M23", "M24",
            "M31", "M32", "M33", "M34",
            "M41", "M42", "M43", "M44",
        ];
        for (int i = 0; i < keys.Length; i++)
            matrixMap.Add(keys[i], new FieldValue() { Real = values[i] });

        map.Add("Matrix", matrixMap);
        return new AuthoringObject(TestSchemas.Object(typeof(TransformComponent).FullName!, ("Matrix", TestSchemas.Floats(keys))), map);
    }

    /// <summary>属性が Razor の生成クラスに残り、起動時の走査で拾えること</summary>
    [Fact]
    public void Startup_registers_the_transform_inspector_and_keeps_the_default_fallback()
    {
        var registry = new InspectorRegistry(EditorBootstrap.FindAnnotated<InspectorForAttribute>());
        Assert.Equal(typeof(TransformInspector), registry.Resolve(Transform(Matrix4x4.Identity).Schema));
        Assert.Equal(typeof(DefaultInspector), registry.Resolve(TestSchemas.Object("Probe.Other")));
    }

    [Fact]
    public void Two_inspectors_claiming_the_same_type_are_refused()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => new InspectorRegistry([
            (typeof(TransformInspector), [new InspectorForAttribute("Probe.Component")]),
            (typeof(DefaultInspector), [new InspectorForAttribute("Probe.Component")]),
        ]));

        Assert.Contains("Probe.Component", error.Message);
    }

    [Fact]
    public void Transform_writes_edits_back_into_the_matrix_leaves()
    {
        AuthoringObject authoring = Transform(Matrix4x4.Identity);
        string? editKey = "unset";
        AuthoringObjectViewModel component = Component(authoring, key => editKey = key);
        using var model = new TransformInspector();
        model.Bind(component);

        Assert.All(new[] { model.Position, model.Rotation, model.Scale }, group =>
        {
            Assert.Equal(new[] { "X", "Y", "Z" }, group.Fields.Select(f => f.Name));
            Assert.Equal(new int?[] { 0, 1, 2 }, group.Fields.Select(f => f.AxisHint));
        });
        model.Position.Fields[0].NumericValue = 3;

        FieldValue matrixMap = Assert.IsType<FieldValue>(component.Capture().Data.Get("Matrix"));
        Assert.Equal(3d, (matrixMap.Get("M41")!).Real, precision: 5);
        Assert.Equal("pos.x", editKey);
    }

    [Fact]
    public void The_gizmo_and_the_number_fields_stay_in_sync()
    {
        using var model = new TransformInspector();
        model.Bind(Component(Transform(Matrix4x4.Identity)));
        TransformGizmoModel gizmo = model.Gizmo;

        model.Position.Fields[1].NumericValue = 5;
        Assert.Equal(5d, gizmo.PositionY, precision: 5);
        model.Position.Fields[0].NumericValue = 5;

        GizmoPoint center = TransformGizmoModel.Center;
        Assert.True(gizmo.BeginDrag(center.X, center.Y));
        gizmo.DragTo(center.X + 30, center.Y);
        gizmo.EndDrag();

        Assert.Equal(gizmo.PositionX, model.Position.Fields[0].NumericValue, precision: 4);
    }

    /// <summary>実体が差し替わっても書き込み先が古いツリーに残らないこと</summary>
    [Fact]
    public void Applying_a_snapshot_preserves_the_edit_target()
    {
        AuthoringObject authoring = Transform(Matrix4x4.Identity);
        AuthoringObjectViewModel component = Component(authoring);
        using var model = new TransformInspector();
        model.Bind(component);

        AuthoringObject polled = Transform(Matrix4x4.Identity);
        component.Apply(polled);

        model.Position.Fields[0].NumericValue = 7;

        FieldValue matrixMap = Assert.IsType<FieldValue>(component.Capture().Data.Get("Matrix"));
        Assert.Equal(7d, (matrixMap.Get("M41")!).Real, precision: 5);
    }
}
