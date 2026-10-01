using EmptyEngine.Editor;
using EmptyEngine.Editor.Authoring;
using EmptyEngine.Editor.ViewModels.Inspector;
using Xunit;

namespace EmptyEngine.Tests;

public sealed class ComponentSnapshotTests
{
    private static readonly ObjectSchema Schema = TestSchemas.Object("Probe",
        ("Count", new("integer", FieldKind.Int)),
        ("Mask", new("unsigned", FieldKind.UInt)),
        ("Position", TestSchemas.Floats("X", "Y")),
        ("Target", new("Target", FieldKind.ObjectReference)),
        ("Tint", TestSchemas.Color("R", "G", "B", "A")),
        ("Points", new FieldTypeInfo("Point[]", FieldKind.Array)
        {
            Element = TestSchemas.Floats("X", "Y")
        }));

    private static AuthoringObject Snapshot(long count = 3)
    {
        var data = new FieldValue();
        data.Add("Count", new FieldValue { Integer = count });
        data.Add("Mask", new FieldValue { Unsigned = ulong.MaxValue });
        data.Add("Target", FieldValue.Nil());
        var tint = new FieldValue();
        foreach (string channel in new[] { "R", "G", "B", "A" })
            tint.Add(channel, new FieldValue { Real = 1 });
        data.Add("Tint", tint);
        var point = new FieldValue();
        point.Add("X", new FieldValue { Real = 0 });
        var points = new FieldValue();
        points.Items.Add(point);
        data.Add("Points", points);
        return new(Schema, data);
    }

    private static AuthoringObjectViewModel Component(AuthoringObject value, Action<string?>? edited = null) =>
        new("Probe", "Probe", value, edited ?? (_ => { }));

    [Fact]
    public void Input_and_captured_snapshots_are_independent_of_the_live_fields()
    {
        AuthoringObject input = Snapshot();
        AuthoringObjectViewModel component = Component(input);
        AuthoringObject first = component.Capture();
        input.Data.Get("Count")!.Integer = 50;
        first.Data.Get("Count")!.Integer = 60;
        Assert.Equal(3, component.Root.Get("Count")!.IntegerValue);

        component.Root.Get("Count")!.IntegerValue = 4;
        Assert.Equal(50, input.Data.Get("Count")!.Integer);
        Assert.Equal(60, first.Data.Get("Count")!.Integer);
        Assert.Equal(4, component.Capture().Data.Get("Count")!.Integer);
    }

    [Fact]
    public void Reading_missing_and_null_fields_does_not_materialize_them()
    {
        AuthoringObjectViewModel component = Component(Snapshot());
        FieldViewModel position = component.Root.Get("Position")!;
        Assert.Equal(2, position.Children.Count);
        Assert.Equal(0, position.Get("X")!.NumericValue);
        Assert.False(position.IsPresent);
        Assert.True(component.Root.Get("Target")!.IsNull);

        AuthoringObject saved = component.Capture();
        Assert.Null(saved.Data.Get("Position"));
        Assert.True(saved.Data.Get("Target")!.IsNull);

        position.Get("X")!.NumericValue = 0;
        Assert.Equal(0, component.Capture().Data.Get("Position")!.Get("X")!.Real);
        Assert.Null(component.Capture().Data.Get("Position")!.Get("Y"));
    }

    [Fact]
    public void Integer_editing_preserves_the_full_signed_and_unsigned_range()
    {
        AuthoringObjectViewModel component = Component(Snapshot());
        FieldViewModel count = component.Root.Get("Count")!;
        FieldViewModel mask = component.Root.Get("Mask")!;
        Assert.True(count.SetNumericText("9223372036854775806"));
        Assert.True(mask.SetNumericText("18446744073709551614"));
        Assert.Equal(long.MaxValue - 1, component.Capture().Data.Get("Count")!.Integer);
        Assert.Equal(ulong.MaxValue - 1, component.Capture().Data.Get("Mask")!.Unsigned);
        Assert.False(mask.SetNumericText("18446744073709551616"));
        Assert.Equal("18446744073709551614", mask.NumericText);
    }

