using EmptyEngine.Editor;
using EmptyEngine.Editor.Assets;
using EmptyEngine.Editor.Authoring;
using EmptyEngine.Editor.Hosting;
using Xunit;

namespace EmptyEngine.Tests;

/// <summary>シーン追加 API のシーン指定の解決</summary>
public sealed class SceneAssetKeyResolutionTests
{
    private const string SceneKey = "scene-guid";

    private static readonly string AssetsRoot = Path.Combine(Path.GetTempPath(), "ee-scene-resolve", "Assets");
    private static readonly string ScenePath = Path.Combine(AssetsRoot, "Scenes", "Main.scene");
    private static readonly string ProbePath = Path.Combine(AssetsRoot, "Probe.asset");

    private static AssetCatalog Catalog()
    {
        var catalog = new AssetCatalog();
        catalog.Update(new Dictionary<string, ImportedSource>(StringComparer.Ordinal)
        {
            [SceneKey] = new ImportedScene("Scenes/Main.scene", new HierarchyNode("root", "Main"), ScenePath)
            {
                Key = SceneKey
            },
            ["probe-guid"] = new ImportedAsset("Probe.asset", new AuthoringObject(TestSchemas.Object("Probe"), new FieldValue()), ProbePath)
            {
                Key = "probe-guid"
            },
        });
        return catalog;
    }

    private static string? Resolve(string value) => EditorWebRunner.ResolveSceneAssetKey(Catalog(), value);

    [Theory]
    [InlineData(SceneKey)]
    [InlineData("Scenes/Main.scene")]
    [InlineData(@"Scenes\Main.scene")]
    [InlineData("scenes/main.SCENE")]
    [InlineData("  Scenes/Main.scene  ")]
    [InlineData("Scenes/Main.scene.meta")]
    public void KeyDisplayPathAndMetaResolveToTheScene(string value)
    {
        Assert.Equal(SceneKey, Resolve(value));
    }

    [Fact]
    public void AbsoluteSourcePathAndItsMetaResolveToTheScene()
    {
        Assert.Equal(SceneKey, Resolve(ScenePath));
        Assert.Equal(SceneKey, Resolve(ScenePath + ".meta"));
    }

    /// <remarks>シーンを足す API なので、シーン以外のアセットはどの書き方でも引かない</remarks>
    [Fact]
    public void NonSceneAssetsAreNotResolved()
    {
        Assert.Null(Resolve("probe-guid"));
        Assert.Null(Resolve("Probe.asset"));
        Assert.Null(Resolve(ProbePath));
    }

    [Fact]
    public void UnknownScenesAreNotResolved()
    {
        Assert.Null(Resolve("Scenes/Missing.scene"));
        Assert.Null(Resolve(Path.Combine(AssetsRoot, "Scenes", "Missing.scene")));
    }
}
