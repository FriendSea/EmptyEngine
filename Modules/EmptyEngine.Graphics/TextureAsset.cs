using EmptyEngine.Core;
using EmptyEngine.ObjectModel;

namespace EmptyEngine.Graphics;

/// <summary>テクスチャ本体のピクセル格納形式</summary>
public enum TexturePixelFormat
{
    /// <summary>非圧縮 RGBA8</summary>
    Rgba8Unorm = 1,

    /// <summary>Basis Universal UASTC を格納した KTX2（1 mip、Zstandard なし）</summary>
    BasisUniversalKtx2 = 2,
}

/// <summary>インポート済みテクスチャ</summary>
[Asset]
public sealed class TextureAsset
{
    private readonly Dictionary<BasisTranscodeTarget, TranscodedTexture> _transcodedTextures = [];

    /// <summary>テクスチャ幅（px）</summary>
    public int Width;

    /// <summary>テクスチャ高さ（px）</summary>
    public int Height;

    /// <summary>ピクセル本体の格納形式</summary>
    public TexturePixelFormat Format;

    /// <summary>ピクセル本体（圧縮済みブロック列または RGBA8 連続バイト）</summary>
    public IAssetBinary Pixels = null!;

    /// <summary>Basis Universal KTX2 を指定ターゲット形式へ変換し、アセットが破棄されるまで再利用する</summary>
    public TranscodedTexture Transcode(BasisTranscodeTarget target)
    {
        if (_transcodedTextures.TryGetValue(target, out TranscodedTexture cached))
            return cached;

        if (Format != TexturePixelFormat.BasisUniversalKtx2)
            throw new InvalidOperationException("Only Basis Universal KTX2 textures can be transcoded.");

        if (Width <= 0 || Height <= 0)
            throw new InvalidDataException($"Invalid texture size {Width}x{Height}.");

        byte[] source;
        using (Stream stream = Pixels.OpenRead())
        using (var memory = new MemoryStream())
        {
            stream.CopyTo(memory);
            source = memory.ToArray();
        }

        TranscodedTexture transcoded = BasisUniversalTranscoder.Transcode(this, target, source);
        _transcodedTextures[target] = transcoded;
        return transcoded;
    }
}
