using System.Text;
using EmptyEngine.Editor.Authoring;
using EmptyEngine.Editor.Catalog;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EmptyEngine.Tests;

/// <summary>型カタログ形式の読み側の固定</summary>
public sealed class TypeCatalogFormatTests
{
    private static TypeCatalogDocument Parse(string json) => TypeCatalogDocument.Parse(Encoding.UTF8.GetBytes(json), NullLogger.Instance);

    private static FieldValue Field(FieldValue data, string name) =>
        Assert.IsType<FieldValue>(data).Get(name) ?? throw new Xunit.Sdk.XunitException($"field '{name}' is missing");

    [Fact]
    public void Fields_reference_types_by_id_and_defaults_keep_their_scalar_kind()
    {
        TypeCatalogDocument document = Parse("""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "title": "EmptyEngine type catalog",
          "x-formatVersion": 5,
          "$defs": {
            "Game.Player": {
              "type": "object",
              "x-kind": "struct",
              "title": "Player",
              "x-attachable": true,
              "x-assignableTo": ["Game.Player", "EmptyEngine.ObjectModel.IAttachable"],
              "default": {
                "Speed": 4.5, "Weight": 0.5, "Hp": 3, "Mask": 7, "Alive": true, "Label": "p1",
                "Mode": 2, "Texture": "abc", "Points": [ { "X": 1.0, "Y": 2.0 } ]
              },
              "properties": {
                "Speed":   { "$ref": "#/$defs/System.Single" },
                "Weight":  { "$ref": "#/$defs/System.Double" },
                "Hp":      { "$ref": "#/$defs/System.Int32" },
                "Mask":    { "$ref": "#/$defs/System.UInt32" },
                "Alive":   { "$ref": "#/$defs/System.Boolean" },
                "Label":   { "$ref": "#/$defs/System.String" },
                "Mode":    { "$ref": "#/$defs/Game.MoveMode" },
                "Texture": { "$ref": "#/$defs/Game.TextureRef" },
                "Door":    { "$ref": "#/$defs/Game.DoorRef" },
                "Points":  { "$ref": "#/$defs/Game.Vec2%5B%5D" },
                    "Origin": { "$ref": "#/$defs/Game.Vec2" }
              }
            },
            "System.Single":  { "type": "number", "format": "float", "x-kind": "float" },
            "System.Double":  { "type": "number", "format": "double", "x-kind": "double" },
            "System.Int32":   { "type": "integer", "x-kind": "int" },
            "System.UInt32":  { "type": "integer", "minimum": 0, "x-kind": "uint" },
            "System.Boolean": { "type": "boolean", "x-kind": "bool" },
            "System.String":  { "type": "string", "x-kind": "string" },
            "Game.MoveMode":  { "type": "integer", "x-kind": "enum",
              "anyOf": [ { "const": 0, "title": "Idle" }, { "const": 2, "title": "Run" } ] },
            "Game.TextureRef": { "type": "object", "x-kind": "assetRef", "x-target": "Game.Texture" },
            "Game.DoorRef":    { "type": "object", "x-kind": "objectRef", "x-target": "Game.Door" },
            "Game.Vec2[]":     { "type": "array", "x-kind": "array", "items": { "$ref": "#/$defs/Game.Vec2" } },
            "Game.Vec2": { "type": "object", "x-kind": "struct",
              "properties": { "X": { "$ref": "#/$defs/System.Single" }, "Y": { "$ref": "#/$defs/System.Single" } } }
          }
        }
        """);

        TypeCatalogEntry entry = Assert.Single(document.Types, t => t.Attachable);
        ObjectSchema schema = entry.Schema;

        Assert.Equal("Game.Player", schema.TypeName);
        Assert.Equal("Player", schema.DisplayName);
        Assert.True(schema.IsAssignableTo("EmptyEngine.ObjectModel.IAttachable"));

        Assert.Equal(FieldKind.AssetReference, schema.Fields["Texture"].Kind);
        Assert.Equal("Game.Texture", schema.Fields["Texture"].TypeName);
        Assert.Equal(FieldKind.ObjectReference, schema.Fields["Door"].Kind);
        Assert.Equal("Game.Door", schema.Fields["Door"].TypeName);
        Assert.Equal(FieldKind.Enum, schema.Fields["Mode"].Kind);
        Assert.Equal(new[] { "Idle", "Run" }, schema.Fields["Mode"].EnumMembers.Select(m => m.Name));
        Assert.Equal(2, schema.Fields["Mode"].EnumValueOf("Run"));

        Assert.Equal("Game.Vec2", schema.Fields["Origin"].TypeName);
        Assert.Equal(new[] { "X", "Y" }, schema.Fields["Origin"].Members!.Keys.Order());
        Assert.True(schema.Fields["Points"].IsArray);
        Assert.Equal("Game.Vec2", schema.Fields["Points"].Element!.TypeName);
        Assert.Equal(new[] { "X", "Y" }, schema.Fields["Points"].Element!.Members!.Keys.Order());

        FieldValue data = entry.CreateDefaultData();
        Assert.Equal(FieldKind.Float32, schema.Fields["Speed"].Kind);
        Assert.Equal(FieldKind.Float64, schema.Fields["Weight"].Kind);
        Assert.Equal(FieldKind.Int, schema.Fields["Hp"].Kind);
        Assert.Equal(FieldKind.UInt, schema.Fields["Mask"].Kind);
        Assert.Equal(FieldKind.Bool, schema.Fields["Alive"].Kind);
        Assert.Equal(FieldKind.String, schema.Fields["Label"].Kind);
        Assert.Equal(4.5, Assert.IsType<FieldValue>(Field(data, "Speed")).Real);

        Assert.Equal("abc", Assert.IsType<FieldValue>(Field(data, "Texture")).Text);

        FieldValue element = Assert.Single(Assert.IsType<FieldValue>(Field(data, "Points")).Items);
        Assert.Equal(FieldKind.Float32, schema.Fields["Points"].Element!.Member("X").Kind);

        Assert.NotSame(entry.CreateDefaultData(), entry.CreateDefaultData());
    }

