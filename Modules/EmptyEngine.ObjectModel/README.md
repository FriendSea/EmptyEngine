# EmptyEngine.ObjectModel

EmptyEngine is an editor-only game engine with no runtime implementation of its own. This package is part of an example .NET implementation of a runtime for use with EmptyEngine.

The object and component model. It contains `IObject`, `TransformComponent`, references to objects within a scene (`ObjectReference`, `ComponentReference<T>`), the `[Asset]` attribute for types treated as assets, and more.

```cs
var transform = owner.GetAttachable<TransformComponent>();
transform?.Rotate(0f, 0f, 45f);
```
