using EmptyEngine.Editor.Authoring;
using EmptyEngine.Editor.Components.Inspector;
using EmptyEngine.Editor.Hosting;
using EmptyEngine.Editor.ViewModels.Inspector;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EmptyEngine.Tests;

/// <summary>カーソル固定ドラッグから呼ぶ数値編集と、その保持・解除の検証</summary>
public sealed class FieldScrubTests
{
    [Theory]
    [InlineData("System.Single", 12.7)]
    [InlineData("System.Double", 12.7)]
    [InlineData("System.Int32", 13)]
    public async Task Scrubbing_preserves_numeric_types_and_holds_runtime_refreshes(string type, double expected)
    {
        using var dispatcher = new EditorDispatcher(NullLogger<EditorDispatcher>.Instance);
        var field = new FieldViewModel("Value", TestSchemas.Scalar(type), new FieldValue { Integer = 10, Real = 10 });
        using var editor = new Probe(dispatcher, field);

        Assert.Equal(10d, await editor.BeginScrub());
        Assert.True(field.Edits.IsEditing);
        await editor.SetScrubValue(12.7);
        field.Apply(new FieldValue { Integer = 99, Real = 99 });
        Assert.Equal(expected, field.NumericValue, precision: 5);

        await editor.EndScrub();
        Assert.False(field.Edits.IsEditing);
        field.Apply(new FieldValue { Integer = 99, Real = 99 });
        Assert.Equal(99d, field.NumericValue);
    }

    [Fact]
    public async Task Alpha_scrubbing_preserves_rgb_and_clamps_to_byte_range()
    {
        using var dispatcher = new EditorDispatcher(NullLogger<EditorDispatcher>.Instance);
        var field = new FieldViewModel("Color", TestSchemas.Color("R", "G", "B", "A"));
        field.ColorValue = EditorColor.FromArgb(128, 30, 60, 90);
        using var editor = new Probe(dispatcher, field);

        Assert.Equal(128d, await editor.BeginScrub());
        await editor.SetScrubValue(1000);
        Assert.Equal(EditorColor.FromArgb(255, 30, 60, 90), field.ColorValue);
        await editor.SetScrubValue(-100);
        Assert.Equal(EditorColor.FromArgb(0, 30, 60, 90), field.ColorValue);
        await editor.EndScrub();
        Assert.False(field.Edits.IsEditing);
    }

    [Fact]
    public async Task Disposing_a_field_releases_its_hold_and_ignores_late_drag_callbacks()
    {
        using var dispatcher = new EditorDispatcher(NullLogger<EditorDispatcher>.Instance);
        var field = new FieldViewModel("Value", TestSchemas.Scalar("System.Single"), new FieldValue { Real = 10 });
        var editor = new Probe(dispatcher, field);
        await editor.BeginScrub();

        await editor.DisposeAsync();
        await editor.SetScrubValue(20);
        await editor.EndScrub();
        Assert.Null(await editor.BeginScrub());
        Assert.False(field.Edits.IsEditing);
        Assert.Equal(10d, field.NumericValue);
    }

    [Fact]
    public async Task Read_only_fields_reject_dragging_without_taking_an_edit_hold()
    {
        using var dispatcher = new EditorDispatcher(NullLogger<EditorDispatcher>.Instance);
        var field = new FieldViewModel("Value", TestSchemas.Scalar("System.Single"), new FieldValue { Real = 10 });
        field.MarkReadOnly();
        using var editor = new Probe(dispatcher, field);

        Assert.Null(await editor.BeginScrub());
        await editor.SetScrubValue(20);
        Assert.False(field.Edits.IsEditing);
        Assert.Equal(10d, field.NumericValue);
    }

    private sealed class Probe : FieldEditor
    {
        public Probe(EditorDispatcher dispatcher, FieldViewModel field)
        {
            Dispatcher = dispatcher;
            Field = field;
            OnParametersSet();
        }

        protected override void OnObservedChange(string? propertyName) { }
    }
}
