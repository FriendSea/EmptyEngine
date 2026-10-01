using EmptyEngine.Core;
using EmptyEngine.Graphics.Editor;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace EmptyEngine.Graphics.Tests;

public sealed class BasisUniversalTextureTests
{
    [Fact]
    public void EncoderProducesSingleLevelUncompressedUastcThatTranscodesToAllRuntimeTargets()
    {
        using var image = new Image<Rgba32>(5, 3, new Rgba32(20, 80, 160, 200));
        byte[] ktx2 = TextureEncoding.EncodeBasisUniversal(image);

        byte[] identifier = [0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A];
        Assert.True(ktx2.AsSpan().StartsWith(identifier));

        var asset = new TextureAsset
        {
            Width = 5,
            Height = 3,
            Format = TexturePixelFormat.BasisUniversalKtx2,
            Pixels = new TestBinary(ktx2),
        };

        Assert.Equal(60, BasisUniversalTranscoder.Transcode(asset, BasisTranscodeTarget.Rgba8).Pixels.Length);
        Assert.Equal(32, BasisUniversalTranscoder.Transcode(asset, BasisTranscodeTarget.Bc7).Pixels.Length);
        Assert.Equal(32, BasisUniversalTranscoder.Transcode(asset, BasisTranscodeTarget.Astc4x4).Pixels.Length);
        Assert.Equal(32, BasisUniversalTranscoder.Transcode(asset, BasisTranscodeTarget.Etc2Rgba8).Pixels.Length);
    }

    [Fact]
    public void TextureAssetCachesTranscodedResultsUntilDisposed()
    {
        using var image = new Image<Rgba32>(5, 3, new Rgba32(20, 80, 160, 200));
        var asset = new TextureAsset
        {
            Width = 5,
            Height = 3,
            Format = TexturePixelFormat.BasisUniversalKtx2,
            Pixels = new TestBinary(TextureEncoding.EncodeBasisUniversal(image)),
        };

        TranscodedTexture first = asset.Transcode(BasisTranscodeTarget.Bc7);
        TranscodedTexture second = asset.Transcode(BasisTranscodeTarget.Bc7);

        Assert.Same(first.Pixels, second.Pixels);
        Assert.Equal(first.Target, second.Target);
        Assert.Equal(first.BytesPerRow, second.BytesPerRow);
        Assert.Equal(first.RowsPerImage, second.RowsPerImage);
    }

    private sealed class TestBinary(byte[] bytes) : IAssetBinary
    {
        public long Length => bytes.Length;
        public Stream OpenRead() => new MemoryStream(bytes, writable: false);
    }
}
