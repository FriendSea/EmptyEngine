using EmptyEngine.Editor;
using EmptyEngine.Editor.Assets;
using EmptyEngine.Editor.Authoring;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace EmptyEngine.Graphics.Editor;

/// <summary>PNG の BC7 圧縮での <see cref="TextureAsset"/> としての取り込み</summary>
public sealed class PngTextureImporter(ISchemaSource schemas) : IAssetImporter
{
    public IReadOnlyCollection<string> SupportedExtensions => [".png"];

    public async Task<AssetImportResult> ImportAsync(AssetImportRequest request, CancellationToken cancellationToken = default)
    {
        using Image<Rgba32> image = await Image.LoadAsync<Rgba32>(request.SourcePath, cancellationToken);
        int width = image.Width;
        int height = image.Height;

        byte[] ktx2 = TextureEncoding.EncodeBasisUniversal(image);

        string stagingPath = Path.Combine(Path.GetTempPath(), $"ee-tex-{Guid.NewGuid():N}.ktx2");
        await File.WriteAllBytesAsync(stagingPath, ktx2, cancellationToken);

        var asset = new TextureAsset
        {
            Width = width,
            Height = height,
            Format = TexturePixelFormat.BasisUniversalKtx2,
            Pixels = new FileAssetBinary(stagingPath),
        };

        var imported = new ImportedAsset(request.RelativePath, ImporterUtils.FromClr(asset, schemas), request.SourcePath);
        return AssetImportResult.Succeeded(
            $"Imported texture {request.RelativePath} ({width}x{height}, Basis Universal UASTC, {ktx2.Length} bytes)",
            imported);
    }
}
