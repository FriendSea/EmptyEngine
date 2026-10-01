using System.Runtime.CompilerServices;
using EmptyEngine.Graphics;
using Xunit;

namespace EmptyEngine.Text.Tests;

/// <summary>字ごとに焼いたテクスチャの使い回しの検証</summary>
public sealed class GlyphTextTests
{
    [Fact]
    public void Same_character_twice_shares_one_texture()
    {
        FontAsset font = LoadFont();
        var glyphs = new List<GlyphPlacement>();

        GlyphText.Arrange(font, "00", 48f, TextAlignment.Left, glyphs, out _, out _);

        Assert.Equal(2, glyphs.Count);
        Assert.Same(glyphs[0].Texture, glyphs[1].Texture);
    }

    [Fact]
    public void Rearranging_reuses_the_textures_of_characters_that_did_not_change()
    {
        FontAsset font = LoadFont();
        var before = new List<GlyphPlacement>();
        var after = new List<GlyphPlacement>();

        GlyphText.Arrange(font, "01:23", 48f, TextAlignment.Left, before, out _, out _);
        TextureAsset[] first = [.. before.Select(static g => g.Texture)];
        GlyphText.Arrange(font, "01:24", 48f, TextAlignment.Left, after, out _, out _);

        Assert.Equal(first.Length, after.Count);
        for (int i = 0; i < first.Length - 1; i++)
            Assert.Same(first[i], after[i].Texture);
        Assert.NotSame(first[^1], after[^1].Texture);
    }

    [Fact]
    public void Blank_only_text_places_no_glyph()
    {
        FontAsset font = LoadFont();
        var glyphs = new List<GlyphPlacement>();

        GlyphText.Arrange(font, "   ", 48f, TextAlignment.Left, glyphs, out _, out _);

        Assert.Empty(glyphs);
    }

    [Fact]
    public void Ink_size_matches_what_the_whole_string_rasterizer_reports()
    {
        FontAsset font = LoadFont();
        var glyphs = new List<GlyphPlacement>();
        const int padding = 4;

        GlyphText.Arrange(font, "01:23.45", 48f, TextAlignment.Left, glyphs, out float width, out float height);
        TextureAsset whole = TextRenderer.Render(font, "01:23.45", 48f, padding: padding, alignment: TextAlignment.Left);

        Assert.Equal(whole.Width, (int)MathF.Ceiling(width) + (padding * 2));
        Assert.Equal(whole.Height, (int)MathF.Ceiling(height) + (padding * 2));
    }

    [Fact]
    public void Glyphs_are_placed_left_to_right_without_overlapping_their_ink()
    {
        FontAsset font = LoadFont();
        var glyphs = new List<GlyphPlacement>();

        GlyphText.Arrange(font, "123", 48f, TextAlignment.Left, glyphs, out _, out _);

        Assert.Equal(3, glyphs.Count);
        Assert.True(glyphs[0].X < glyphs[1].X && glyphs[1].X < glyphs[2].X, "Glyphs are not laid out from left to right.");
    }