    /// <summary>同じ型の記述が表に 1 回だけであること</summary>
    /// <summary>読み手が型 id を割らないこと</summary>
    [Fact]
    public void Reader_never_parses_a_type_id_or_shortens_a_name()
    {
        TypeCatalogDocument document = Parse("""
        {
          "x-formatVersion": 4,
          "$defs": {
            "bevy_sprite::sprite::Sprite": { "x-attachable": true },
            "Game.Nested.Thing, Game": { "x-attachable": true }
          }
        }
        """);

        TypeCatalogEntry rust = document.Types[0];
        Assert.Equal("bevy_sprite::sprite::Sprite", rust.Schema.TypeName);
        Assert.Equal("bevy_sprite::sprite::Sprite", rust.Schema.DisplayName);

        TypeCatalogEntry odd = document.Types[1];
        Assert.Equal("Game.Nested.Thing, Game", odd.Schema.TypeName);
        Assert.Equal("Game.Nested.Thing, Game", odd.Schema.DisplayName);
    }

    [Fact]
    public void Source_location_is_available_only_when_path_and_positive_line_are_both_valid()
    {
        TypeCatalogDocument document = Parse("""
        {
          "$defs": {
            "Game.Valid": {
              "x-attachable": true,
              "x-sourcePath": "C:/game/Player.cs",
              "x-sourceLine": 42
            },
            "Game.NoPath": { "x-attachable": true, "x-sourceLine": 3 },
            "Game.BlankPath": { "x-attachable": true, "x-sourcePath": "  ", "x-sourceLine": 3 },
            "Game.ZeroLine": { "x-attachable": true, "x-sourcePath": "C:/game/Zero.cs", "x-sourceLine": 0 },
            "Game.TextLine": { "x-attachable": true, "x-sourcePath": "C:/game/Text.cs", "x-sourceLine": "7" }
          }
        }
        """);

        TypeCatalogEntry valid = document.Types.Single(entry => entry.Schema.TypeName == "Game.Valid");
        Assert.Equal(new TypeSourceLocation("C:/game/Player.cs", 42), valid.SourceLocation);

        Assert.All(
            document.Types.Where(entry => entry.Schema.TypeName != "Game.Valid"),
            entry => Assert.Null(entry.SourceLocation));
    }

    /// <summary><c>$ref</c> が JSON Pointer ＋ URI 断片の規則で書かれること</summary>
    [Fact]
    public void Percent_encoded_refs_resolve_back_to_the_raw_type_id()
    {
        TypeCatalogDocument document = Parse("""
        {
          "x-formatVersion": 4,
          "$defs": {
            "Game.Holder": {
              "x-attachable": true,
              "properties": {
                "Icon":  { "$ref": "#/$defs/Game.Ref%601%5B%5BGame.Icon,%20Game%5D%5D" },
                "Maybe": { "$ref": "#/$defs/core::option::Option%3Cglam::Vec2%3E" },
                "Odd":   { "$ref": "#/$defs/Game.A~1B~0C" }
              }
            },
            "Game.Ref`1[[Game.Icon, Game]]": { "type": "object", "x-kind": "assetRef", "x-target": "Game.Icon" },
            "core::option::Option<glam::Vec2>": { "type": "object", "x-kind": "union",
              "properties": { "None": true, "Some": { "$ref": "#/$defs/glam::Vec2" } } },
            "glam::Vec2": { "type": "object", "x-kind": "struct", "properties": { "x": { "$ref": "#/$defs/f32" } } },
            "Game.A/B~C": { "type": "string", "x-kind": "string" },
            "f32[]": { "x-kind": "array", "items": { "$ref": "#/$defs/f32" } },
            "f32": { "type": "number", "x-kind": "float" }
          }
        }
        """);

        ObjectSchema schema = Assert.Single(document.Types, t => t.Attachable).Schema;

        Assert.Equal(FieldKind.AssetReference, schema.Fields["Icon"].Kind);
        Assert.Equal("Game.Icon", schema.Fields["Icon"].TypeName);
        Assert.Equal(FieldKind.Union, schema.Fields["Maybe"].Kind);
        Assert.Equal(new[] { "None", "Some" }, schema.Fields["Maybe"].Members!.Keys);
        Assert.Equal("Game.A/B~C", schema.Fields["Odd"].TypeName);
    }

