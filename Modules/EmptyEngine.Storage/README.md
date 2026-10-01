# EmptyEngine.Storage

EmptyEngine is an editor-only game engine with no runtime implementation of its own. This package is part of an example .NET implementation of a runtime for use with EmptyEngine.

Runtime asset storage and save data. During development it reads the editor's import results; in distribution builds it reads the bundled artifacts. On the web it reads assets served over HTTP by the editor.

It also abstracts a writable area you can use for save data and the like.
```cs
SaveStore.Write("save", text);
string? loaded = SaveStore.Read("save");
```
