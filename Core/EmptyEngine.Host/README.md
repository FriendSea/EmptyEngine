# EmptyEngine.Host

EmptyEngine is an editor-only game engine with no runtime implementation of its own.

This package is the CLI that opens an EmptyEngine project (`*.emptyengine`). It launches the runtime and the editor, each under `dotnet watch`, and rebuilds them automatically when sources change. Distribution builds are also run from here.

```
dotnet tool install -g EmptyEngine.Host
emptyengine <Project>.emptyengine
```

Requires the .NET 10 SDK or later.