    [Fact]
    public void Glyph_texture_carries_a_distance_beside_its_coverage()
    {
        FontAsset font = LoadFont();
        var glyphs = new List<GlyphPlacement>();

        GlyphText.Arrange(font, "0", 48f, TextAlignment.Left, glyphs, out _, out _, withDistance: true);

        byte[] pixels = ReadPixels(Assert.Single(glyphs).Texture);
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] == 255)
                Assert.True(pixels[i] > 128, "A filled pixel is outside the glyph.");
            else if (pixels[i + 3] == 0)
                Assert.True(pixels[i] < 128, "An unfilled pixel is inside the glyph.");
        }
    }

    [Fact]
    public void Distance_field_reaches_zero_before_the_texture_border()
    {
        FontAsset font = LoadFont();
        var glyphs = new List<GlyphPlacement>();
        const float pixelSize = 48f;
        float spread = GlyphDistanceField.SpreadFor(pixelSize);

        GlyphText.Arrange(font, "0", pixelSize, TextAlignment.Left, glyphs, out _, out _, withDistance: true);

        TextureAsset texture = Assert.Single(glyphs).Texture;
        byte[] pixels = ReadPixels(texture);
        for (int y = 0; y < texture.Height; y++)
        {
            for (int x = 0; x < texture.Width; x++)
            {
                if (x != 0 && y != 0 && x != texture.Width - 1 && y != texture.Height - 1)
                    continue;

                float inside = ((pixels[((y * texture.Width) + x) * 4] / 255f) - 0.5f) * 2f * spread;
                Assert.True(inside <= -(spread - 2f), $"({x}, {y}) is within reach of the outline.");
            }
        }
    }

    [Fact]
    public void Distance_field_is_baked_only_when_it_is_asked_for()
    {
        FontAsset font = LoadFont();
        var plain = new List<GlyphPlacement>();
        var outlined = new List<GlyphPlacement>();

        GlyphText.Arrange(font, "0", 48f, TextAlignment.Left, plain, out _, out _);
        GlyphText.Arrange(font, "0", 48f, TextAlignment.Left, outlined, out _, out _, withDistance: true);

        TextureAsset plainTexture = Assert.Single(plain).Texture;
        TextureAsset outlinedTexture = Assert.Single(outlined).Texture;
        Assert.NotSame(plainTexture, outlinedTexture);
        Assert.True(
            plainTexture.Width < outlinedTexture.Width && plainTexture.Height < outlinedTexture.Height,
            "A glyph without an outline still has distance-field padding.");
    }

    [Fact]
    public void Outlined_glyphs_are_baked_once_for_every_display_size()
    {
        FontAsset font = LoadFont();
        var small = new List<GlyphPlacement>();
        var large = new List<GlyphPlacement>();

        GlyphText.Arrange(font, "0", 48f, TextAlignment.Left, small, out _, out _, withDistance: true);
        GlyphText.Arrange(font, "0", 200f, TextAlignment.Left, large, out _, out _, withDistance: true);

        Assert.Same(Assert.Single(small).Texture, Assert.Single(large).Texture);
    }

    [Fact]
    public void Outlined_quads_scale_with_the_display_size()
    {
        FontAsset font = LoadFont();
        var small = new List<GlyphPlacement>();
        var large = new List<GlyphPlacement>();

        GlyphText.Arrange(font, "0", 48f, TextAlignment.Left, small, out _, out _, withDistance: true);
        GlyphText.Arrange(font, "0", 96f, TextAlignment.Left, large, out _, out _, withDistance: true);

        GlyphPlacement a = Assert.Single(small);
        GlyphPlacement b = Assert.Single(large);
        Assert.Equal(a.X * 2f, b.X, 3);
        Assert.Equal(a.Y * 2f, b.Y, 3);
        Assert.Equal(a.Width * 2f, b.Width, 3);
        Assert.Equal(a.Height * 2f, b.Height, 3);
    }

    [Fact]
    public void Outlined_quad_holds_the_ink_with_the_outline_reach_around_it()
    {
        FontAsset font = LoadFont();
        var glyphs = new List<GlyphPlacement>();
        const float pixelSize = 200f;
        float texel = pixelSize / GlyphText.DistancePixelSize;
        float spread = GlyphDistanceField.SpreadFor(GlyphText.DistancePixelSize) * texel;

        GlyphText.Arrange(
            font, "0", pixelSize, TextAlignment.Left, glyphs, out float width, out float height, withDistance: true);

        GlyphPlacement glyph = Assert.Single(glyphs);
        Assert.Equal(-spread, glyph.X, 3);
        Assert.Equal(-spread, glyph.Y, 3);
        Assert.True(
            MathF.Abs(glyph.Width - (width + (spread * 2f))) <= texel + 1f,
            $"The quad is {glyph.Width} wide for {width} of ink plus {spread} of reach on both sides.");
        Assert.True(
            MathF.Abs(glyph.Height - (height + (spread * 2f))) <= texel + 1f,
            $"The quad is {glyph.Height} tall for {height} of ink plus {spread} of reach on both sides.");
    }

    private static byte[] ReadPixels(TextureAsset texture)
    {
        var pixels = new byte[texture.Pixels.Length];
        using Stream stream = texture.Pixels.OpenRead();
        stream.ReadExactly(pixels);
        return pixels;
    }

    private static FontAsset LoadFont()
        => new() { Data = new MemoryAssetBinary(File.ReadAllBytes(SampleFontPath())) };

    private static string SampleFontPath([CallerFilePath] string? thisFile = null)
        => Path.Combine(Path.GetDirectoryName(thisFile!)!, "Fonts", "ShareTechMono-Regular.ttf");
}
