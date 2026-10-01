using EmptyEngine.Modules.Testing;
using EmptyEngine.Core;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Authoring;
using EmptyEngine.Editor.ViewModels.Inspector;
using Xunit;

namespace EmptyEngine.Serialization.Tests;

/// <summary>インスペクタが型情報を CLR の <see cref="Type"/> に頼らないことの検証</summary>
public sealed class InspectorTypeInfoTests
{
    private static AuthoringObject Create<T>() where T : new()
    {
        CatalogStub.Register<T>();
        return CatalogStub.Schemas.CreateDefault(typeof(T).FullName!)
               ?? throw new InvalidOperationException("catalog stub did not produce a default");
    }

    private static FieldViewModel Field(AuthoringObject authoring, string name) =>
        new(name, authoring.Schema.Fields[name], authoring.Data.Get(name));

    [Fact]
    public void Enum_picker_preserves_known_edits_and_unknown_stored_values()
    {
        FieldViewModel mode = Field(Create<TestModeHolder>(), nameof(TestModeHolder.Mode));
        Assert.True(mode.IsEnum);
        Assert.False(mode.IsNumeric);
        Assert.Equal(new[] { "Idle", "Loop", "PingPong" }, mode.EnumOptions);
        Assert.Equal("Loop", mode.EnumValue);

        mode.EnumValue = "PingPong";
        Assert.Equal(9, mode.Capture().Integer);
        mode.Apply(new FieldValue { Integer = 42 });
        Assert.Equal("42", mode.EnumValue);
        Assert.Equal(42, mode.Capture().Integer);
    }

    [Fact]
    public void Typed_object_reference_carries_its_constraint_to_the_picker()
    {
        FieldViewModel target = Field(Create<TestModeHolder>(), nameof(TestModeHolder.Target));

        Assert.True(target.IsObjectReference);
        Assert.Equal(typeof(TestSprite).FullName, target.ObjectTypeConstraint);
        Assert.Equal(nameof(TestSprite), target.ObjectTypeConstraintDisplay);
    }

    [Fact]
    public void Constraint_survives_when_the_type_cannot_be_loaded_by_the_editor()
    {
        const string gameOnlyType = "Game.NotLoadedInTheEditor";
        var field = new FieldViewModel(
            "reference", new FieldTypeInfo(gameOnlyType, FieldKind.AssetReference));

        Assert.Null(Type.GetType(gameOnlyType));
        Assert.True(field.IsAssetReference);
        Assert.Equal(gameOnlyType, field.AssetTypeConstraint);
        Assert.Equal("NotLoadedInTheEditor", field.AssetTypeConstraintDisplay);
    }

    [Fact]
    public void Reference_arrays_are_editable_and_carry_the_element_constraint()
    {
        AuthoringObject authoring = Create<TestArrayHolder>();
        FieldViewModel targets = Field(authoring, nameof(TestArrayHolder.Targets));

        Assert.True(targets.IsArray);
        Assert.Empty(targets.Children);

        targets.AddArrayElement();
        targets.AddArrayElement();
        Assert.Equal(2, targets.Children.Count);

        FieldViewModel slot = targets.Children[0];
        Assert.True(slot.IsObjectReference);
        Assert.Equal(typeof(TestSprite).FullName, slot.ObjectTypeConstraint);

        slot.AssignObjectReference("target-id");

        var array = targets.Capture();
        Assert.Equal(2, array.Items.Count);
        Assert.Equal("target-id", AuthoringTestHelpers.TargetIdOf(array.Items[0]));

        targets.RemoveArrayElement(targets.Children[1]);
        Assert.Single(targets.Capture().Items);
    }

