using System.Runtime.CompilerServices;
using EmptyEngine.Graphics;
using SixLabors.Fonts;
using SixLabors.Fonts.Unicode;
using SixLabors.ImageSharp.Drawing.Processing;

namespace EmptyEngine.Text;

/// <summary>一文字分のテクスチャと文字列内での配置。</summary>
/// <remarks>文字列全体の ink 矩形の左上を原点とし、右と下を正とする。矩形の大きさはテクスチャの余白を含む。</remarks>
public readonly record struct GlyphPlacement(TextureAsset Texture, float X, float Y, float Width, float Height);

/// <summary>文字列の、字ごとに焼いたテクスチャの並びとしての組み立て</summary>
/// <remarks>文字は白で生成する。描画色は利用側で指定する。縁取りを指定した場合は、輪郭からの距離を持つテクスチャを返す。</remarks>
public static class GlyphText
{
    /// <summary>字のテクスチャの余白（画素）。</summary>
    private const int GlyphPadding = 2;

    /// <summary>距離場を焼く大きさ（画素）</summary>
    /// <remarks>表示の大きさに依らず一定＝同じ字のテクスチャは表示の大きさをまたいで使い回される</remarks>
    public const float DistancePixelSize = 48f;

    private static readonly ConditionalWeakTable<FontAsset, FontGlyphCache> Caches = new();

    private sealed class FontGlyphCache
    {
        public readonly Dictionary<(float PixelSize, int CodePoint, bool WithDistance), TextureAsset?> Glyphs = [];
        public readonly Dictionary<(float PixelSize, TextAlignment Alignment), RichTextOptions> Options = [];
    }

    /// <summary>文字列の、字ごとの置き場所への分解</summary>
    /// <param name="into">結果の受け皿（呼び出しごとに詰め直す）</param>
    /// <param name="width">文字列全体の ink 幅</param>
    /// <param name="height">文字列全体の ink 高さ</param>
    /// <param name="withDistance">縁取りに使う、輪郭からの距離も持つ絵を焼くか</param>
    public static void Arrange(
        FontAsset font,
        string text,
        float pixelSize,
        TextAlignment alignment,
        List<GlyphPlacement> into,
        out float width,
        out float height,
        bool withDistance = false)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(into);

        into.Clear();
        width = 0f;
        height = 0f;
        if (string.IsNullOrEmpty(text))
            return;

        FontGlyphCache cache = Caches.GetValue(font, static _ => new FontGlyphCache());
        RichTextOptions options = GetOptions(font, cache, pixelSize, alignment);

        if (!TextMeasurer.TryMeasureCharacterBounds(text, options, out ReadOnlySpan<GlyphBounds> glyphs)
            || glyphs.Length == 0)
        {
            return;
        }

        FontRectangle ink = Union(glyphs);
        width = ink.Width;
        height = ink.Height;

        float bakeSize = withDistance ? DistancePixelSize : pixelSize;
        float texel = pixelSize / bakeSize;
        float padding = (withDistance ? GlyphDistanceField.SpreadFor(bakeSize) : GlyphPadding) * texel;

        foreach (GlyphBounds glyph in glyphs)
        {
            FontRectangle bounds = glyph.Bounds;
            if (bounds.Width <= 0f || bounds.Height <= 0f)
                continue;

            TextureAsset? texture = GetGlyph(font, cache, bakeSize, glyph.Codepoint, withDistance);
            if (texture is null)
                continue;

            into.Add(new GlyphPlacement(
                texture,
                bounds.X - ink.X - padding,
                bounds.Y - ink.Y - padding,
                texture.Width * texel,
                texture.Height * texel));
        }
    }

    /// <summary>字ごとの矩形をすべて含む矩形</summary>
    private static FontRectangle Union(ReadOnlySpan<GlyphBounds> glyphs)
    {
        float left = float.MaxValue;
        float top = float.MaxValue;
        float right = float.MinValue;
        float bottom = float.MinValue;

        foreach (GlyphBounds glyph in glyphs)
        {
            FontRectangle bounds = glyph.Bounds;
            left = MathF.Min(left, bounds.Left);
            top = MathF.Min(top, bounds.Top);
            right = MathF.Max(right, bounds.Right);
            bottom = MathF.Max(bottom, bounds.Bottom);
        }

        return new FontRectangle(left, top, right - left, bottom - top);
    }

    private static RichTextOptions GetOptions(
        FontAsset font, FontGlyphCache cache, float pixelSize, TextAlignment alignment)
    {
        if (cache.Options.TryGetValue((pixelSize, alignment), out RichTextOptions? options))
            return options;

        Font typeface = TextRenderer.GetFamily(font).CreateFont(pixelSize, FontStyle.Regular);
        options = new RichTextOptions(typeface) { TextAlignment = TextRenderer.ToFontsAlignment(alignment) };
        cache.Options[(pixelSize, alignment)] = options;
        return options;
    }

    /// <remarks>距離場の有無で別の絵になるので、同じ字でも別々に焼いて持つ</remarks>
    private static TextureAsset? GetGlyph(
        FontAsset font, FontGlyphCache cache, float pixelSize, CodePoint codePoint, bool withDistance)
    {
        if (cache.Glyphs.TryGetValue((pixelSize, codePoint.Value, withDistance), out TextureAsset? texture))
            return texture;

        texture = withDistance
            ? GlyphDistanceField.Bake(font, codePoint.ToString(), pixelSize)
            : TextRenderer.Render(
                font, codePoint.ToString(), pixelSize, padding: GlyphPadding, alignment: TextAlignment.Left);
        if (IsBlank(texture))
            texture = null;

        cache.Glyphs[(pixelSize, codePoint.Value, withDistance)] = texture;
        return texture;
    }

    /// <summary>絵が何も無い（＝空白の）字か</summary>
    private static bool IsBlank(TextureAsset texture)
    {
        var pixels = new byte[texture.Pixels.Length];
        using (Stream stream = texture.Pixels.OpenRead())
        {
            stream.ReadExactly(pixels);
        }

        for (int i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0)
                return false;
        }

        return true;
    }
}
