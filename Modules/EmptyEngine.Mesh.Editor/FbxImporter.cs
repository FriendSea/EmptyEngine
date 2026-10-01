using System.Numerics;
using System.Text.Json;
using EmptyEngine.Core;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Assets;
using EmptyEngine.Graphics.Editor;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Ai = Silk.NET.Assimp;
using Color = EmptyEngine.Graphics.Color;
using EmptyEngine.Editor.Authoring;
using TextureAsset = EmptyEngine.Graphics.TextureAsset;
using TexturePixelFormat = EmptyEngine.Graphics.TexturePixelFormat;

namespace EmptyEngine.Mesh.Editor;

/// <summary>FBX の 1 ファイル全体の <see cref="MeshAsset"/> としての取り込み</summary>
public sealed class FbxImporter(ISchemaSource schemas) : IAssetImporter
{
    public IReadOnlyCollection<string> SupportedExtensions => [".fbx"];

    public async Task<AssetImportResult> ImportAsync(AssetImportRequest request, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() => Import(request, schemas), cancellationToken);
    }

    private static unsafe AssetImportResult Import(AssetImportRequest request, ISchemaSource schemas)
    {
        Ai.Assimp assimp = Ai.Assimp.GetApi();
        var flags = (uint)(Ai.PostProcessSteps.Triangulate
                         | Ai.PostProcessSteps.GenerateSmoothNormals
                         | Ai.PostProcessSteps.JoinIdenticalVertices
                         | Ai.PostProcessSteps.ImproveCacheLocality
                         | Ai.PostProcessSteps.FlipUVs);
#if EMPTYENGINE_LEFT_HANDED
        flags |= (uint)(Ai.PostProcessSteps.MakeLeftHanded
                      | Ai.PostProcessSteps.FlipWindingOrder);
#endif
        Ai.Scene* scene = assimp.ImportFile(request.SourcePath, flags);
        if (scene is null || scene->MRootNode is null)
        {
            string error = assimp.GetErrorStringS();
            return AssetImportResult.Failed($"Assimp failed for {request.RelativePath}: {error}");
        }

        try
        {
            return Convert(assimp, scene, request, schemas);
        }
        finally
        {
            assimp.ReleaseImport(scene);
        }
    }

    private static unsafe AssetImportResult Convert(
        Ai.Assimp assimp, Ai.Scene* scene, AssetImportRequest request, ISchemaSource schemas)
    {
        var warnings = new List<string>();

        var materials = new MeshMaterial[Math.Max(1, (int)scene->MNumMaterials)];
        var embeddedTable = new List<MeshEmbeddedTexture>();
        var embeddedBytes = new List<byte[]>();
        var embeddedIndexByTexture = new Dictionary<nint, int>();
        for (int i = 0; i < materials.Length; i++)
        {
            materials[i] = i < scene->MNumMaterials
                ? ConvertMaterial(assimp, scene, scene->MMaterials[i], request, embeddedTable, embeddedBytes, embeddedIndexByTexture, warnings)
                : new MeshMaterial();
        }

        float unitScale = ReadUnitScale(scene) * 0.01f;
        var vertices = new List<float>();
        var indices = new List<uint>();
        var subsets = new List<MeshSubset>();
        BakeNode(scene, scene->MRootNode, Matrix4x4.CreateScale(unitScale), vertices, indices, subsets, warnings);

        if (vertices.Count == 0)
        {
            return AssetImportResult.Failed($"No triangle geometry in {request.RelativePath}");
        }

        string verticesPath = StagingPath("vtx");
        string indicesPath = StagingPath("idx");
        WriteFloats(verticesPath, vertices);
        WriteUints(indicesPath, indices);

        string? pixelsPath = null;
        if (embeddedBytes.Count > 0)
        {
            pixelsPath = StagingPath("tex");
            using FileStream stream = File.Create(pixelsPath);
            foreach (byte[] bytes in embeddedBytes)
            {
                stream.Write(bytes);
            }
        }

        var asset = new MeshAsset
        {
            VertexCount = vertices.Count / FloatsPerVertex,
            IndexCount = indices.Count,
            Subsets = [.. subsets],
            Materials = materials,
            EmbeddedTextures = [.. embeddedTable],
            Vertices = new FileAssetBinary(verticesPath),
            Indices = new FileAssetBinary(indicesPath),
            TexturePixels = pixelsPath is not null ? new FileAssetBinary(pixelsPath) : null,
        };

        string message = $"Imported mesh {request.RelativePath} " +
            $"({asset.VertexCount} verts, {asset.IndexCount} indices, {subsets.Count} subset(s), " +
            $"{materials.Length} material(s), {embeddedTable.Count} embedded texture(s))";
        if (warnings.Count > 0)
        {
            message += " | " + string.Join(" | ", warnings);
        }

        var imported = new ImportedAsset(request.RelativePath, ImporterUtils.FromClr(asset, schemas), request.SourcePath);
        return AssetImportResult.Succeeded(message, imported);
    }

    private const int FloatsPerVertex = 8;

    private static unsafe void BakeNode(
        Ai.Scene* scene,
        Ai.Node* node,
        Matrix4x4 parentWorld,
        List<float> vertices,
        List<uint> indices,
        List<MeshSubset> subsets,
        List<string> warnings)
    {
        Matrix4x4 world = Matrix4x4.Transpose(node->MTransformation) * parentWorld;

        for (uint i = 0; i < node->MNumMeshes; i++)
        {
            Ai.Mesh* mesh = scene->MMeshes[node->MMeshes[i]];
            BakeMesh(mesh, world, vertices, indices, subsets, warnings);
        }

        for (uint i = 0; i < node->MNumChildren; i++)
        {
            BakeNode(scene, node->MChildren[i], world, vertices, indices, subsets, warnings);
        }
    }

    private static unsafe void BakeMesh(
        Ai.Mesh* mesh,
        Matrix4x4 world,
        List<float> vertices,
        List<uint> indices,
        List<MeshSubset> subsets,
        List<string> warnings)
    {
        if (mesh->MNumVertices == 0 || mesh->MNumFaces == 0)
        {
            return;
        }

        Matrix4x4 normalMatrix = Matrix4x4.Invert(world, out Matrix4x4 inverse)
            ? Matrix4x4.Transpose(inverse)
            : world;

        uint baseVertex = (uint)(vertices.Count / FloatsPerVertex);
        Vector3* uvChannel = mesh->MTextureCoords.Element0;

        for (uint v = 0; v < mesh->MNumVertices; v++)
        {
            Vector3 position = Vector3.Transform(mesh->MVertices[v], world);
            Vector3 normal = mesh->MNormals is not null
                ? Vector3.Normalize(Vector3.TransformNormal(mesh->MNormals[v], normalMatrix))
                : Vector3.UnitY;
            Vector3 uv = uvChannel is not null ? uvChannel[v] : default;

            vertices.Add(position.X);
            vertices.Add(position.Y);
            vertices.Add(position.Z);
            vertices.Add(normal.X);
            vertices.Add(normal.Y);
            vertices.Add(normal.Z);
            vertices.Add(uv.X);
            vertices.Add(uv.Y);
        }

        int indexOffset = indices.Count;
        int skippedFaces = 0;
        for (uint f = 0; f < mesh->MNumFaces; f++)
        {
            Ai.Face face = mesh->MFaces[f];
            if (face.MNumIndices != 3)
            {
                skippedFaces++;
                continue;
            }

            indices.Add(baseVertex + face.MIndices[0]);
            indices.Add(baseVertex + face.MIndices[1]);
            indices.Add(baseVertex + face.MIndices[2]);
        }

        if (skippedFaces > 0)
        {
            warnings.Add($"skipped {skippedFaces} non-triangle face(s) in mesh '{mesh->MName.AsString}'");
        }

        int indexCount = indices.Count - indexOffset;
        if (indexCount > 0)
        {
            subsets.Add(new MeshSubset
            {
                IndexOffset = indexOffset,
                IndexCount = indexCount,
                MaterialIndex = (int)mesh->MMaterialIndex,
            });
        }
    }

    private static unsafe MeshMaterial ConvertMaterial(
        Ai.Assimp assimp,
        Ai.Scene* scene,
        Ai.Material* material,
        AssetImportRequest request,
        List<MeshEmbeddedTexture> embeddedTable,
        List<byte[]> embeddedBytes,
        Dictionary<nint, int> embeddedIndexByTexture,
        List<string> warnings)
    {
        var result = new MeshMaterial();

        Vector4 diffuse = default;
        if (assimp.GetMaterialColor(material, Ai.Assimp.MatkeyColorDiffuse, 0, 0, &diffuse) == Ai.Return.Success)
        {
            result.BaseColor = new Color(diffuse.X, diffuse.Y, diffuse.Z, diffuse.W);
        }

        if (assimp.GetMaterialTextureCount(material, Ai.TextureType.Diffuse) == 0)
        {
            return result;
        }

        Ai.AssimpString path = default;
        if (assimp.GetMaterialTexture(material, Ai.TextureType.Diffuse, 0, &path, null, null, null, null, null, null) != Ai.Return.Success)
        {
            return result;
        }

        string texturePath = path.AsString;
        // Read the embedded texture table directly because some macOS Assimp libraries omit aiGetEmbeddedTexture.
        Ai.Texture* embedded = FindEmbeddedTexture(scene, texturePath);
        if (embedded is not null)
        {
            result.EmbeddedTexture = EncodeEmbedded(embedded, embeddedTable, embeddedBytes, embeddedIndexByTexture, warnings);
        }
        else
        {
            result.Texture = ResolveExternalTexture(request, texturePath, warnings);
        }

        return result;
    }

    private static unsafe Ai.Texture* FindEmbeddedTexture(Ai.Scene* scene, string texturePath)
    {
        if (scene->MTextures is null || scene->MNumTextures == 0)
        {
            return null;
        }

        // Assimp's material convention uses "*<index>" for embedded textures.
        if (texturePath.Length > 1
            && texturePath[0] == '*'
            && uint.TryParse(texturePath.AsSpan(1), out uint index)
            && index < scene->MNumTextures)
        {
            return scene->MTextures[index];
        }

        // Embedded textures may be referenced by filename.
        for (uint i = 0; i < scene->MNumTextures; i++)
        {
            Ai.Texture* texture = scene->MTextures[i];
            if (texture is not null
                && string.Equals(texture->MFilename.AsString, texturePath, StringComparison.Ordinal))
            {
                return texture;
            }
        }

        return null;
    }

    private static unsafe int EncodeEmbedded(
        Ai.Texture* texture,
        List<MeshEmbeddedTexture> embeddedTable,
        List<byte[]> embeddedBytes,
        Dictionary<nint, int> embeddedIndexByTexture,
        List<string> warnings)
    {
        if (embeddedIndexByTexture.TryGetValue((nint)texture, out int existing))
        {
            return existing;
        }

        try
        {
            using Image<Rgba32> image = DecodeEmbedded(texture);
            byte[] ktx2 = TextureEncoding.EncodeBasisUniversal(image);

            long offset = 0;
            foreach (byte[] bytes in embeddedBytes)
            {
                offset += bytes.Length;
            }

            int index = embeddedTable.Count;
            embeddedTable.Add(new MeshEmbeddedTexture
            {
                Width = image.Width,
                Height = image.Height,
                Format = TexturePixelFormat.BasisUniversalKtx2,
                Offset = offset,
                Length = ktx2.Length,
            });
            embeddedBytes.Add(ktx2);
            embeddedIndexByTexture[(nint)texture] = index;
            return index;
        }
        catch (Exception ex)
        {
            warnings.Add($"embedded texture decode failed: {ex.Message}");
            embeddedIndexByTexture[(nint)texture] = -1;
            return -1;
        }
    }

    private static unsafe Image<Rgba32> DecodeEmbedded(Ai.Texture* texture)
    {
        if (texture->MHeight == 0)
        {
            var bytes = new ReadOnlySpan<byte>(texture->PcData, (int)texture->MWidth);
            return Image.Load<Rgba32>(bytes);
        }

        int width = (int)texture->MWidth;
        int height = (int)texture->MHeight;
        var image = new Image<Rgba32>(width, height);
        Ai.Texel* texels = texture->PcData;
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < height; y++)
            {
                Span<Rgba32> row = accessor.GetRowSpan(y);
                for (int x = 0; x < width; x++)
                {
                    Ai.Texel t = texels[(y * width) + x];
                    row[x] = new Rgba32(t.R, t.G, t.B, t.A);
                }
            }
        });
        return image;
    }

    private static AssetReference<TextureAsset> ResolveExternalTexture(
        AssetImportRequest request, string texturePath, List<string> warnings)
    {
        string sourceDir = Path.GetDirectoryName(request.SourcePath)!;
        string candidate = Path.GetFullPath(Path.Combine(sourceDir, texturePath));
        if (!File.Exists(candidate))
        {
            candidate = Path.Combine(sourceDir, Path.GetFileName(texturePath.Replace('\\', '/')));
        }

        string metaPath = candidate + ".meta";
        if (!File.Exists(candidate) || !File.Exists(metaPath))
        {
            warnings.Add($"external texture not found (or no .meta): {texturePath}");
            return new AssetReference<TextureAsset>();
        }

        try
        {
            using JsonDocument meta = JsonDocument.Parse(File.ReadAllText(metaPath));
            string? guid = meta.RootElement.GetProperty("guid").GetString();
            return string.IsNullOrEmpty(guid) ? new AssetReference<TextureAsset>() : new AssetReference<TextureAsset>(guid);
        }
        catch (Exception ex)
        {
            warnings.Add($"failed to read .meta for {texturePath}: {ex.Message}");
            return new AssetReference<TextureAsset>();
        }
    }

    private static unsafe float ReadUnitScale(Ai.Scene* scene)
    {
        Ai.Metadata* metadata = scene->MMetaData;
        if (metadata is null)
        {
            return 1f;
        }

        for (uint i = 0; i < metadata->MNumProperties; i++)
        {
            if (!string.Equals(metadata->MKeys[i].AsString, "UnitScaleFactor", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Ai.MetadataEntry entry = metadata->MValues[i];
            return entry.MType switch
            {
                Ai.MetadataType.Float => *(float*)entry.MData,
                Ai.MetadataType.Double => (float)*(double*)entry.MData,
                Ai.MetadataType.Int32 => *(int*)entry.MData,
                _ => 1f,
            };
        }

        return 1f;
    }

    private static string StagingPath(string tag)
        => Path.Combine(Path.GetTempPath(), $"ee-mesh-{tag}-{Guid.NewGuid():N}.bin");

    private static void WriteFloats(string path, List<float> values)
    {
        using var writer = new BinaryWriter(File.Create(path));
        foreach (float value in values)
        {
            writer.Write(value);
        }
    }

    private static void WriteUints(string path, List<uint> values)
    {
        using var writer = new BinaryWriter(File.Create(path));
        foreach (uint value in values)
        {
            writer.Write(value);
        }
    }
}
