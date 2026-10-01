# Type catalog

English | [日本語](type-catalog_ja.md)

This is the spec for the file the editor uses to learn about the types of your runtime's components and assets.

The editor never loads runtime code. The fields shown in the inspector, the candidates in "Add Component", and the default values of newly added components all come from the type catalog. Generate the type catalog from your runtime's types as part of the runtime build.

If you represent components and assets with `IAttachable` and `[Asset]` from [EmptyEngine.ObjectModel](../Modules/EmptyEngine.ObjectModel), referencing [EmptyEngine.TypeCatalog](../Modules/EmptyEngine.TypeCatalog) generates the catalog on every build, so you don't need to read this document. Otherwise, generate it yourself. [BevyRuntimeSample](../samples/BevyRuntimeSample/build.rs)'s `build.rs`, which generates it from Rust types, is a useful reference.

## Overview

The format is JSON Schema (draft 2020-12). Each type is described by one entry in `$defs`, and component and asset fields point to other type descriptions with `$ref`.

### Location

Place it in the project's `.artifacts/` directory, named `TypeCatalog.json`, as UTF-8 JSON. The editor walks up from the location of its own executable and reads `TypeCatalog.json` in the first `.artifacts/` it finds.

### Rewriting

You may rewrite the whole file on every runtime build. The editor detects changes to the file while running and reloads it, and open inspectors follow the new descriptions. If the editor reads the file mid-write and it is corrupt, or the file briefly disappears, the editor keeps using the previous contents.

The editor starts without a catalog, but you cannot add components or edit in the inspector.

## TypeCatalog.json

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "title": "EmptyEngine type catalog",
  "x-formatVersion": 7,
  "$defs": {
    "MyGame.Player": {
      "x-kind": "struct",
      "title": "Player",
      "x-attachable": true,
      "default": { "Speed": 5, "Mode": 0, "Tint": { "R": 1, "G": 1, "B": 1, "A": 1 } },
      "properties": {
        "Speed": { "$ref": "#/$defs/System.Single" },
        "Mode":  { "$ref": "#/$defs/MyGame.MoveMode" },
        "Tint":  { "$ref": "#/$defs/MyGame.Color" }
      }
    },
    "System.Single": { "x-kind": "float" },
    "MyGame.MoveMode": {
      "x-kind": "enum",
      "anyOf": [ { "const": 0, "title": "Idle" }, { "const": 1, "title": "Run" } ]
    },
    "MyGame.Color": {
      "x-kind": "struct",
      "x-color": ["R", "G", "B", "A"],
      "properties": {
        "R": { "$ref": "#/$defs/System.Single" },
        "G": { "$ref": "#/$defs/System.Single" },
        "B": { "$ref": "#/$defs/System.Single" },
        "A": { "$ref": "#/$defs/System.Single" }
      }
    }
  }
}
```

### Top level

| Key | Required | Contents |
|---|---|---|
| `$schema` | ✔ | `https://json-schema.org/draft/2020-12/schema` |
| `x-formatVersion` | ✔ | Version of this spec. Currently `7` |
| `$defs` | ✔ | Table of type descriptions. Keys are type ids |

The order of `$defs` becomes the order of the "Add Component" menu.

### Type ids

A type id is a string that identifies a type. Make it **the identifier used to denote that type in your scene and asset data**. The editor looks up `$defs` by the type names in the scenes it receives from the runtime.

The editor does not interpret type ids. It does not derive display names from them either, so specify display names with `title`.

For example, the samples in this repository use the following strings as type ids.

| Sample | Type id | Example |
|---|---|---|
| DotnetRuntimeSample | Fully qualified type name | `MyGame.Player` |
| BevyRuntimeSample | Rust type path | `bevy_transform::components::transform::Transform` |

Field types are referenced with `{"$ref": "#/$defs/<type id>"}`. Write the type id as is for `$defs` keys, and escape `$ref` according to JSON Schema rules.

### Type descriptions

