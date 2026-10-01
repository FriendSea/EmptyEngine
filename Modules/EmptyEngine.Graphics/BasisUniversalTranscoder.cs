using System.Runtime.InteropServices;

namespace EmptyEngine.Graphics;

public enum BasisTranscodeTarget : uint
{
    Rgba8 = 0,
    Bc7 = 1,
    Astc4x4 = 2,
    Etc2Rgba8 = 3,
}

public readonly record struct TranscodedTexture(
    byte[] Pixels,
    BasisTranscodeTarget Target,
    uint BytesPerRow,
    uint RowsPerImage);

internal static unsafe class BasisUniversalTranscoder
{
#if IOS
    private const string NativeLibrary = "__Internal";
#else
    private const string NativeLibrary = "basisu_transcoder";
#endif

    [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint ee_basis_transcode_ktx2_uastc(
        byte* data,
        uint dataSize,
        uint expectedWidth,
        uint expectedHeight,
        BasisTranscodeTarget target,
        byte* output,
        uint outputSize);

    internal static TranscodedTexture Transcode(TextureAsset asset, BasisTranscodeTarget target)
        => asset.Transcode(target);

    internal static TranscodedTexture Transcode(TextureAsset asset, BasisTranscodeTarget target, byte[] source)
    {
        bool compressed = target != BasisTranscodeTarget.Rgba8;
        uint blocksWide = checked((uint)(asset.Width + 3) / 4);
        uint blockRows = checked((uint)(asset.Height + 3) / 4);
        uint bytesPerRow = compressed ? checked(blocksWide * 16) : checked((uint)asset.Width * 4);
        uint rowsPerImage = compressed ? blockRows : (uint)asset.Height;
        uint outputSize = checked(bytesPerRow * rowsPerImage);
        byte[] output = new byte[outputSize];

        fixed (byte* sourcePointer = source)
        fixed (byte* outputPointer = output)
        {
            uint error = ee_basis_transcode_ktx2_uastc(
                sourcePointer,
                checked((uint)source.Length),
                (uint)asset.Width,
                (uint)asset.Height,
                target,
                outputPointer,
                outputSize);
            if (error != 0)
                throw new InvalidDataException($"Basis Universal KTX2 transcode failed (error {error}).");
        }

        return new TranscodedTexture(output, target, bytesPerRow, rowsPerImage);
    }
}
