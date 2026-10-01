using EmptyEngine.Modules.Testing;
using EmptyEngine.Editor;
using EmptyEngine.SceneSource.Editor;
using EmptyEngine.Tests.Contracts;

namespace EmptyEngine.SceneSource.Tests;

public sealed class SceneImporterTests : SceneImporterContract
{
    protected override ISceneImporter CreateSubject() => new SceneAssetImporter(CatalogStub.Schemas);

    protected override HierarchyNode SampleScene() => SceneFixture.Hierarchy();

    protected override string SceneRelativePath => "scenes/test.scene";
}
