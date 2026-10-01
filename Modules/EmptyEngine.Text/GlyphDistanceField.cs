using EmptyEngine.Graphics;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace EmptyEngine.Text;

/// <summary>字の絵と、輪郭からの符号付き距離を一緒に持つテクスチャの焼き付け</summary>
/// <remarks>A チャンネルは被覆率、R チャンネルは輪郭からの符号付き距離を表す。距離は生成時の画素単位で、字の内側を正とし、±<see cref="SpreadFor"/> を 0..1 に対応させる。0.5 が輪郭を表し、周囲には <see cref="SpreadFor"/> 分の余白が付く。</remarks>
public static class GlyphDistanceField
{
    /// <summary>距離場が表せる、輪郭からの距離</summary>
    /// <remarks>字の絵の周りに取る余白でもある＝縁取りを伸ばせるのもこの太さまで</remarks>
    public static int SpreadFor(float pixelSize) => Math.Clamp((int)MathF.Ceiling(pixelSize * 0.2f), 4, 24);

    /// <summary>被覆率と距離を持つ 1 文字ぶんのテクスチャ</summary>
    /// <param name="pixelSize">字を焼く大きさ</param>
    public static TextureAsset Bake(FontAsset font, string text, float pixelSize)
    {
        ArgumentNullException.ThrowIfNull(font);
        text ??= string.Empty;

        int spread = SpreadFor(pixelSize);
        FontFamily family = TextRenderer.GetFamily(font);
        Font typeface = family.CreateFont(pixelSize, FontStyle.Regular);
        FontRectangle bounds = TextMeasurer.MeasureBounds(text, new RichTextOptions(typeface));

        int width = Math.Max(1, (int)MathF.Ceiling(bounds.Width) + (spread * 2));
        int height = Math.Max(1, (int)MathF.Ceiling(bounds.Height) + (spread * 2));
        var pixels = new byte[width * height * 4];

        if (text.Length > 0 && bounds.Width > 0f && bounds.Height > 0f)
        {
            WriteCoverage(typeface, text, bounds, spread, width, height, pixels);
            WriteDistance(family, text, pixelSize, bounds, spread, width, height, pixels);
        }

        return new TextureAsset
        {
            Width = width,
            Height = height,
            Format = TexturePixelFormat.Rgba8Unorm,
            Pixels = new MemoryAssetBinary(pixels),
        };
    }

    /// <summary>距離の計算に使う拡大倍率</summary>
    private const int Supersample = 4;

    /// <summary>スタックに置ける 1 列ぶんの長さ</summary>
    private const int StackLine = 256;

