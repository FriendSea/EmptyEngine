# EmptyEngine.PlayerLoop

EmptyEngine is an editor-only game engine with no runtime implementation of its own. This package is part of an example .NET implementation of a runtime for use with EmptyEngine.

Frame-driven updates. It contains a registry that groups updates, frame waits, and tweens, along with `UpdatableComponent` and awaitable frame waits and tweens. If you stop advancing a registry, everything that belongs to it stops together.

```cs
var loop = new DefaultPlayerLoopRegistry();
// Call periodically
loop.Tick(deltaSeconds);

// Wait for frames
await loop.WaitFrames(30);

// Tween
await Tween.Play(0.3, t => { /* ... */ }, loop, Ease.QuadInOut);
```
