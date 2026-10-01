# EmptyEngine.Graphics

EmptyEngine is an editor-only game engine with no runtime implementation of its own. This package is part of an example .NET implementation of a runtime for use with EmptyEngine.

Graphics resource definitions independent of any rendering API. It contains `Color`, `Rect`, textures, sprites, and a transcoder that expands Basis Universal–compressed textures into GPU formats.

```cs
// Expand a Basis Universal–compressed texture into a GPU format
TranscodedTexture pixels = texture.Transcode(BasisTranscodeTarget.Bc7);
```

## Attribution in distributed games

Depending on the platforms you build for, include the copyright notices and license texts of the following libraries in your game's credits or in a bundled text file.

- **Desktop, Web, iOS**: Basis Universal (Apache-2.0)

For Basis Universal, include the NOTICE file in addition to the LICENSE. Both are in the package under `native/basis_universal/`.
