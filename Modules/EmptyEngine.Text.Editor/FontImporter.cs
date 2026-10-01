using EmptyEngine.Editor;
using EmptyEngine.Editor.Assets;
using EmptyEngine.Editor.Authoring;

namespace EmptyEngine.Text.Editor;

/// <summary>TTF/OTF フォントファイルの <see cref="FontAsset"/> としての取り込み</summary>
public sealed class FontImporter(ISchemaSource schemas) : IAssetImporter
{
    public IReadOnlyCollection<string> SupportedExtensions => [".ttf", ".otf"];

    public Task<AssetImportResult> ImportAsync(AssetImportRequest request, CancellationToken cancellationToken = default)
    {
        var asset = new FontAsset
        {
            Data = new FileAssetBinary(request.SourcePath),
        };

        var imported = new ImportedAsset(request.RelativePath, ImporterUtils.FromClr(asset, schemas), request.SourcePath);
        return Task.FromResult(AssetImportResult.Succeeded(
            $"Imported font {request.RelativePath}",
            imported));
    }
}
