# EmptyEngine.Collision

EmptyEngine is an editor-only game engine with no runtime implementation of its own. This package is part of an example .NET implementation of a runtime for use with EmptyEngine.

Collider components are added to and removed from a registry as they are created and destroyed.
Code that tests for collisions against colliders needs a reference to the registry.
```cs
// If you move an object that has a collider component, an explicit Sync is required
collider.Sync();

// Query the registry for collisions
ICollider? hit = registry.Overlaps(position, radius);
ICollider? wall = registry.Raycast(from, to, out float t);
```
