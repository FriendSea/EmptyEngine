using EmptyEngine.Editor.Authoring;
using EmptyEngine.Editor.ViewModels.Inspector;

using Xunit;

namespace EmptyEngine.Tests;

/// <summary>複合 struct を 1 行へ詰める規則</summary>
public sealed class InlineCompositeTests
{
    private static FieldTypeInfo Value(string typeName) => TestSchemas.Scalar(typeName);

    private static FieldTypeInfo StructType(string typeName, string memberTypeName, params string[] members) =>
        Value(typeName) with
        {
            Members = members.ToDictionary(m => m, _ => Value(memberTypeName), StringComparer.Ordinal),
        };

    private static FieldViewModel AxisGroup(params string[] axes) =>
        TreeField("V", StructType("Probe.Axes", "System.Single", axes), IntegerScalars(axes));

    private static FieldValue IntegerScalars(params string[] keys)
    {
        var map = new FieldValue();
        foreach (string key in keys)
            map.Add(key, new FieldValue() { Integer = 0 });
        return map;
    }

    private static FieldViewModel TreeField(string name, FieldTypeInfo declared, FieldValue value) =>
        new(name, declared, value);

    [Theory]
    [InlineData((object)new[] { "X", "Y" })]
    [InlineData((object)new[] { "X", "Y", "Z", "W" })]
    public void Small_groups_of_axis_numbers_fold_into_one_row(string[] axes)
    {
        FieldViewModel field = AxisGroup(axes);

        Assert.True(field.IsComposite);
        Assert.True(field.IsInlineComposite);
        Assert.All(field.Children, child => Assert.Equal(1, child.Name.Length));
    }

    [Fact]
    public void Five_axis_numbers_are_one_too_many()
    {
        Assert.False(AxisGroup("X", "Y", "Z", "W", "V").IsInlineComposite);
    }

    [Fact]
    public void The_declared_shape_decides_even_when_the_tree_holds_less()
    {
        FieldViewModel field = TreeField(
            "Velocity",
            StructType("System.Numerics.Vector3", "System.Single", "X", "Y", "Z"),
            IntegerScalars("X"));

        Assert.Equal(3, field.Children.Count);
        Assert.Single(field.Capture().Entries);
        Assert.True(field.IsInlineComposite);
    }

    [Fact]
    public void Integer_groups_stay_stacked()
    {
        FieldViewModel field = TreeField(
            "Cell",
            StructType("Probe.Cell", "System.Int32", "X", "Y"),
            IntegerScalars("X", "Y"));

        Assert.True(field.IsComposite);
        Assert.False(field.IsInlineComposite);
    }

    [Fact]
    public void Members_named_with_words_stay_stacked()
    {
        FieldViewModel field = TreeField(
            "Size",
            StructType("Probe.Extent", "System.Single", "Width", "Height"),
            IntegerScalars("Width", "Height"));

        Assert.True(field.IsComposite);
        Assert.False(field.IsInlineComposite);
    }
}
