# EmptyEngine.WebGpu.Editor

EmptyEngine is an editor-only game engine with no runtime implementation of its own. This package is part of an example .NET implementation of a runtime for use with EmptyEngine.

The editor side of EmptyEngine.WebGpu. It contains an importer for WGSL shaders and reads the parameters each shader declares.

The material inspector draws a preview with the browser's WebGPU: the material's `fs_main` on a quad, with its render state, parameter values and extra textures. The preview has no component, so it uses a stand-in vertex shader that writes the bundled varyings, the material's own main texture, zero for every global, and stand-in values for anything the fragment shader reads from group 0. A fragment shader whose inputs differ from the bundled varyings cannot be shown.
