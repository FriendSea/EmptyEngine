using EmptyEngine.Modules.Testing;
using EmptyEngine.Core;
using EmptyEngine.Storage;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Authoring;
using EmptyEngine.Serialization.Editor;
using EmptyEngine.Tests.Contracts;
using EmptyEngine.World;
using Xunit;

namespace EmptyEngine.Serialization.Tests;

public sealed class AssetRoundTripTests : AssetRoundTripContract<TestAsset>
{
    protected override ISchemaSource Schemas => CatalogStub.Schemas;

    protected override IAssetArtifactStore CreateSerializer(string rootPath) => new AssetArtifactStore(rootPath, CatalogStub.Schemas);

    protected override IAssetResolver CreateResolver(string rootPath) => new WorldAssetResolver(AssetStorage.FromDirectory(rootPath));

    protected override TestAsset CreateSampleAsset() => new("hello world");

    protected override void AssertEquivalent(TestAsset expected, TestAsset actual) => Assert.Equal(expected, actual);

    protected override string AssetKey => "greeting.txt";
}