| Key | Contents |
|---|---|
| `x-kind` | Kind (see the table below). Defaults to `struct` |
| `title` | Display name shown in the inspector and "Add Component". Defaults to the type id |
| `x-attachable` | If `true`, the type appears as an "Add Component" candidate. Defaults to `false` |
| `x-assignableTo` | Array of type ids this type can be assigned to (itself, base types, interfaces). Used to filter the asset picker |
| `default` | Default value (see [Default values](#default-values)) |
| `properties` | Table of a `struct`'s fields or a `union`'s variants. The value for each key is a single `$ref` |
| `anyOf` | Choices of an `enum`. An array of `{"const": number, "title": name}`, in dropdown order |
| `items` | `$ref` of an `array`'s element type |
| `x-target` | Type id a reference can point to (see [References](#references)) |
| `x-color` | For a `struct` representing a color, an array of its RGBA field names in R, G, B, A order |
| `x-sourcePath`, `x-sourceLine` | Absolute path and line number (1-based) of the source file declaring the type. Used to open the source from the inspector |

The editor does not read any other JSON Schema keywords (`type`, `format`, etc.). Feel free to add them if you want to use them for JSON validation.

### Kinds

| `x-kind` | Value | Inspector |
|---|---|---|
| `float`, `double` | Number | Number field |
| `int`, `uint` | Integer | Number field |
| `bool` | Boolean | Checkbox |
| `string` | String | Text field |
| `struct` | Map keyed by field name | One field per member |
| `array` | Array | List with add/remove |
| `enum` | Number | Dropdown |
| `union` | Map with a single tag | Fields of the selected variant |
| `assetRef` | Asset key | Asset picker |
| `objectRef` | Id of an object in the scene | Object picker |
| `binary` | Byte sequence (see below) | Read-only row showing only the size |

Values inside the editor carry a scalar kind determined by `x-kind` (32-/64-bit floating point, signed/unsigned). When an editor extension serializes scenes or assets, it can use this kind to choose how to write each value.

`binary` is for fields holding large byte sequences, such as texture pixels. The editor handles them separately from other fields, and they cannot be edited in the inspector. How they are persisted is up to the editor extension.

### union

A type whose value is one of several variants, each of which may carry a value (such as a Rust enum). The value is a map with a single tag, `{"<tag>": <value>}`; a variant without a value is `{"<tag>": null}`.

```json
"core::option::Option<glam::Vec2>": {
  "x-kind": "union",
  "properties": {
    "None": true,
    "Some": { "$ref": "#/$defs/glam::Vec2" }
  }
}
```

`properties` is a table from tag name to value type. For variants whose value cannot be expressed as a single type id (those with no value, or with multiple values), use `true`.

In the inspector you can edit the contents of the selected variant. You cannot switch variants.

### References

The value of `assetRef` and `objectRef` is a single key string. Unset is `null` or an empty string.

In `x-target`, write the type id of the reference type the property points to. The asset picker only offers assets whose `x-assignableTo` contains that type id (assets of types without `x-assignableTo` remain as candidates). If `x-target` is omitted, the field can point to any asset, including scenes.

For fields that point to a scene, set `x-target` to the reserved value `@scene`. The picker then offers only scenes. Values starting with `@` are reserved and cannot be used as type ids.

### Colors

A `struct` with `x-color` becomes a color picker in the inspector. All the fields you specify must be in `properties` and be floating-point numbers from 0 to 1.

### Which types to include

Include the types that are serialized. You don't need to include every type in your runtime. For example, EmptyEngine.TypeCatalog starts from types implementing `IAttachable` and types marked with `[Asset]`, and recursively follows the types of their serialized members (it does not follow the targets of references).

You only need to list the fields you want in `properties`. Fields not in `properties` are not shown in the inspector and are not saved to scene files or elsewhere.

Conversely, you can also list fields in `properties` that the runtime does not hold in the scene data. BevyRuntimeSample's `Sprite.image` uses this to make an asset reference that is carried outside the scene editable.

## Default values

In `default`, write a single value of the type, directly as a JSON value. Scalar kinds are determined by the `x-kind` of each field's type.

```json
"default": { "Volume": 1, "Clip": null, "Offset": { "X": 0, "Y": 0, "Z": 0 } }
```

Whether a default value exists changes what the editor sends to the runtime when a component is added.

| Default value | What the editor sends | Who fills in the defaults |
|---|---|---|
| None | A value with no fields (an empty map) | The runtime |
| Present | A copy of the default value | The type catalog |

If your runtime can accept a value with some fields missing and fill in the rest with defaults, you may omit default values. The inspector shows zeros until a value comes back from the runtime. If your runtime can't accept such values, write default values.

The most reliable way to produce default values is to create a value of the type during the build, using the same code as the runtime, and serialize it. Examples of generation in this repository:

- EmptyEngine.TypeCatalog: runs the constructor once and serializes the result the same way the runtime does. It passes `null` for reference-type constructor parameters, so defaults that depend on injected dependencies are not reflected
- BevyRuntimeSample: writes the `Default::default()` value to JSON while walking the type catalog descriptions

Omit `default` for types containing values that JSON cannot represent correctly, such as maps with non-string keys or 128-bit integers.

## Compatibility

- The editor ignores unknown keys
- Fields pointing to types not in the type catalog, or to types with an unknown `x-kind`, are treated as having no type information and are displayed based only on the shape of the value
- The order of `properties` is not guaranteed. The editor lays out fields in document order, but don't give the order any meaning

## Bonus: validating JSON descriptions

Since the type catalog is JSON Schema, when you write files that describe values of your types in JSON, you can point to types in `$defs` with `$ref` to get validation and completion in your text editor.

For example, the `.asset` files of [EmptyEngine.SceneSource.Editor](../Modules/EmptyEngine.SceneSource.Editor) (JSON with the type in `TypeName` and the value in `Data`) take advantage of this. EmptyEngine.TypeCatalog writes a schema `asset.schema.json` next to `TypeCatalog.json` that selects the type of `Data` (`TypeCatalog.json#/$defs/<type id>`) based on the value of `TypeName`, and the editor rewrites the `$schema` of each `.asset` to point at this file every time it saves. In VS Code, it takes effect once you make `*.asset` open as JSON via `files.associations` in `.vscode/settings.json`.
