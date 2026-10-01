using EmptyEngine.Editor;
using EmptyEngine.Editor.Authoring;
using EmptyEngine.Editor.ViewModels.Inspector;
using Xunit;

namespace EmptyEngine.Tests;

/// <summary>配列フィールドのインスペクタ対応の検証</summary>
public sealed class ArrayFieldTests
{
    private const string TypeName = "Probe.ArrayHolder";

    private static readonly FieldTypeInfo Single = new("System.Single", FieldKind.Float32);
    private static readonly FieldTypeInfo Text = new("System.String", FieldKind.String);
    private static readonly FieldTypeInfo Target = new("Probe.Sprite", FieldKind.ObjectReference);

    private static readonly FieldTypeInfo Point = new("Probe.Point", FieldKind.Map)
    {
        Members = new Dictionary<string, FieldTypeInfo> { ["X"] = Single, ["Y"] = Single, ["Z"] = Single },
    };

    /// <summary>配列の宣言型（要素型を持つことが「配列である」ことの定義）</summary>
    private static FieldTypeInfo ArrayOf(FieldTypeInfo element) =>
        new(element.TypeName + "[]", FieldKind.Array) { Element = element };

    private static readonly ObjectSchema Schema = new()
    {
        TypeName = TypeName,
        DisplayName = "ArrayHolder",
        Fields = new Dictionary<string, FieldTypeInfo>
        {
            ["Targets"] = ArrayOf(Target),
            ["Speeds"] = ArrayOf(Single),
            ["Waypoints"] = ArrayOf(Point),
            ["Labels"] = ArrayOf(Text),
            ["Jagged"] = ArrayOf(ArrayOf(Single)),
        },
    };

    private static AuthoringObject Probe(string field, params FieldValue[] items)
    {
        var data = new FieldValue();
        foreach (string name in Schema.Fields.Keys)
        {
            var array = new FieldValue();
            if (name == field)
                array.Items.AddRange(items);
            data.Add(name, array);
        }

        return new AuthoringObject(Schema, data);
    }

    private static FieldValue Float(double value) => new() { Real = value };

    private static FieldValue Reference(string targetId) => new() { Text = targetId };

    private static FieldValue PointValue(double x, double y, double z)
    {
        var map = new FieldValue();
        map.Add("X", Float(x));
        map.Add("Y", Float(y));
        map.Add("Z", Float(z));
        return map;
    }

    private static IReadOnlyList<string> TargetIds(FieldViewModel field) =>
        field.Capture().Items.Select(item => item.Text ?? string.Empty).ToArray();

    private static FieldViewModel FieldFor(AuthoringObject probe, string name) =>
        new AuthoringObjectViewModel(TypeName, TypeName, probe, _ => { }).Fields.Single(field => field.Name == name);

    [Fact]
    public void Jagged_arrays_are_editable_recursively()
    {
        var names = new AuthoringObjectViewModel(TypeName, TypeName, Probe("Speeds"), _ => { }).Fields.Select(f => f.Name).ToList();

        Assert.Contains("Jagged", names);
        FieldViewModel field = FieldFor(Probe("Jagged"), "Jagged");
        field.AddArrayElement();
        field.Children[0].AddArrayElement();
        field.Children[0].Children[0].NumericValue = 2.5;
        Assert.Equal(2.5, field.Capture().Items[0].Items[0].Real);
    }

    [Fact]
    public void Editing_a_numeric_element_converts_to_the_element_type()
    {
        AuthoringObject probe = Probe("Speeds", Float(1), Float(2));
        FieldViewModel field = FieldFor(probe, "Speeds");

        field.Children[0].NumericValue = 3.5d;

        Assert.Equal([3.5d, 2d], field.Capture().Items.Select(i => i.Real));
    }

    [Fact]
    public void Editing_a_composite_element_writes_the_whole_struct_back()
    {
        AuthoringObject probe = Probe("Waypoints", PointValue(1, 2, 3));
        FieldViewModel field = FieldFor(probe, "Waypoints");

        FieldViewModel y = field.Children[0].Children.Single(c => c.Name == "Y");
        y.NumericValue = 9d;

        var point = field.Capture().Items[0];
        Assert.Equal(1d, (point.Get("X")!).Real);
        Assert.Equal(9d, (point.Get("Y")!).Real);
        Assert.Equal(3d, (point.Get("Z")!).Real);
    }

    [Fact]
    public void Adding_and_editing_reference_slots_updates_the_component()
    {
        var component = new AuthoringObjectViewModel(
            TypeName, TypeName, Probe("Targets", Reference("a")), _ => { });
        FieldViewModel field = component.Fields.Single(f => f.Name == "Targets");
        Assert.True(field.IsArray);

        field.AddArrayElement();
        Assert.Equal("[2]", field.ArrayLengthText);
        Assert.Equal(["[0]", "[1]"], field.Children.Select(e => e.Name));
        Assert.All(field.Children, e =>
        {
            Assert.True(e.IsObjectReference);
            Assert.False(e.IsComposite);
        });
        Assert.Equal(["a", "(none)"], field.Children.Select(e => e.ObjectReferenceText));
        Assert.Equal(["a", ""], component.Capture().Data.Get("Targets")!.Items.Select(i => i.Text ?? ""));

        field.Children[1].AssignObjectReference("b");
        Assert.Equal(["a", "b"], component.Capture().Data.Get("Targets")!.Items.Select(i => i.Text));
    }

    [Fact]
    public void Remove_drops_the_slot_and_reindexes_the_rest()
    {
        AuthoringObject probe = Probe("Targets", Reference("a"), Reference("b"), Reference("c"));
        FieldViewModel field = FieldFor(probe, "Targets");
        FieldViewModel last = field.Children[2];

        field.Children[1].RemoveSelfFromArray();

        Assert.Equal(["a", "c"], TargetIds(field));
        Assert.Equal(["[0]", "[1]"], field.Children.Select(e => e.Name));
        Assert.Same(last, field.Children[1]);

        field.Children[1].AssignObjectReference("z");
        Assert.Equal(["a", "z"], TargetIds(field));
    }

    [Fact]
    public void Add_on_a_string_array_yields_an_empty_string_not_a_throw()
    {
        AuthoringObject probe = Probe("Labels");
        FieldViewModel field = FieldFor(probe, "Labels");

        field.AddArrayElement();

        Assert.Equal([string.Empty], field.Capture().Items.Select(i => i.Text));
    }

}
