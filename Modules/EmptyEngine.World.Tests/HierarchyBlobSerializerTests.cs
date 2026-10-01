using EmptyEngine.Modules.Testing;
using EmptyEngine.Editor;
using EmptyEngine.Tests.Contracts;
using EmptyEngine.World.Editor;

namespace EmptyEngine.World.Tests;

public sealed class HierarchyBlobSerializerTests : HierarchyBlobContract
{
    protected override IHierarchyBlobSerializer CreateSubject() => new HierarchyBlobSerializer(CatalogStub.Schemas);

    protected override byte[] SampleBlob() => SceneFixture.Blob();
    protected override IReadOnlyList<HierarchyNode> ExpectedRoots() => [SceneFixture.Hierarchy()];
}
