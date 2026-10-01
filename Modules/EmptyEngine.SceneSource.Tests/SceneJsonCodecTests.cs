using EmptyEngine.Modules.Testing;
using System.Numerics;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Authoring;
using EmptyEngine.ObjectModel;
using EmptyEngine.SceneSource.Editor;
using Xunit;

namespace EmptyEngine.SceneSource.Tests;

public sealed class SceneJsonCodecTests
{
    private const string SceneWithUnknownType = """
    {
      "Id": "obj-1",
      "Name": "O",
      "Components": [
        { "TypeName": "EmptyEngine.Modules.Testing.TestTransform", "Data": { "X": 1, "Y": 2, "Z": 3 } },
        { "TypeName": "Some.Deleted.Component", "Data": { "Foo": 42 } }
      ]
    }
    """;

    [Fact]
    public void Missing_component_schema_is_reported()
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => SceneJsonCodec.FromJson(SceneWithUnknownType, CatalogStub.Schemas));
        Assert.Contains("Some.Deleted.Component", error.Message);
    }

    [Fact]
    public void Unknown_fields_are_dropped()
    {
        string json = SceneWithUnknownType.Replace("Some.Deleted.Component", "EmptyEngine.Modules.Testing.TestTransform");
        HierarchyNode root = SceneJsonCodec.FromJson(json, CatalogStub.Schemas);
        AuthoringObject stale = root.Components[1];
        Assert.Null(stale.Data.Get("Foo"));
    }

    [Fact]
    public void Missing_component_fields_are_filled_from_catalog_defaults()
    {
        CatalogStub.Register<TestEmission>();

        HierarchyNode root = SceneJsonCodec.FromJson("""
            {
              "Id": "root",
              "Name": "Root",
              "Components": [
                {
                  "TypeName": "EmptyEngine.Modules.Testing.TestEmission",
                  "Data": { "Count": 7 }
                }
              ],
              "Children": []
            }
            """, CatalogStub.Schemas);

        var component = Assert.IsType<AuthoringObject>(Assert.Single(root.Components));
        var data = Assert.IsType<FieldValue>(component.Data);
        var emit = Assert.IsType<FieldValue>(data.Get("Emit"));

        Assert.True(emit.Bool);
        Assert.Equal(7, AuthoringTestHelpers.GetInt(component, "Count"));
    }

    [Fact]
    public void Saved_component_fields_override_catalog_defaults()
    {
        CatalogStub.Register<TestEmission>();

        HierarchyNode root = SceneJsonCodec.FromJson("""
            {
              "Id": "root",
              "Name": "Root",
              "Components": [
                {
                  "TypeName": "EmptyEngine.Modules.Testing.TestEmission",
                  "Data": { "Emit": false }
                }
              ],
              "Children": []
            }
            """, CatalogStub.Schemas);

        var component = Assert.IsType<AuthoringObject>(Assert.Single(root.Components));
        var data = Assert.IsType<FieldValue>(component.Data);
        var emit = Assert.IsType<FieldValue>(data.Get("Emit"));

        Assert.False(emit.Bool);
        Assert.Equal(1, AuthoringTestHelpers.GetInt(component, "Count"));
    }

    [Fact]
    public void Numerics_fields_round_trip_and_computed_property_is_excluded()
    {
        var matrix = Matrix4x4.CreateScale(2f, 3f, 1f) * Matrix4x4.CreateTranslation(5f, 6f, 7f);
        var component = new TestNumerics { Matrix = matrix };
        var root = new HierarchyNode("obj-1", "O", Array.Empty<HierarchyNode>(), AuthoringTestHelpers.Components(component));

        string json = SceneJsonCodec.ToJson(root);

        Assert.DoesNotContain("\"Matrix\": {}", json);
        Assert.Contains("\"M41\": 5", json);
        Assert.DoesNotContain("\"Position\"", json);

        HierarchyNode restored = SceneJsonCodec.FromJson(json, CatalogStub.Schemas);
        var restoredComponent = Assert.IsType<AuthoringObject>(restored.Components[0]);
        Assert.Equal("EmptyEngine.Modules.Testing.TestNumerics", restoredComponent.TypeName);
        string reJson = SceneJsonCodec.ToJson(restored);
        Assert.Contains("\"M41\": 5", reJson);
        Assert.DoesNotContain("\"Position\"", reJson);
    }

    [Fact]
    public void Reference_array_round_trips_through_the_source_scene()
    {
        var component = new MultiReferenceProbe
        {
            Targets = [ObjectReference.FromId("a"), ObjectReference.FromId("b")],
        };
        var root = new HierarchyNode("obj-1", "O", Array.Empty<HierarchyNode>(), AuthoringTestHelpers.Components(component));

        string json = SceneJsonCodec.ToJson(root);

        Assert.Contains("\"Targets\"", json);

        HierarchyNode restored = SceneJsonCodec.FromJson(json, CatalogStub.Schemas);
        var restoredComponent = Assert.IsType<AuthoringObject>(restored.Components[0]);
        Assert.Equal("EmptyEngine.Modules.Testing.MultiReferenceProbe", restoredComponent.TypeName);
        Assert.Equal(
            new[] { "a", "b" },
            AuthoringTestHelpers.GetTargetIds(restoredComponent, nameof(MultiReferenceProbe.Targets)));
        Assert.Equal(json, SceneJsonCodec.ToJson(restored));
    }

}
