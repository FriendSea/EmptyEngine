using EmptyEngine.Modules.Testing;
using System.Runtime.CompilerServices;
using EmptyEngine.Core;
using EmptyEngine.Storage;
using EmptyEngine.Editor;
using EmptyEngine.Graphics;
using EmptyEngine.Tests;
using EmptyEngine.Text.Editor;
using EmptyEngine.World;
using Xunit;

namespace EmptyEngine.Text.Tests;

/// <summary>ランタイム文字描画の検証</summary>
public sealed class TextRendererTests
{
    [Fact]
    public void Renders_text_into_a_nonempty_rgba_texture()
    {
        var font = new FontAsset { Data = new MemoryAssetBinary(File.ReadAllBytes(SampleFontPath())) };

        TextureAsset texture = TextRenderer.Render(font, "Hi", pixelSize: 64f);

        Assert.Equal(TexturePixelFormat.Rgba8Unorm, texture.Format);
        Assert.True(texture.Width > 0 && texture.Height > 0);
        Assert.Equal((long)texture.Width * texture.Height * 4, texture.Pixels.Length);
        Assert.True(HasOpaquePixel(texture), "The rasterized result has no opaque pixels (no text was drawn).");
    }

    [Fact]
    public void Empty_text_yields_fully_transparent_texture_without_throwing()
    {
        var font = new FontAsset { Data = new MemoryAssetBinary(File.ReadAllBytes(SampleFontPath())) };

        TextureAsset texture = TextRenderer.Render(font, string.Empty);

        Assert.True(texture.Width > 0 && texture.Height > 0);
        Assert.False(HasOpaquePixel(texture));
    }

    [Fact]
    public async Task Imported_font_roundtrips_through_artifact_and_renders()
    {
        string source = CreateTempDir();
        try
        {
            var importer = new FontImporter(CatalogStub.Schemas);
            AssetImportResult result = await importer.ImportAsync(new AssetImportRequest(SampleFontPath(), "Font.ttf"));
            Assert.True(result.Success);

            var serializer = TestArtifacts.At(source);
            await serializer.SaveAssetAsync(new AssetKey("font.bin"), AuthoringTestHelpers.AssetOf(result));

            var resolver = new WorldAssetResolver(AssetStorage.FromDirectory(source));
            Assert.True(resolver.TryLoad(new AssetReference<FontAsset>("font.bin"), out FontAsset? resolved));
            Assert.NotNull(resolved);

            TextureAsset texture = TextRenderer.Render(resolved!, "Score", pixelSize: 48f);
            Assert.True(HasOpaquePixel(texture));
        }
        finally
        {
            Directory.Delete(source, recursive: true);
        }
    }

    [Fact]
    public void Multiline_alignment_moves_the_short_line_within_the_texture()
    {
        var font = new FontAsset { Data = new MemoryAssetBinary(File.ReadAllBytes(SampleFontPath())) };

        const string text = "WWWWWW\nI";

        TextureAsset left = TextRenderer.Render(font, text, alignment: TextAlignment.Left);
        TextureAsset right = TextRenderer.Render(font, text, alignment: TextAlignment.Right);

        Assert.Equal(left.Width, right.Width);
        Assert.True(
            InkCenterXOfBottomHalf(right) > InkCenterXOfBottomHalf(left),
            "The short line is not shifted right with right alignment (alignment has no effect).");
    }

    [Fact]
    public void Default_alignment_is_center_so_scenes_without_the_key_keep_their_look()
    {
        Assert.Equal(TextAlignment.Center, default(TextAlignment));
    }

    private static float InkCenterXOfBottomHalf(TextureAsset texture)
    {
        var bytes = new byte[texture.Pixels.Length];
        using (Stream stream = texture.Pixels.OpenRead())
        {
            stream.ReadExactly(bytes);
        }

        long sum = 0;
        long count = 0;
        for (int y = texture.Height / 2; y < texture.Height; y++)
        {
            for (int x = 0; x < texture.Width; x++)
            {
                if (bytes[(((y * texture.Width) + x) * 4) + 3] == 0) continue;
                sum += x;
                count++;
            }
        }

        Assert.True(count > 0, "The lower half has no ink (the test text did not wrap into two lines as expected).");
        return (float)sum / count;
    }

    private static bool HasOpaquePixel(TextureAsset texture)
    {
        var bytes = new byte[texture.Pixels.Length];
        using (Stream stream = texture.Pixels.OpenRead())
        {
            stream.ReadExactly(bytes);
        }

        for (int i = 3; i < bytes.Length; i += 4)
        {
            if (bytes[i] != 0) return true;
        }

        return false;
    }

    private static string SampleFontPath([CallerFilePath] string? thisFile = null)
        => Path.Combine(Path.GetDirectoryName(thisFile!)!, "Fonts", "ShareTechMono-Regular.ttf");

    private static string CreateTempDir()
    {
        string path = Path.Combine(Path.GetTempPath(), "ee-text-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
