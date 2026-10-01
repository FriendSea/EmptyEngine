using EmptyEngine.Editor.Authoring;
using EmptyEngine.Editor.ViewModels.Inspector;
using Xunit;

namespace EmptyEngine.Tests;

/// <summary>インスペクタ数値フィールドの編集判定の検証</summary>
public sealed class FieldViewModelTests
{
    [Theory]
    [InlineData(0.1f)]
    [InlineData(-5.6155797E-21f)]
    public void Lossy_decimal_roundtrip_writeback_is_not_an_edit(float value)
    {
        int edits = 0;
        var field = new FieldViewModel("F", new FieldTypeInfo("float", FieldKind.Float32), new FieldValue { Real = value }, _ => edits++);

        field.NumericValue = (double)(decimal)field.NumericValue;

        Assert.Equal(value, field.Capture().Real);
        Assert.Equal(0, edits);
    }

    [Fact]
    public void Real_numeric_edit_updates_the_vm_and_notifies_once()
    {
        int edits = 0;
        var field = new FieldViewModel("F", new FieldTypeInfo("float", FieldKind.Float32), new FieldValue { Real = 0.1f }, _ => edits++);

        field.NumericValue = 0.5;

        Assert.Equal(0.5, field.Capture().Real);
        Assert.Equal(1, edits);
    }
}
