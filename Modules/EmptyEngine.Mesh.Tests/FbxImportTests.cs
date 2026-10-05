using EmptyEngine.Modules.Testing;
using EmptyEngine.Editor;
using EmptyEngine.Mesh.Editor;
using Xunit;

namespace EmptyEngine.Mesh.Tests;

/// <summary><see cref="FbxImporter"/> の取込検証</summary>
public sealed class FbxImportTests
{
    private const string CubeFbx = """
        ; FBX 7.3.0 project file
        FBXHeaderExtension:  {
        	FBXHeaderVersion: 1003
        	FBXVersion: 7300
        }
        GlobalSettings:  {
        	Version: 1000
        	Properties70:  {
        	}
        }
        Definitions:  {
        	Version: 100
        	Count: 2
        	ObjectType: "Model" {
        		Count: 1
        	}
        	ObjectType: "Geometry" {
        		Count: 1
        	}
        }
        Objects:  {
        	Geometry: 1000, "Geometry::cube", "Mesh" {
        		Vertices: *24 {
        			a: -50,-50,50,50,-50,50,50,50,50,-50,50,50,-50,-50,-50,50,-50,-50,50,50,-50,-50,50,-50
        		}
        		PolygonVertexIndex: *36 {
        			a: 0,1,-3,0,2,-4,5,4,-8,5,7,-7,4,0,-4,4,3,-8,1,5,-7,1,6,-3,3,2,-7,3,6,-8,4,5,-2,4,1,-1
        		}
        		GeometryVersion: 124
        	}
        	Model: 2000, "Model::cube", "Mesh" {
        		Version: 232
        	}
        }
        Connections:  {
        	C: "OO",1000,2000
        	C: "OO",2000,0
        }
        """;

    [Fact]
    public async Task Fbx_cube_imports_as_single_mesh_asset_in_meters()
    {
        string root = CreateTempDir();
        try
        {
            string fbxPath = Path.Combine(root, "cube.fbx");
            await File.WriteAllTextAsync(fbxPath, CubeFbx);

            var importer = new FbxImporter(CatalogStub.Schemas);
            Assert.Contains(".fbx", importer.SupportedExtensions);

            var request = new AssetImportRequest(fbxPath, "cube.fbx");
            AssetImportResult result = await importer.ImportAsync(request);

            Assert.True(result.Success, result.Message);
            ImportedSource imported = Assert.Single(result.Assets);
            Assert.Equal(string.Empty, imported.LocalId);

            MeshAsset mesh = await AuthoringTestHelpers.ResolveAsync<MeshAsset>(
                root, AuthoringTestHelpers.AssetOf(result));
            Assert.Equal(36, mesh.IndexCount);
            Assert.True(mesh.VertexCount >= 8, $"VertexCount={mesh.VertexCount}");
            Assert.Single(mesh.Subsets);
            Assert.Equal(36, mesh.Subsets[0].IndexCount);
            Assert.NotEmpty(mesh.Materials);
            Assert.Empty(mesh.EmbeddedTextures);

            // 存在しない本体も長さ 0 の領域として復元される。
            Assert.Equal(0, mesh.TexturePixels?.Length ?? 0);

            Assert.Equal(mesh.VertexCount * 8 * sizeof(float), mesh.Vertices.Length);
            Assert.Equal(mesh.IndexCount * sizeof(uint), mesh.Indices.Length);
            using Stream stream = mesh.Vertices.OpenRead();
            byte[] bytes = new byte[mesh.Vertices.Length];
            stream.ReadExactly(bytes);
            for (int v = 0; v < mesh.VertexCount; v++)
            {
                for (int axis = 0; axis < 3; axis++)
                {
                    float value = BitConverter.ToSingle(bytes, ((v * 8) + axis) * sizeof(float));
                    Assert.Equal(0.5f, MathF.Abs(value), precision: 3);
                }
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDir()
    {
        string path = Path.Combine(Path.GetTempPath(), "ee-fbx-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
