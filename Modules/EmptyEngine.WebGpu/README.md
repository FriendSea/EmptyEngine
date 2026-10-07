# EmptyEngine.WebGpu

EmptyEngine is an editor-only game engine with no runtime implementation of its own. This package is part of an example .NET implementation of a runtime for use with EmptyEngine.

Rendering with WebGPU. It contains cameras, sprites, meshes, lines, effects whose appearance is defined by WGSL shaders, and a screen-space canvas. Desktop and iOS render with Dawn; the web renders with the browser's WebGPU.

```cs
var renderWorld = new RenderWorld();
IPresenter presenter = new DesktopPresenter(hwnd, hinstance, width, height);
// Call every frame
presenter.RenderFrame(delta, renderWorld);
```

## Materials and shaders

Sprite, Mesh, LineRenderer and Effect components draw with a `MaterialAsset`. The material names a WGSL shader that must define `fs_main`, and owns the render state (blend, depth write, depth compare, queue), the fragment-side parameter values, the main texture and the extra textures. A component with no material uses the bundled default for its kind.

The vertex shader is chosen in this order. A `vs_main` whose vertex inputs do not match the component is skipped with a warning.

1. The component's `Shader`
2. The material's shader, if it defines `vs_main`
3. The bundled vertex shader for the component kind

Bind groups:

| Group | Contents | Stages | Owner |
|---|---|---|---|
| 0 | binding 0: component uniform; binding 3: `Params` struct whose values live on the component; binding 31: Effect emitter history | vertex, fragment | component instance |
| 1 | bindings 0–7: world-shared values, one variable per binding (`f32` or `vec2`/`vec3`/`vec4<f32>`), set through `RenderWorld.Globals` by slot number. Unset slots read 0 | vertex, fragment | renderer |
| 2 | binding 0: `Params` struct whose values live on the material; binding 1: sampler; bindings 2+: extra `texture_2d<f32>` | fragment | material |
| 3 | binding 0: main texture (the component's own if it has one, otherwise the material's); binding 1: its sampler | fragment | texture and sampler pair |

A `Params` struct holds `f32` and `vec4<f32>` members; a trailing `// default <value>` comment on a member sets its initial value.

The bundled vertex shaders write these varyings. A fragment shader that reads only these (and not group 0) works with every component kind. Stages are matched by `@location` number and type only.

| Location | Type | Meaning |
|---|---|---|
| 0 | `vec2<f32>` | uv |
| 1 | `vec4<f32>` | color |
| 2 | `f32` | view depth |
| 3 | `vec3<f32>` | normal (a fixed −Z for kinds without normals) |

The engine has no lights and passes no world time to shaders. Sprite (`anim.x`) and Effect (`time`) receive the seconds since the component first drew.

## Attribution in distributed games

Depending on the platforms you build for, include the copyright notices and license texts of the following libraries in your game's credits or in a bundled text file.

- **Desktop, iOS**: Dawn (BSD-3-Clause), Abseil (Apache-2.0)

Both LICENSE files are in the package under `licenses/`.
