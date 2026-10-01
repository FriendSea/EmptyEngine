using System.Text;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Authoring;
using EmptyEngine.Editor.Catalog;
using EmptyEngine.Editor.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EmptyEngine.Tests;

/// <summary>既定値を焼けなかった型の扱いの検証</summary>
public sealed class CatalogDefaultFallbackTests
{
    private const string WithDefault = "Probe.Filled";
    private const string WithoutDefault = "Probe.Bare";

    private static byte[] Catalog() => Encoding.UTF8.GetBytes(
        """
        {
          "x-formatVersion": 5,
          "x-defaultEncoding": "ron",
          "$defs": {
            "Probe.Filled": {
              "type": "object", "x-kind": "struct", "title": "Filled", "x-attachable": true,
              "properties": { "Value": { "type": "number", "x-kind": "float" } },
              "default": { "Value": 5 }
            },
            "Probe.Bare": {
              "type": "object", "x-kind": "struct", "title": "Bare", "x-attachable": true,
              "x-default": "(Value:99.0)",
              "properties": { "Value": { "type": "number", "x-kind": "float" } }
            }
          }
        }
        """);

    private static TypeCatalogEntry Entry(string typeName) =>
        Assert.Single(TypeCatalogDocument.Parse(Catalog(), NullLogger.Instance).Types, t => t.Schema.TypeName == typeName);

    [Fact]
    public void Catalog_defaults_fill_known_values_and_ignore_retired_encoded_defaults()
    {
        TypeCatalogDocument document = TypeCatalogDocument.Parse(Catalog(), NullLogger.Instance);
        TypeCatalogEntry bare = Assert.Single(document.Types, t => t.Schema.TypeName == WithoutDefault);
        Assert.False(bare.HasDefault);
        Assert.Equal(["Value"], bare.Schema.Fields.Keys);
        Assert.Empty(bare.CreateDefaultData().Entries);

        TypeCatalogEntry filled = Assert.Single(document.Types, t => t.Schema.TypeName == WithDefault);
        Assert.True(filled.HasDefault);
        Assert.Equal(5d, filled.CreateDefaultData().Get("Value")!.Real);
    }

    /// <remarks>ワイヤ上の nil は「実体なし」で、送るとコンポーネントごと捨てられる</remarks>
    [Fact]
    public void A_component_handed_nil_holds_an_empty_map_instead()
    {
        var component = new AuthoringObject(Entry(WithoutDefault).Schema, FieldValue.Nil());

        Assert.Empty(Assert.IsType<FieldValue>(component.Data).Entries);
        Assert.Empty(Assert.IsType<FieldValue>(component.Clone().Data).Entries);
    }

    [Fact]
    public void Only_the_type_without_a_default_is_marked_in_the_inspector()
    {
        using var catalog = new TemporaryCatalog(Catalog());

        EditorViewModel vm = Loaded(catalog.Source, WithoutDefault, WithDefault);

        vm.SelectedNode = vm.RootNodes[0].Children[0];
        Assert.True(vm.SelectedComponents[0].DefaultsMissing);
        Assert.False(vm.SelectedComponents[1].DefaultsMissing);
    }

    [Fact]
    public void A_type_the_catalog_does_not_know_is_not_marked()
    {
        using var catalog = new TemporaryCatalog(Catalog());

        EditorViewModel vm = Loaded(catalog.Source, "Probe.Unlisted");

        vm.SelectedNode = vm.RootNodes[0].Children[0];
        Assert.False(vm.SelectedComponents[0].DefaultsMissing);
    }

    private static EditorViewModel Loaded(TypeCatalog catalog, params string[] typeNames)
    {
        var components = typeNames.Select(t => new AuthoringObject(catalog.Find(t) ?? TestSchemas.Object(t), new FieldValue())).ToArray();
        var scene = new HierarchyNode(
            "root", "Root", [new HierarchyNode("obj", "Object", null, components)], sceneId: "scene-1");

        var vm = EditorFixture.NewEditor(catalog: catalog);
        vm.UpdateHierarchy([scene]);
        return vm;
    }

    /// <summary>一時フォルダへ置いて消す、この検証だけのカタログ</summary>
    private sealed class TemporaryCatalog : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"TypeCatalog.{Guid.NewGuid():N}.json");

        public TemporaryCatalog(byte[] content)
        {
            File.WriteAllBytes(_path, content);
            Source = new TypeCatalog(_path, NullLogger<TypeCatalog>.Instance);
        }

        public TypeCatalog Source { get; }

        public void Dispose() => File.Delete(_path);
    }
}
