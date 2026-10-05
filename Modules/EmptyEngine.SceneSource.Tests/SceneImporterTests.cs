using EmptyEngine.Modules.Testing;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Assets;
using EmptyEngine.SceneSource.Editor;
using EmptyEngine.Tests.Contracts;

namespace EmptyEngine.SceneSource.Tests;

public sealed class SceneImporterTests : SceneImporterContract
{
    protected override ISceneImporter CreateSubject() => new SceneAssetImporter(CatalogStub.Schemas, new AssetCatalog());

    protected override HierarchyNode SampleScene() => SceneFixture.Hierarchy();

    protected override string SceneRelativePath => "scenes/test.scene";
}