    [Fact]
    public void Applying_a_snapshot_retains_field_identity_and_is_silent()
    {
        int edits = 0;
        AuthoringObjectViewModel component = Component(Snapshot(), _ => edits++);
        FieldViewModel count = component.Root.Get("Count")!;
        count.Edits.Hold();
        component.Apply(Snapshot(9), force: false);
        Assert.Equal(3, count.IntegerValue);

        // Undo and explicit restoration also work while a field has focus.
        AuthoringObject applied = Snapshot(7);
        FieldViewModel tint = component.Root.Get("Tint")!;
        FieldViewModel position = component.Root.Get("Position")!;
        FieldViewModel points = component.Root.Get("Points")!;
        component.Apply(applied);
        Assert.Same(count, component.Root.Get("Count"));
        Assert.Equal(7, count.IntegerValue);
        count.Edits.Release();
        Assert.Equal(0, edits);
        Assert.Same(tint, component.Root.Get("Tint"));
        Assert.Same(position, component.Root.Get("Position"));
        Assert.Same(points, component.Root.Get("Points"));
        tint.ColorValue = EditorColor.FromArgb(255, 255, 0, 0);
        position.Get("X")!.NumericValue = 3;
        points.Children[0].Get("X")!.NumericValue = 7;
        AuthoringObject captured = component.Capture();
        Assert.Equal(1d, captured.Data.Get("Tint")!.Get("R")!.Real);
        Assert.Equal(0d, captured.Data.Get("Tint")!.Get("G")!.Real);
        Assert.Equal(0d, captured.Data.Get("Tint")!.Get("B")!.Real);
        Assert.Equal(1d, captured.Data.Get("Tint")!.Get("A")!.Real);
        Assert.Equal(3d, captured.Data.Get("Position")!.Get("X")!.Real);
        Assert.Equal(7d, captured.Data.Get("Points")!.Items[0].Get("X")!.Real);
        Assert.Equal(3, edits);
        Assert.Equal(1d, applied.Data.Get("Tint")!.Get("G")!.Real);
        Assert.Equal(0d, applied.Data.Get("Points")!.Items[0].Get("X")!.Real);
    }

    [Fact]
    public void A_compound_edit_publishes_only_the_completed_value()
    {
        var publications = new List<AuthoringObject>();
        AuthoringObjectViewModel? component = null;
        component = Component(Snapshot(), _ => publications.Add(component!.Capture()));
        component.Edit("position", () =>
        {
            component.Root.Get("Position")!.Get("X")!.NumericValue = 2;
            component.Root.Get("Position")!.Get("Y")!.NumericValue = 5;
        });
        FieldValue position = Assert.Single(publications).Data.Get("Position")!;
        Assert.Equal(2, position.Get("X")!.Real);
        Assert.Equal(5, position.Get("Y")!.Real);
    }

    [Fact]
    public void A_failed_compound_edit_restores_the_value_without_publishing()
    {
        int edits = 0;
        AuthoringObjectViewModel component = Component(Snapshot(), _ => edits++);
        Assert.Throws<InvalidOperationException>(() => component.Edit("count", () =>
        {
            component.Root.Get("Count")!.IntegerValue = 10;
            throw new InvalidOperationException();
        }));
        Assert.Equal(3, component.Root.Get("Count")!.IntegerValue);
        Assert.Equal(0, edits);
    }

    [Fact]
    public void Read_only_fields_reject_scalar_reference_and_replacement_edits()
    {
        AuthoringObjectViewModel component = Component(Snapshot());
        component.Root.MarkReadOnly();
        component.Root.Get("Count")!.IntegerValue = 9;
        component.Root.Get("Target")!.AssignObjectReference("other");
        component.Root.Get("Count")!.Replace(new FieldValue { Integer = 10 });
        Assert.Equal(3, component.Capture().Data.Get("Count")!.Integer);
        Assert.True(component.Capture().Data.Get("Target")!.IsNull);
    }

    [Fact]
    public void Replacing_a_value_with_null_preserves_null_in_the_snapshot()
    {
        AuthoringObjectViewModel component = Component(Snapshot());
        FieldViewModel target = component.Root.Get("Target")!;
        target.AssignObjectReference("other");
        target.Replace(FieldValue.Nil());
        Assert.True(component.Capture().Data.Get("Target")!.IsNull);
        Assert.Null(target.ObjectReferenceId);
    }
}
