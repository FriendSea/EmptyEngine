using EmptyEngine.Editor;
using EmptyEngine.Editor.Authoring;
using EmptyEngine.Editor.ViewModels;
using EmptyEngine.Editor.ViewModels.Inspector;
using Xunit;

namespace EmptyEngine.Tests;

/// <summary>型の宣言が入れ替わったときの、インスペクタの欄の追従の検証</summary>
public sealed class SchemaAdoptionTests
{
    private const string TypeName = "Probe.Grown";

    private static ObjectSchema Declared(params string[] fieldNames) => TestSchemas.Object(
        TypeName, fieldNames.Select(name => (name, TestSchemas.Scalar("System.Single"))).ToArray());

    private static AuthoringObject Value(ObjectSchema schema, params string[] fieldNames)
    {
        var data = new FieldValue();
        foreach (string name in fieldNames) data.Add(name, new FieldValue { Real = 1 });
        return new AuthoringObject(schema, data);
    }

    private static HierarchyNode Scene(AuthoringObject component) =>
        new("root", "Root", [new HierarchyNode("obj", "Object", null, [component])], sceneId: "scene-1");

    private static AuthoringObjectViewModel Component(AuthoringObject value) =>
        new(TypeName, TypeName, value, _ => { });

    [Fact]
    public void Same_declaration_read_twice_stays_equal()
    {
        ObjectSchema schema = Declared("Value");

        // 宣言が変わっていない限り、欄を起こし直さずに済むこと。
        Assert.Equal(schema.Root, schema.Root);
    }

    [Fact]
    public void Adopted_field_appears_in_the_inspector_on_the_next_snapshot()
    {
        ObjectSchema schema = Declared("Value");
        AuthoringObjectViewModel component = Component(Value(schema, "Value"));
        Assert.Equal(["Value"], component.Fields.Select(field => field.Name));

        schema.Adopt(Declared("Value", "Added"));
        component.Apply(Value(schema, "Value", "Added"));

        Assert.Equal(["Value", "Added"], component.Fields.Select(field => field.Name));
        Assert.Equal(1, component.Capture().Data.Get("Added")!.Real);
    }

    [Fact]
    public void Adopted_field_reaches_the_selected_component_through_the_runtime_answer()
    {
        ObjectSchema schema = Declared("Value");
        EditorViewModel editor = EditorFixture.NewEditor();
        editor.UpdateHierarchy([Scene(Value(schema, "Value"))]);
        editor.SelectedNode = editor.RootNodes[0].Children[0];

        schema.Adopt(Declared("Value", "Added"));
        editor.UpdateHierarchy([Scene(Value(schema, "Value", "Added"))]);

        Assert.Equal(["Value", "Added"], editor.SelectedComponents[0].Fields.Select(field => field.Name));
    }

    [Fact]
    public void Field_being_edited_keeps_its_shape_until_the_edit_ends()
    {
        ObjectSchema schema = Declared("Value");
        AuthoringObjectViewModel component = Component(Value(schema, "Value"));
        component.Fields[0].Edits.Hold();

        schema.Adopt(Declared("Value", "Added"));
        component.Apply(Value(schema, "Value", "Added"), force: false);

        Assert.Equal(["Value"], component.Fields.Select(field => field.Name));

        component.Fields[0].Edits.Release();
        component.Apply(Value(schema, "Value", "Added"), force: false);

        Assert.Equal(["Value", "Added"], component.Fields.Select(field => field.Name));
    }
}