    /// <summary>タグ付きバリアント（<c>union</c>）の読み取り</summary>
    [Fact]
    public void Union_variants_carry_the_type_of_their_payload()
    {
        TypeCatalogDocument document = Parse("""
        {
          "x-formatVersion": 5,
          "$defs": {
            "Game.Brush": {
              "x-attachable": true,
              "default": {
                "Tint": { "Srgba": { "red": 1.0 } }, "Size": { "None": null }, "Odd": { "Rect": [0, 1] }
              },
              "properties": {
                "Tint": { "$ref": "#/$defs/Game.Color" },
                "Size": { "$ref": "#/$defs/Game.MaybeVec2" },
                "Odd":  { "$ref": "#/$defs/Game.Shape" }
              }
            },
            "Game.Color": { "type": "object", "x-kind": "union", "minProperties": 1, "maxProperties": 1,
              "properties": { "Srgba": { "$ref": "#/$defs/Game.Srgba" }, "Hsla": { "$ref": "#/$defs/Game.Srgba" } } },
            "Game.Srgba": { "type": "object", "x-kind": "struct", "properties": { "red": { "$ref": "#/$defs/f32" } } },
            "Game.MaybeVec2": { "type": "object", "x-kind": "union",
              "properties": { "None": true, "Some": { "$ref": "#/$defs/Game.Vec2" } } },
            "Game.Vec2": { "type": "object", "x-kind": "struct", "properties": { "x": { "$ref": "#/$defs/f32" } } },
            "Game.Shape": { "type": "object", "x-kind": "union", "properties": { "Rect": { "$ref": "#/$defs/f32[]" } } },
            "f32[]": { "x-kind": "array", "items": { "$ref": "#/$defs/f32" } },
            "f32": { "type": "number", "x-kind": "float" }
          }
        }
        """);

        ObjectSchema schema = Assert.Single(document.Types, t => t.Attachable).Schema;

        Assert.Equal(FieldKind.Union, schema.Fields["Tint"].Kind);
        Assert.Equal(FieldKind.Map, schema.Fields["Tint"].Members!["Srgba"].Kind);

        Assert.Equal(new[] { "Srgba", "Hsla" }, schema.Fields["Tint"].Members!.Keys);
        Assert.Equal(new[] { "red" }, schema.Fields["Tint"].Members!["Srgba"].Members!.Keys);
        Assert.Equal(new[] { "None", "Some" }, schema.Fields["Size"].Members!.Keys);
        Assert.Equal(FieldKind.Array, schema.Fields["Odd"].Member("Rect").Kind);

        FieldValue data = Assert.Single(document.Types, t => t.Attachable).CreateDefaultData();
        FieldValue red = Field(Field(Field(data, "Tint"), "Srgba"), "red");
        Assert.Equal(1, red.Real);

        Assert.True(Field(Field(data, "Size"), "None").IsNull);
        Assert.Equal(2, Assert.IsType<FieldValue>(Field(Field(data, "Odd"), "Rect")).Items.Count);
    }

    /// <summary>引けないフィールドはそのフィールドだけを落とす</summary>
    [Fact]
    public void Missing_field_schemas_drop_only_that_field()
    {
        TypeCatalogDocument document = Parse("""
        {
          "$defs": {
            "Game.Bag": {
              "x-kind": "struct", "x-attachable": true,
              "properties": {
                "Kept":    { "$ref": "#/$defs/System.Int32" },
                "Missing": { "$ref": "#/$defs/Absent" }
              }
            },
            "System.Int32": { "x-kind": "int" }
          }
        }
        """);

        ObjectSchema schema = Assert.Single(document.Types, t => t.Attachable).Schema;

        Assert.Equal(new[] { "Kept" }, schema.Fields.Keys);
    }
}
