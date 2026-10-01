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
            Params = WgslShaderParams.Parse(wgsl),
            TextureSlots = WgslShaderParams.ParseTextureSlots(wgsl),
            Globals = WgslShaderParams.ParseGlobals(wgsl),
            Blend = WgslShaderDirectives.ParseBlend(wgsl),
            Render = WgslShaderDirectives.ParseRender(wgsl),
            Queue = WgslShaderDirectives.ParseQueue(wgsl),
            DepthWrite = WgslShaderDirectives.ParseDepthWrite(wgsl),
            DepthCompare = WgslShaderDirectives.ParseDepthCompare(wgsl),
        };

        var imported = new ImportedAsset(request.RelativePath, ImporterUtils.FromClr(asset, schemas), request.SourcePath);
        return Task.FromResult(AssetImportResult.Succeeded(
            $"Imported shader {request.RelativePath}",
            imported));
    }
}