    [Fact]
    public void Null_arrays_are_editable_as_empty_arrays()
    {
        Create<TestArrayHolder>();
        var data = new FieldValue();
        data.Add(nameof(TestArrayHolder.Targets), FieldValue.Nil());
        data.Add(nameof(TestArrayHolder.Stages), FieldValue.Nil());
        var authoring = new AuthoringObject(
            CatalogStub.Schemas.Get(typeof(TestArrayHolder).FullName!), data);

        FieldViewModel targets = Field(authoring, nameof(TestArrayHolder.Targets));
        Assert.True(targets.IsArray);
        Assert.Empty(targets.Children);

        targets.AddArrayElement();
        targets.Children[0].AssignObjectReference("target-id");

        var stored = targets.Capture();
        Assert.Equal("target-id", AuthoringTestHelpers.TargetIdOf(Assert.Single(stored.Items)));

        FieldViewModel stages = Field(authoring, nameof(TestArrayHolder.Stages));
        stages.AddArrayElement();
        Assert.True(Assert.Single(stages.Children).IsComposite);
    }

    [Fact]
    public void Struct_element_arrays_expand_into_editable_members()
    {
        AuthoringObject authoring = Create<TestArrayHolder>();
        var component = new AuthoringObjectViewModel(authoring.TypeName, authoring.TypeName, authoring, _ => { });
        FieldViewModel stages = component.Fields.Single(f => f.Name == nameof(TestArrayHolder.Stages));

        Assert.True(stages.IsArray);
        stages.AddArrayElement();

        FieldViewModel element = Assert.Single(stages.Children);
        Assert.True(element.IsComposite);
        Assert.Equal(
            new[] { nameof(TestStage.Scene), nameof(TestStage.Title), nameof(TestStage.Par) },
            element.Children.Select(c => c.Name));

        FieldViewModel scene = element.Children[0];
        Assert.True(scene.IsAssetReference);
        Assert.Equal(typeof(TestAsset).FullName, scene.AssetTypeConstraint);

        scene.AssignAssetReference(new AssetKey("scene-key"));
        element.Children[1].TextValue = "Stage 1";

        AuthoringObject captured = component.Capture();
        Assert.Equal(["scene-key"], captured.EnumerateAssetReferences().Select(r => r.Value));
        var array = captured.Data.Get(nameof(TestArrayHolder.Stages))!;
        var map = Assert.IsType<FieldValue>(Assert.Single(array.Items));
        Assert.Equal("scene-key", map.Get(nameof(TestStage.Scene))!.ReferenceKey);
        Assert.Equal("Stage 1", Assert.IsType<FieldValue>(map.Get(nameof(TestStage.Title))).Text);
    }

    [Fact]
    public void Nested_struct_stays_one_composite_row_instead_of_dotted_fields()
    {
        AuthoringObject authoring = Create<TestNestedHolder>();

        var names = authoring.Schema.Fields.Keys.ToList();
        Assert.Equal(new[] { nameof(TestNestedHolder.Offset), nameof(TestNestedHolder.Tint) }, names);

        FieldViewModel offset = Field(authoring, nameof(TestNestedHolder.Offset));
        Assert.True(offset.IsComposite);
        Assert.Equal(new[] { "X", "Y", "Z" }, offset.Children.Select(c => c.Name));

        offset.Children[1].NumericValue = 3;

        var map = offset.Capture();
        Assert.Equal(3, Assert.IsType<FieldValue>(map.Get("Y")).Real);
    }

    [Fact]
    public void Rgba_struct_becomes_a_color_picker_and_writes_back_to_the_tree()
    {
        AuthoringObject authoring = Create<TestNestedHolder>();
        FieldViewModel tint = Field(authoring, nameof(TestNestedHolder.Tint));

        Assert.True(tint.IsColor);
        Assert.False(tint.IsComposite);

        tint.ColorValue = EditorColor.FromArgb(255, 255, 0, 0);

        var map = tint.Capture();
        Assert.Equal(1d, Assert.IsType<FieldValue>(map.Get("R")).Real);
        Assert.Equal(0d, Assert.IsType<FieldValue>(map.Get("G")).Real);
        Assert.Equal(1d, Assert.IsType<FieldValue>(map.Get("A")).Real);
    }
}
