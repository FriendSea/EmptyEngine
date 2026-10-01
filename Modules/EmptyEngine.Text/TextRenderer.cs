using System.Runtime.CompilerServices;
using EmptyEngine.Graphics;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using FontsTextAlignment = SixLabors.Fonts.TextAlignment;

namespace EmptyEngine.Text;

/// <summary><see cref="FontAsset"/> と文字列からの RGBA8 テクスチャの実行時生成</summary>
public static class TextRenderer
{
    private static readonly ConditionalWeakTable<FontAsset, FontFamilyHolder> FamilyCache = new();

    private sealed class FontFamilyHolder(FontFamily family)
    {
        public FontFamily Family { get; } = family;
    }

    /// <summary>文字列をラスタライズしたテクスチャの取得</summary>
    public static TextureAsset Render(
        FontAsset font,
        string text,
        float pixelSize = 48f,
        byte r = 255,
        byte g = 255,
        byte b = 255,
        byte a = 255,
        int padding = 4,
        TextAlignment alignment = TextAlignment.Left)
    {
        ArgumentNullException.ThrowIfNull(font);
        text ??= string.Empty;

        Font typeface = GetFamily(font).CreateFont(pixelSize, FontStyle.Regular);
        FontsTextAlignment lineAlignment = ToFontsAlignment(alignment);

        var measureOptions = new RichTextOptions(typeface) { TextAlignment = lineAlignment };
        FontRectangle bounds = TextMeasurer.MeasureBounds(text, measureOptions);

        int width = Math.Max(1, (int)Math.Ceiling(bounds.Width) + (padding * 2));
        int height = Math.Max(1, (int)Math.Ceiling(bounds.Height) + (padding * 2));

        using var image = new Image<Rgba32>(width, height);

        if (text.Length > 0 && bounds.Width > 0 && bounds.Height > 0)
        {
            var drawOptions = new RichTextOptions(typeface)
            {
                TextAlignment = lineAlignment,
                Origin = new PointF(padding - bounds.X, padding - bounds.Y),
            };
            SixLabors.ImageSharp.Color color = SixLabors.ImageSharp.Color.FromRgba(r, g, b, a);
            image.Mutate(ctx => ctx.DrawText(drawOptions, text, color));
        }

        var pixels = new byte[width * height * 4];
        image.CopyPixelDataTo(pixels);

        return new TextureAsset
        {
            Width = width,
            Height = height,
            Format = TexturePixelFormat.Rgba8Unorm,
            Pixels = new MemoryAssetBinary(pixels),
        };
    }

    internal static FontsTextAlignment ToFontsAlignment(TextAlignment alignment) => alignment switch
    {
        TextAlignment.Center => FontsTextAlignment.Center,
        TextAlignment.Right => FontsTextAlignment.End,
        _ => FontsTextAlignment.Start,
    };

    internal static FontFamily GetFamily(FontAsset font)
    {
        if (FamilyCache.TryGetValue(font, out FontFamilyHolder? holder))
        {
            return holder.Family;
        }

        var collection = new FontCollection();
        FontFamily family;
        using (Stream source = font.Data.OpenRead())
        using (var seekable = new MemoryStream())
        {
            source.CopyTo(seekable);
            seekable.Position = 0;
            family = collection.Add(seekable);
        }

        holder = FamilyCache.GetValue(font, _ => new FontFamilyHolder(family));
        return holder.Family;
    }
}
