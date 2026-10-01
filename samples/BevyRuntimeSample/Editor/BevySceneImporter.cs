using EmptyEngine.Editor;
using EmptyEngine.Editor.Authoring;
using EmptyEngine.Editor.Assets;

namespace BevyRuntimeSample.Editor;

/// <summary>ソースシーン（<c>.bscene</c>）と <see cref="HierarchyNode"/> の相互変換</summary>
public sealed class BevySceneImporter(ISchemaSource schemas) : ISceneImporter
{
    public IReadOnlyCollection<string> SupportedExtensions => [".bscene"];

    public Task<AssetImportResult> ImportAsync(AssetImportRequest request, CancellationToken cancellationToken = default)
    {
        HierarchyNode scene = BevySceneText.ReadScene(File.ReadAllText(request.SourcePath), schemas);
        var imported = new ImportedScene(request.RelativePath, scene, request.SourcePath);
        return Task.FromResult(AssetImportResult.Succeeded($"Imported scene {request.RelativePath}", imported));
    }

    public Task SaveAsync(HierarchyNode scene, string filePath, CancellationToken cancellationToken = default)
    {
        File.WriteAllText(filePath, BevySceneText.WriteScene(scene));
        return Task.CompletedTask;
    }
}
