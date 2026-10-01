# EmptyEngine.Templates

EmptyEngine is an editor-only game engine with no runtime implementation of its own.

This package provides `dotnet new` templates for .NET game projects that reference the EmptyEngine packages. A generated project includes the runtime, the editor settings (`Editor/Editor.targets`), and the project manifest (`*.emptyengine`).

## Usage

```
dotnet new install EmptyEngine.Templates
dotnet new emptyengine
```

The editor settings automatically reference the editor-side packages of the modules your game references.
