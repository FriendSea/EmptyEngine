# EmptyEngine.Text

EmptyEngine is an editor-only game engine with no runtime implementation of its own. This package is part of an example .NET implementation of a runtime for use with EmptyEngine.

Text rendering. It contains font assets, glyph rasterization, distance fields for outlines, and text layout.

```cs
TextureAsset texture = TextRenderer.Render(font, "Hello");
```

## Attribution in distributed games

Depending on the platforms you build for, include the copyright notices and license texts of the following libraries in your game's credits or in a bundled text file.

- **Desktop, Web, iOS**: SixLabors.ImageSharp, ImageSharp.Drawing, and Fonts (Apache-2.0)

SixLabors libraries are under the Six Labors Split License, which is Apache-2.0 as long as you use them through this package. Referencing them directly from your game project comes with conditions (<https://sixlabors.com/pricing/>).
