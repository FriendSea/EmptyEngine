using EmptyEngine.Modules.Testing;
using EmptyEngine.Core;
using EmptyEngine.Storage;
using EmptyEngine.Editor.Assets;
using EmptyEngine.Tests;
using Xunit;

namespace EmptyEngine.World.Tests;

/// <summary>本体を持つアセットがメタデータと本体を分離したまま往復することの検証</summary>
public sealed class BinaryAssetTests
{
    [Fact]
    public async Task Binary_asset_round_trips_metadata_and_payload()
    {
        string root = CreateTempDir();
        string sourceDir = CreateTempDir();
        try
        {
            byte[] payload = new byte[4096];
            for (int i = 0; i < payload.Length; i++)
            {
                payload[i] = (byte)(i % 251);
            }

            string sourceFile = Path.Combine(sourceDir, "source.bin");
            File.WriteAllBytes(sourceFile, payload);

            var serializer = TestArtifacts.At(root);
            var resolver = new WorldAssetResolver(AssetStorage.FromDirectory(root));
            var asset = new BinaryTestAsset
            {
                Width = 64,
                Height = 32,
                Payload = new FileAssetBinary(sourceFile),
            };
            var reference = new AssetKey("textures/sprite.tex");

            await serializer.SaveAssetAsync(reference, ImporterUtils.FromClr(asset, CatalogStub.Schemas));

            bool resolved = resolver.TryLoad(new AssetReference<BinaryTestAsset>(reference), out BinaryTestAsset? actual);

            Assert.True(resolved);
            Assert.NotNull(actual);
            Assert.Equal(64, actual!.Width);
            Assert.Equal(32, actual.Height);
            Assert.NotNull(actual.Payload);
            Assert.Equal(payload.Length, actual.Payload.Length);

            using Stream stream = actual.Payload.OpenRead();
            byte[] readBack = new byte[payload.Length];
            stream.ReadExactly(readBack);

            Assert.Equal(payload, readBack);
            Assert.Equal(-1, stream.ReadByte());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    private static string CreateTempDir()
    {
        string path = Path.Combine(Path.GetTempPath(), "ee-binary-asset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
