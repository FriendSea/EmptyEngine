using System.Text.Json;
using System.Text.Json.Nodes;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Authoring;

namespace EmptyEngine.SceneSource.Editor;

/// <summary><c>.asset</c>（JSON）を任意の型のアセットとして取り込む汎用インポーター</summary>
public sealed class TypedAssetImporter(ISchemaSource schemas) : IAssetImporter
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private const string ArtifactsDirName = ".artifacts";
    private const string AssetSchemaFileName = "asset.schema.json";

    public IReadOnlyCollection<string> SupportedExtensions => [".asset"];

    public bool IsSaveSupported => true;

    /// <summary>編集済みアセットの <c>.asset</c> 形への書き戻し</summary>
    public async Task SaveAsync(AuthoringObject asset, string sourcePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);

        var root = new JsonObject();
        if (ResolveSchemaReference(sourcePath) is { } schema)
            root["$schema"] = schema;
        root["TypeName"] = asset.TypeName;
        root["Data"] = FieldJson.Encode(asset.Data, asset.Schema.Root);

        await File.WriteAllTextAsync(sourcePath, root.ToJsonString(WriteOptions), cancellationToken);
    }

    /// <summary>このソースから見た <c>.asset</c> 用スキーマへの相対パス</summary>
    private static string? ResolveSchemaReference(string sourcePath)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(sourcePath));
        if (directory is null)
            return null;

        for (DirectoryInfo? dir = new(directory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, ArtifactsDirName, AssetSchemaFileName);
            if (File.Exists(candidate))
                return Path.GetRelativePath(directory, candidate).Replace(Path.DirectorySeparatorChar, '/');
        }

        return ExistingSchemaReference(sourcePath);
    }

    private static string? ExistingSchemaReference(string sourcePath)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(sourcePath));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("$schema", out JsonElement schema)
                && schema.ValueKind == JsonValueKind.String
                    ? schema.GetString()
                    : null;
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return null;
        }
    }

    public async Task<AssetImportResult> ImportAsync(AssetImportRequest request, CancellationToken cancellationToken = default)
    {
        string json = await File.ReadAllTextAsync(request.SourcePath, cancellationToken);

        string typeName;
        ObjectSchema schema;
        FieldValue? data;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("TypeName", out JsonElement typeNameEl)
                || typeNameEl.ValueKind != JsonValueKind.String)
                return AssetImportResult.Failed($"Missing 'TypeName' in .asset source: {request.RelativePath}");

            typeName = typeNameEl.GetString() ?? string.Empty;
            schema = schemas.Get(typeName);
            data = root.TryGetProperty("Data", out JsonElement d) && d.ValueKind == JsonValueKind.Object
                ? FieldJson.Decode(d, schema.Root)
                : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return AssetImportResult.Failed($"Invalid .asset JSON ({request.RelativePath}): {ex.Message}");
        }


        FieldValue defaults = schemas.CreateDefault(typeName)?.Data ?? new FieldValue();
        FieldValue merged = schema.Root.Overlay(defaults, data);

        var value = new AuthoringObject(schema, merged);

        var asset = new ImportedAsset(request.RelativePath, value, request.SourcePath);
        return AssetImportResult.Succeeded($"Imported {typeName} from {request.RelativePath}", asset);
    }
}
