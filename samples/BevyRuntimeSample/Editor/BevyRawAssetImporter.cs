using EmptyEngine.Editor;
using EmptyEngine.Editor.Assets;
using EmptyEngine.Editor.Authoring;

namespace BevyRuntimeSample.Editor;

/// <summary>画像等を無変換で取り込むインポーター</summary>
public sealed class BevyRawAssetImporter : IAssetImporter
{
    public IReadOnlyCollection<string> SupportedExtensions => [".png", ".jpg", ".jpeg", ".bmp", ".webp"];

    public Task<AssetImportResult> ImportAsync(AssetImportRequest request, CancellationToken cancellationToken = default)
    {
        string extension = Path.GetExtension(request.SourcePath).TrimStart('.').ToLowerInvariant();

        // このスタックのカタログは Rust の型から焼かれるので、BevyRawAsset に対応する
        // 型記述は無い。CLR 反射に頼らず、このインポータが自分で authoring 表現を組む。
        var imported = new ImportedAsset(
            request.RelativePath,
            BevyAssetArtifactStore.RawAsset(new FileAssetBinary(request.SourcePath), extension),
            request.SourcePath);
        return Task.FromResult(AssetImportResult.Succeeded(
            $"Imported {request.RelativePath} (raw, .{extension})", imported));
    }
}
