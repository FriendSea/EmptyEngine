using System.Text;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Authoring;
using EmptyEngine.Editor.Catalog;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EmptyEngine.Tests;

/// <summary>読み直したカタログで増えた欄への既定値の補完の検証</summary>
public sealed class CatalogDefaultFillTests
{
    private const string TypeName = "Probe.Grown";

    private const string Before =
        """
        {
          "$defs": {
            "Probe.Grown": {
              "type": "object", "x-kind": "struct", "x-attachable": true,
              "properties": { "Value": { "type": "number", "x-kind": "float" } },
              "default": { "Value": 5 }
            }
          }
        }
        """;

    private const string After =
        """
        {
          "$defs": {
            "Probe.Grown": {
              "type": "object", "x-kind": "struct", "x-attachable": true,
              "properties": {
                "Value": { "type": "number", "x-kind": "float" },
                "Added": { "type": "number", "x-kind": "float" },
                "Points": { "$ref": "#/$defs/Probe.Point[]" }
              },
              "default": { "Value": 5, "Added": 7, "Points": [] }
            },
            "Probe.Point[]": { "x-kind": "array", "items": { "$ref": "#/$defs/Probe.Point" } },
            "Probe.Point": {
              "type": "object", "x-kind": "struct",
              "properties": {
                "X": { "type": "number", "x-kind": "float" },
                "Y": { "type": "number", "x-kind": "float" }
              },
              "default": { "X": 0, "Y": 3 }
            }
          }
        }
        """;

    private static ObjectSchema Parse(string json, ObjectSchema? kept = null)
    {
        ObjectSchema? declared = null;
        TypeCatalogDocument document = TypeCatalogDocument.Parse(
            Encoding.UTF8.GetBytes(json), NullLogger.Instance,
            declaration =>
            {
                if (kept is null || declaration.TypeName != TypeName) return declaration;
                declared = declaration;
                return kept;
            });
        kept?.Adopt(declared!);
        return Assert.Single(document.Types, t => t.Schema.TypeName == TypeName).Schema;
    }

    [Fact]
    public void Only_the_added_field_takes_the_catalog_default()
    {
        ObjectSchema schema = Parse(Before);
        var data = new FieldValue();
        data.Add("Value", new FieldValue { Real = 1 });
        var component = new AuthoringObject(schema, data);
        Assert.Null(component.Data.Get("Added"));

        Parse(After, schema);

        Assert.Equal(1d, component.Data.Get("Value")!.Real);
        Assert.Equal(7d, component.Data.Get("Added")!.Real);
    }

    [Fact]
    public void An_array_element_takes_its_element_type_default()
    {
        ObjectSchema schema = Parse(After);
        var point = new FieldValue();
        point.Add("X", new FieldValue { Real = 1 });
        var points = new FieldValue();
        points.Items.Add(point);
        var data = new FieldValue();
        data.Add("Points", points);

        FieldValue element = new AuthoringObject(schema, data).Data.Get("Points")!.Items[0];

        Assert.Equal(1d, element.Get("X")!.Real);
        Assert.Equal(3d, element.Get("Y")!.Real);
    }

    [Fact]
    public void A_type_without_a_default_keeps_the_key_absent()
    {
        ObjectSchema schema = TestSchemas.Object(TypeName, ("Value", TestSchemas.Scalar("System.Single")));

        Assert.Null(new AuthoringObject(schema, new FieldValue()).Data.Get("Value"));
    }
}
