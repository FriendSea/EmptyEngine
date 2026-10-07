using EmptyEngine.Editor;
using EmptyEngine.Editor.Assets;
using EmptyEngine.Editor.Authoring;

namespace EmptyEngine.WebGpu.Editor;

/// <summary>WGSL シェーダファイルの <see cref="ShaderAsset"/> としての取り込み</summary>
public sealed class ShaderImporter(ISchemaSource schemas) : IAssetImporter
{
    public IReadOnlyCollection<string> SupportedExtensions => [".wgsl"];

    public Task<AssetImportResult> ImportAsync(AssetImportRequest request, CancellationToken cancellationToken = default)
    {
        string wgsl = File.ReadAllText(request.SourcePath);

        var asset = new ShaderAsset
        {
            Source = new FileAssetBinary(request.SourcePath),
            HasVertex = WgslShaderParams.HasVertexEntry(wgsl),
            HasFragment = WgslShaderParams.HasFragmentEntry(wgsl),
            VertexInputs = WgslShaderParams.ParseVertexInputs(wgsl),
            VertexParams = WgslShaderParams.ParseVertexParams(wgsl),
            FragmentParams = WgslShaderParams.ParseFragmentParams(wgsl),
            TextureSlots = WgslShaderParams.ParseTextureSlots(wgsl),
        };

        var imported = new ImportedAsset(request.RelativePath, ImporterUtils.FromClr(asset, schemas), request.SourcePath);
        return Task.FromResult(AssetImportResult.Succeeded(
            $"Imported shader {request.RelativePath}",
            imported));
    }
}
