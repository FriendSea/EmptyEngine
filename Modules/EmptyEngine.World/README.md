# EmptyEngine.World

EmptyEngine is an editor-only game engine with no runtime implementation of its own. This package is part of an example .NET implementation of a runtime for use with EmptyEngine.

The runtime world of a scene. It contains `GameObject`, `SceneWorld`, restoring from scene blobs, asset reference resolution, and loading of startup scenes.

```cs
var world = new SceneWorld(new WorldAssetResolver());

IObject instance = world.Instantiate(prefab);
instance.Destroy();

// Call periodically
world.FlushPendingDestructions();
```

## Attribution in distributed games

Depending on the platforms you build for, include the copyright notices and license texts of the following libraries in your game's credits or in a bundled text file.

- **Desktop, Web, iOS**: MessagePack and its bundled lz4net and BufferWriter.cs (MIT, BSD-2-Clause, Apache-2.0)
- **Desktop, iOS**: Microsoft.NET.StringTools (MIT)
