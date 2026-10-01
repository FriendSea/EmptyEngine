using EmptyEngine.Editor;
using EmptyEngine.Editor.Authoring;
using EmptyEngine.Editor.ViewModels.Inspector;
using EmptyEngine.Editor.ViewModels.Utils;
using Xunit;

namespace EmptyEngine.Tests;

/// <summary>参照フィールドがピッカーの葉として描かれることの検証</summary>
public sealed class ReferenceFieldTests
{
    private const string TypeName = "Probe.ReferenceHolder";

    private static readonly FieldTypeInfo Target = new("Probe.Sprite", FieldKind.ObjectReference);
    private static readonly FieldTypeInfo Icon = new("Probe.Texture", FieldKind.AssetReference);

    private static readonly ObjectSchema Schema = new()
    {
        TypeName = TypeName,
        DisplayName = "ReferenceHolder",
        Fields = new Dictionary<string, FieldTypeInfo>
        {
            ["Target"] = Target,
            ["Icon"] = Icon,
            ["Targets"] = new("Probe.Sprite[]", FieldKind.Array)
            {
                Element = Target },
        },
    };

    private static AuthoringObject Probe(string targetId, string assetKey, params string[] elements)
    {
        var data = new FieldValue();
        data.Add("Target", Reference(targetId));
        data.Add("Icon", Reference(assetKey));

        var array = new FieldValue();
        foreach (string element in elements)
            array.Items.Add(Reference(element));
        data.Add("Targets", array);

        return new AuthoringObject(Schema, data);
    }

    private static FieldValue Reference(string key) => new() { Text = key };

    private static FieldViewModel FieldFor(
        AuthoringObject probe,
        string name,
        Func<string, string?>? objectDisplayResolver = null)
    {
        return new FieldViewModel(name, probe.Schema.Fields[name], probe.Data.Get(name),
            objectDisplayResolver: objectDisplayResolver);
    }

    [Fact]
    public void Object_reference_shows_the_resolved_object_name()
    {
        FieldViewModel field = FieldFor(Probe("obj-1", "asset-1"), "Target", id => id == "obj-1" ? "Player" : null);

        Assert.Equal("Player", field.ObjectReferenceText);
    }

    [Fact]
    public void Object_reference_picker_tracks_assignment_and_clearing()
    {
        FieldViewModel field = FieldFor(Probe(string.Empty, "asset-1"), "Target");
        Assert.True(field.IsObjectReference);
        Assert.False(field.IsComposite);
        Assert.Equal(EditorFieldKinds.ObjectReference, EditorFieldKinds.Of(field));
        Assert.Equal("(none)", field.ObjectReferenceText);
        Assert.Null(field.ObjectReferenceId);

        field.AssignObjectReference("obj-2");
        Assert.Equal("obj-2", field.ObjectReferenceId);
        Assert.Equal("obj-2", field.ObjectReferenceText);

        field.ClearObjectReference();
        Assert.Null(field.ObjectReferenceId);
        Assert.Equal("(none)", field.ObjectReferenceText);
    }

    [Fact]
    public void Asset_reference_is_a_picker_leaf_too()
    {
        FieldViewModel field = FieldFor(Probe("obj-1", "asset-1"), "Icon");

        Assert.True(field.IsAssetReference);
        Assert.False(field.IsComposite);
        Assert.Equal("asset-1", field.AssetReferenceText);
    }

}