    /// <summary>字の被覆率の A への書き込み</summary>
    /// <remarks>RGB チャンネルは白にする。</remarks>
    private static void WriteCoverage(
        Font typeface, string text, FontRectangle bounds, int spread, int width, int height, byte[] pixels)
    {
        var options = new RichTextOptions(typeface)
        {
            Origin = new PointF(spread - bounds.X, spread - bounds.Y),
        };

        using var image = new Image<Rgba32>(width, height);
        image.Mutate(ctx => ctx.DrawText(options, text, SixLabors.ImageSharp.Color.White));
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < height; y++)
            {
                Span<Rgba32> row = accessor.GetRowSpan(y);
                for (int x = 0; x < width; x++)
                {
                    int index = ((y * width) + x) * 4;
                    pixels[index + 1] = 255;
                    pixels[index + 2] = 255;
                    pixels[index + 3] = row[x].A;
                }
            }
        });
    }

    /// <summary>輪郭からの符号付き距離の R への書き込み</summary>
    private static void WriteDistance(
        FontFamily family,
        string text,
        float pixelSize,
        FontRectangle bounds,
        int spread,
        int width,
        int height,
        byte[] pixels)
    {
        int scale = Supersample;
        int fineWidth = width * scale;
        int fineHeight = height * scale;
        bool[] ink = Rasterize(family, text, pixelSize * scale, bounds, spread, scale, fineWidth, fineHeight);

        var field = new float[fineWidth * fineHeight];
        var toInk = new float[width * height];
        var toGap = new float[width * height];

        SquaredDistance(ink, marked: true, fineWidth, fineHeight, field);
        Downsample(field, fineWidth, scale, width, height, toInk);
        SquaredDistance(ink, marked: false, fineWidth, fineHeight, field);
        Downsample(field, fineWidth, scale, width, height, toGap);

        for (int i = 0; i < toInk.Length; i++)
        {
            float depth = (toInk[i] > 0f ? 0.5f - toInk[i] : toGap[i] - 0.5f) / scale;
            float encoded = 0.5f + (depth / (spread * 2f));
            pixels[i * 4] = (byte)Math.Clamp((int)MathF.Round(encoded * 255f), 0, 255);
        }
    }

    /// <summary>字の絵を細かく焼いた、輪郭の内と外の印</summary>
    private static bool[] Rasterize(
        FontFamily family,
        string text,
        float pixelSize,
        FontRectangle bounds,
        int spread,
        int scale,
        int width,
        int height)
    {
        var options = new RichTextOptions(family.CreateFont(pixelSize, FontStyle.Regular))
        {
            Origin = new PointF((spread - bounds.X) * scale, (spread - bounds.Y) * scale),
        };

        var ink = new bool[width * height];
        using var image = new Image<Rgba32>(width, height);
        image.Mutate(ctx => ctx.DrawText(options, text, SixLabors.ImageSharp.Color.White));
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < height; y++)
            {
                Span<Rgba32> row = accessor.GetRowSpan(y);
                for (int x = 0; x < width; x++)
                    ink[(y * width) + x] = row[x].A >= 128;
            }
        });

        return ink;
    }

    /// <summary>細かい距離場からの、焼いた大きさの画素ごとの距離の抜き出し</summary>
    private static void Downsample(float[] field, int fineWidth, int scale, int width, int height, float[] into)
    {
        int center = scale / 2;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int fine = (((y * scale) + center) * fineWidth) + (x * scale) + center;
                into[(y * width) + x] = MathF.Sqrt(field[fine]);
            }
        }
    }

    /// <summary>各画素から、印の付いた画素までの距離の 2 乗</summary>
    private static void SquaredDistance(bool[] ink, bool marked, int width, int height, float[] into)
    {
        const float Far = 1e10f;
        for (int i = 0; i < into.Length; i++)
            into[i] = ink[i] == marked ? 0f : Far;

        int longest = Math.Max(width, height);
        Span<float> source = longest < StackLine ? stackalloc float[StackLine] : new float[longest];
        Span<float> nearest = longest < StackLine ? stackalloc float[StackLine] : new float[longest];
        Span<int> hull = longest < StackLine ? stackalloc int[StackLine] : new int[longest];
        Span<float> border = longest < StackLine ? stackalloc float[StackLine] : new float[longest + 1];

        for (int x = 0; x < width; x++)
        {
            for (int y = 0, i = x; y < height; y++, i += width)
                source[y] = into[i];
            LowerEnvelope(source[..height], nearest, hull, border);
            for (int y = 0, i = x; y < height; y++, i += width)
                into[i] = nearest[y];
        }

        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
                source[x] = into[row + x];
            LowerEnvelope(source[..width], nearest, hull, border);
            for (int x = 0; x < width; x++)
                into[row + x] = nearest[x];
        }
    }

    /// <summary>各位置に立てた放物線が、位置ごとにいちばん低くなる高さ</summary>
    private static void LowerEnvelope(ReadOnlySpan<float> source, Span<float> into, Span<int> hull, Span<float> border)
    {
        int count = source.Length;
        int top = 0;
        hull[0] = 0;
        border[0] = float.NegativeInfinity;
        border[1] = float.PositiveInfinity;

        for (int q = 1; q < count; q++)
        {
            float raised = source[q] + (q * q);
            int p = hull[top];
            float crossing = (raised - (source[p] + (p * p))) / (2 * (q - p));
            while (crossing <= border[top])
            {
                top--;
                p = hull[top];
                crossing = (raised - (source[p] + (p * p))) / (2 * (q - p));
            }

            top++;
            hull[top] = q;
            border[top] = crossing;
            border[top + 1] = float.PositiveInfinity;
        }

        top = 0;
        for (int q = 0; q < count; q++)
        {
            while (border[top + 1] < q)
                top++;
            int p = hull[top];
            into[q] = ((q - p) * (q - p)) + source[p];
        }
    }
}
