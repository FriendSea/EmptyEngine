using EmptyEngine.Editor;
using EmptyEngine.Editor.Authoring;
using Xunit;

namespace EmptyEngine.Tests;

public sealed class FieldValueTests
{
    [Fact]
    public void Missing_and_null_values_remain_distinct()
    {
        var data = new FieldValue();
        data.Add("Empty", FieldValue.Nil());
        Assert.True(data.Get("Empty")!.IsNull);
        Assert.Null(data.Get("Missing"));
    }

    [Fact]
    public void Clone_shares_only_the_schema_and_copies_all_values()
    {
        var data = new FieldValue();
        var children = new FieldValue();
        children.Items.Add(new FieldValue { Integer = 1 });
        data.Add("Items", children);
        data.Add("Integer", new FieldValue { Integer = long.MinValue });
        data.Add("Unsigned", new FieldValue { Unsigned = ulong.MaxValue });
        var schema = TestSchemas.Object("Probe", ("Items", new FieldTypeInfo("System.Int64[]", FieldKind.Array) { Element = TestSchemas.Scalar("System.Int64") }),
            ("Integer", TestSchemas.Scalar("System.Int64")), ("Unsigned", TestSchemas.Scalar("System.UInt64")));
        var original = new AuthoringObject(schema, data);
        AuthoringObject clone = original.Clone();
        Assert.Same(schema, clone.Schema);
        Assert.Equal(long.MinValue, clone.Data.Get("Integer")!.Integer);
        Assert.Equal(ulong.MaxValue, clone.Data.Get("Unsigned")!.Unsigned);
        clone.Data.Get("Items")!.Items[0].Integer = 99;
        clone.Data.Get("Items")!.Items.Add(FieldValue.Nil());
        Assert.Equal(1, children.Items[0].Integer);
        Assert.Single(children.Items);
    }
}
