# EmptyEngine.WebGpu

EmptyEngine is an editor-only game engine with no runtime implementation of its own. This package is part of an example .NET implementation of a runtime for use with EmptyEngine.

Rendering with WebGPU. It contains cameras, sprites, meshes, lines, effects whose appearance is defined by WGSL shaders, and a screen-space canvas. Desktop and iOS render with Dawn; the web renders with the browser's WebGPU.

```cs
var renderWorld = new RenderWorld();
IPresenter presenter = new DesktopPresenter(hwnd, hinstance, width, height);
// Call every frame
presenter.RenderFrame(delta, renderWorld);
```

## Attribution in distributed games

Depending on the platforms you build for, include the copyright notices and license texts of the following libraries in your game's credits or in a bundled text file.

- **Desktop, iOS**: Dawn (BSD-3-Clause), Abseil (Apache-2.0)

Both LICENSE files are in the package under `licenses/`.
