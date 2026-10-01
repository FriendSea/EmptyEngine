using EmptyEngine.Modules.Testing;
using EmptyEngine.Core;
using EmptyEngine.Editor.Assets;
using EmptyEngine.Graphics;
using EmptyEngine.Storage;
using EmptyEngine.Tests;
using EmptyEngine.World;
using Xunit;

namespace EmptyEngine.Mesh.Tests;

/// <summary><see cref="MeshAsset"/> の blob 往復検証</summary>
public sealed class MeshAssetTests
{
    [Fact]
    public async Task Mesh_asset_round_trips_binaries_and_embedded_texture_slices()
    {
        string root = CreateTempDir();
        string sourceDir = CreateTempDir();
        try
        {
            byte[] vertices = MakeBytes(32 * 3, seed: 1);
            byte[] indices = MakeBytes(4 * 3, seed: 2);
            byte[] texture0 = MakeBytes(64, seed: 3);
            byte[] texture1 = MakeBytes(128, seed: 4);
            byte[] texturePixels = [.. texture0, .. texture1];

            string verticesFile = WriteFile(sourceDir, "v.bin", vertices);
            string indicesFile = WriteFile(sourceDir, "i.bin", indices);
            string pixelsFile = WriteFile(sourceDir, "t.bin", texturePixels);

            var asset = new MeshAsset
            {
                VertexCount = 3,
                IndexCount = 3,
                Subsets = [new MeshSubset { IndexOffset = 0, IndexCount = 3, MaterialIndex = 0 }],
                Materials =
                [
                    new MeshMaterial { EmbeddedTexture = 1 },
                    new MeshMaterial(),
                ],
                EmbeddedTextures =
                [
                    new MeshEmbeddedTexture { Width = 4, Height = 4, Format = TexturePixelFormat.BasisUniversalKtx2, Offset = 0, Length = texture0.Length },
                    new MeshEmbeddedTexture { Width = 8, Height = 8, Format = TexturePixelFormat.Rgba8Unorm, Offset = texture0.Length, Length = texture1.Length },
                ],
                Vertices = new FileAssetBinary(verticesFile),
                Indices = new FileAssetBinary(indicesFile),
                TexturePixels = new FileAssetBinary(pixelsFile),
            };

            var serializer = TestArtifacts.At(root);
            var resolver = new WorldAssetResolver(AssetStorage.FromDirectory(root));
            var reference = new AssetKey("models/test.mesh");

            await serializer.SaveAssetAsync(reference, ImporterUtils.FromClr(asset, CatalogStub.Schemas));
            bool resolved = resolver.TryLoad(new AssetReference<MeshAsset>(reference), out MeshAsset? actual);

            Assert.True(resolved);
            Assert.NotNull(actual);
            Assert.Equal(3, actual!.VertexCount);
            Assert.Single(actual.Subsets);
            Assert.Equal(2, actual.Materials.Length);

            Assert.Equal(vertices, ReadAll(actual.Vertices));
            Assert.Equal(indices, ReadAll(actual.Indices));

            Assert.Equal(2, actual.ResolvedTextures.Count);
            TextureAsset? embedded = actual.ResolvedTextures[0];
            Assert.NotNull(embedded);
            Assert.Equal(8, embedded!.Width);
            Assert.Equal(TexturePixelFormat.Rgba8Unorm, embedded.Format);
            Assert.Equal(texture1.Length, embedded.Pixels.Length);
            Assert.Equal(texture1, ReadAll(embedded.Pixels));
            Assert.Null(actual.ResolvedTextures[1]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    private static byte[] MakeBytes(int length, int seed)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = (byte)((i * 31 + seed * 97) % 251);
        }
        return bytes;
    }

    private static string WriteFile(string dir, string name, byte[] bytes)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] ReadAll(IAssetBinary binary)
    {
        using Stream stream = binary.OpenRead();
        byte[] bytes = new byte[binary.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static string CreateTempDir()
    {
        string path = Path.Combine(Path.GetTempPath(), "ee-mesh-asset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
