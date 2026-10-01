# EmptyEngine.Core

EmptyEngine is an editor-only game engine with no runtime implementation of its own. This package is part of an example .NET implementation of a runtime for use with EmptyEngine.

This is the minimal contract shared between EmptyEngine runtimes and the editor. It contains asset keys and asset references, asset resolution, the scene serializer abstraction, and the runtime-side connection that exchanges scene state with the editor over WebSocket.

From your game project, create a `RuntimeEditorLink` and handle what it raises (events such as `ModeChanged`, and calls into the interface implementations you assign to `Scene` and `Assets`).

```cs
// Connect when launched from the editor
var editorLink = RuntimeMode.IsEditorAttached ?
    new RuntimeEditorLink(Console.WriteLine) :
    null;
editorLink?.ModeChanged += ...
editorLink?.Scene = ... 
editorLink?.Assets = ...
editorLink?.Start();
// ...
// Call periodically
editorLink?.Pump();
```

All events and interface implementation calls are invoked synchronously inside `Pump`.
If you need locking or other synchronization, do it in the module being called.
