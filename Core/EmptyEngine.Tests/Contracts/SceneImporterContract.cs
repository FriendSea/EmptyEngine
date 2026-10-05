using EmptyEngine.Editor;
using Xunit;

namespace EmptyEngine.Tests.Contracts;

/// <summary><see cref="ISceneImporter"/> 実装が満たすべき契約</summary>
public abstract class SceneImporterContract
{
    protected abstract ISceneImporter CreateSubject();

    /// <summary>保存・読み込みの対象となるサンプルシーン</summary>
    protected abstract HierarchyNode SampleScene();

    /// <summary>テストで使うソースファイルの相対パス（対象の拡張子に合わせること）</summary>
    protected abstract string SceneRelativePath { get; }

    [Fact]
    public async Task Scene_source_is_stable_through_save_import_save()
    {
        ISceneImporter importer = CreateSubject();
        string root = CreateTempRoot();
        try
        {
            HierarchyNode original = SampleScene();
            (string sourcePath, AssetImportRequest request) = Prepare(root);

            await importer.SaveAsync(original, sourcePath);
            string firstSource = await File.ReadAllTextAsync(sourcePath);

            AssetImportResult result = await importer.ImportAsync(request);
            Assert.True(result.Success);
            HierarchyNode imported = Assert.IsType<ImportedScene>(result.Asset).Root;

            Assert.Equal(original.Children.Count, imported.Children.Count);
            await importer.SaveAsync(imported, sourcePath);
            string secondSource = await File.ReadAllTextAsync(sourcePath);

            Assert.Equal(firstSource, secondSource);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private (string sourcePath, AssetImportRequest request) Prepare(string root)
    {
        string sourcePath = Path.Combine(root, SceneRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        var request = new AssetImportRequest(sourcePath, SceneRelativePath);
        return (sourcePath, request);
    }

    private static string CreateTempRoot()
    {
        string path = Path.Combine(Path.GetTempPath(), "ee-scene-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
