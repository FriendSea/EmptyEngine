using EmptyEngine.Editor.Authoring;
using EmptyEngine.Editor.ViewModels.Inspector;

using Xunit;

namespace EmptyEngine.Tests;

/// <summary>葉スカラーを編集するときの幅を宣言型が決めること</summary>
public sealed class DeclaredScalarWidthTests
{
    private static FieldViewModel Field(string typeName, FieldValue stored) =>
        new("F", TestSchemas.Scalar(typeName), stored);

    [Fact]
    public void The_schema_selects_the_float_editor()
    {
        var stored = new FieldValue() { Integer = 0 };
        FieldViewModel field = Field("System.Single", stored);

        Assert.Equal(FieldKind.Float32, field.DeclaredType.Kind);

        field.NumericValue = 0.5;

        Assert.Equal(0.5, field.Capture().Real, precision: 6);
    }

}
