using EmptyEngine.Editor.Authoring;
using EmptyEngine.Editor.ViewModels.Inspector;
using Xunit;

namespace EmptyEngine.Tests;

/// <summary>カラーピッカーを当てる型をカタログの宣言だけで決めることの検証</summary>
public sealed class ColorFieldTests
{
    private static FieldValue Float(double value) => new() { Real = value };

    private static FieldValue Channels(params (string Name, double Value)[] channels)
    {
        var map = new FieldValue();
        foreach ((string name, double value) in channels)
            map.Add(name, Float(value));
        return map;
    }

    private static FieldValue Tagged(string tag, FieldValue payload)
    {
        var map = new FieldValue();
        map.Add(tag, payload);
        return map;
    }

    /// <summary>色のバリアントと、色を名乗らないバリアントを 1 つずつ持つ union</summary>
    private static FieldTypeInfo TaggedColor() => new("Test.TaggedColor", FieldKind.Union)
    {
        Members = new Dictionary<string, FieldTypeInfo>(StringComparer.Ordinal)
        {
            ["Srgba"] = TestSchemas.Color("red", "green", "blue", "alpha"),
            ["Hsla"] = TestSchemas.Floats("hue", "saturation", "lightness", "alpha"),
        },
    };

    private static FieldViewModel Field(FieldTypeInfo type, FieldValue? value) => new("Color", type, value);

    private static double Channel(FieldValue map, string name) =>
        map.Get(name) is { IsNull: false } scalar ? scalar.Real : double.NaN;

    /// <summary>チャンネル名は宣言から来る（エディタは綴りの一覧を持たない）</summary>
    [Fact]
    public void Four_numbers_without_a_declaration_are_not_a_color()
    {
        FieldViewModel field = Field(
            TestSchemas.Floats("R", "G", "B", "A"),
            Channels(("R", 1), ("G", 1), ("B", 1), ("A", 1)));

        Assert.False(field.IsColor);
        Assert.True(field.IsComposite);
    }

    /// <summary>色かどうかは型が決めるので、値が無くても変わらない</summary>
    [Fact]
    public void An_absent_value_is_still_a_color()
    {
        Assert.True(Field(TestSchemas.Color("R", "G", "B", "A"), FieldValue.Nil()).IsColor);
        Assert.True(Field(TestSchemas.Color("R", "G", "B", "A"), value: null).IsColor);
    }

    /// <summary>色として宣言されていないバリアントを選んでも、union は色として表示されない。</summary>
    [Fact]
    public void A_variant_that_is_not_a_color_leaves_the_union_alone()
    {
        FieldViewModel field = Field(
            TaggedColor(),
            Tagged("Hsla", Channels(("hue", 0), ("saturation", 1), ("lightness", 1), ("alpha", 1))));

        Assert.False(field.IsColor);
        Assert.True(field.IsComposite);
        Assert.False(Assert.Single(field.Children).IsColor);
    }

    [Fact]
    public void Editing_a_color_inside_a_variant_writes_into_the_payload()
    {
        FieldViewModel field = Field(
            TaggedColor(),
            Tagged("Srgba", Channels(("red", 1), ("green", 1), ("blue", 1), ("alpha", 1))));

        Assert.False(field.IsColor);
        Assert.True(field.IsComposite);
        FieldViewModel color = Assert.Single(field.Children);
        Assert.True(color.IsColor);
        Assert.False(color.IsComposite);
        color.ColorValue = EditorColor.FromArgb(255, 255, 0, 0);

        FieldValue map = field.Capture();
        FieldValue payload = map.Get("Srgba")!;
        Assert.Equal(["Srgba"], map.Entries.Select(entry => entry.Key));
        Assert.Equal(1d, Channel(payload, "red"));
        Assert.Equal(0d, Channel(payload, "green"));
        Assert.Equal(0d, Channel(payload, "blue"));
        Assert.Equal(1d, Channel(payload, "alpha"));
    }
}
