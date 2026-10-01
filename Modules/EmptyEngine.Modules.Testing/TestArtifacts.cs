using EmptyEngine.Editor.Assets;
using EmptyEngine.Serialization.Editor;
using EmptyEngine.World.Editor;

namespace EmptyEngine.Modules.Testing;

/// <summary>両スタックの永続化境界を束ねたテスト用のアーティファクト窓口</summary>
internal static class TestArtifacts
{
    /// <summary><paramref name="rootPath"/> 下へ読み書きする窓口の構築</summary>
    public static EditorArtifacts At(string rootPath) =>
        new(new SceneArtifactStore(rootPath, new HierarchyBlobSerializer(CatalogStub.Schemas)), new AssetArtifactStore(rootPath, CatalogStub.Schemas));
}
