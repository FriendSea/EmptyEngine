# EmptyEngine.TypeCatalog

EmptyEngine is an editor-only game engine with no runtime implementation of its own. This package is part of an example .NET implementation of a runtime for use with EmptyEngine.

A build tool that generates the type catalog (`TypeCatalog.json`) from your game's assemblies. The editor never loads your game's DLLs; it learns the shape of the inspector and the default values from this catalog.

It writes `.artifacts/TypeCatalog.json` on every build. Use `EmptyEngineCatalogOutputPath` to change the output path, and `-p:EmptyEngineGenerateCatalog=false` to turn generation off.
