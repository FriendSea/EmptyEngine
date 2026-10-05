using System.Collections.Concurrent;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Assets;
using EmptyEngine.Editor.Authoring;

namespace EmptyEngine.SceneSource.Editor;

/// <summary>プレハブバリアント（<c>.variant</c>）のシーンインポーター</summary>
/// <remarks>オリジナルは <c>.meta</c> の guid で参照する</remarks>
public sealed class PrefabVariantImporter(ISchemaSource schemas, AssetCatalog sources) : IVariantImporter
{
    private readonly ConcurrentDictionary<string, string> _originalPathByVariant = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> SupportedExtensions => [".variant"];

    public async Task CreateAsync(string originalKey, string filePath, CancellationToken cancellationToken = default)
    {
        string json = PrefabVariantCodec.ToJson(
            new PrefabVariantCodec.VariantData(originalKey, Array.Empty<PrefabVariantCodec.FieldOverride>()));
        await File.WriteAllTextAsync(filePath, json, cancellationToken);
    }

    public async Task<AssetImportResult> ImportAsync(AssetImportRequest request, CancellationToken cancellationToken = default)
    {
        string json = await File.ReadAllTextAsync(request.SourcePath, cancellationToken);
        PrefabVariantCodec.VariantData variant = PrefabVariantCodec.FromJson(json, schemas);

        if (sources.FindSourcePath(variant.Original) is not { } originalPath)
            return AssetImportResult.Failed(
                $"Cannot resolve original prefab '{variant.Original}' for variant {request.RelativePath}");

        _originalPathByVariant[Path.GetFullPath(request.SourcePath)] = originalPath;

        HierarchyNode original = SceneJsonCodec.FromJson(
            await File.ReadAllTextAsync(originalPath, cancellationToken), schemas);
        HierarchyNode merged = PrefabVariantCodec.Merge(
            original, schemas, variant.Overrides, variant.AddedComponents, variant.AddedChildren);

        var asset = new ImportedScene(request.RelativePath, merged, request.SourcePath);
        return AssetImportResult.Succeeded($"Imported variant {request.RelativePath} of {variant.Original}", asset) with
        {
            Dependencies = [variant.Original],
        };
    }

    public async Task SaveAsync(HierarchyNode scene, string filePath, CancellationToken cancellationToken = default)
    {
        string originalRef = File.Exists(filePath)
            ? PrefabVariantCodec.FromJson(await File.ReadAllTextAsync(filePath, cancellationToken), schemas).Original
            : string.Empty;

        if (!_originalPathByVariant.TryGetValue(Path.GetFullPath(filePath), out string? originalPath))
            originalPath = sources.FindSourcePath(originalRef);

        IReadOnlyList<PrefabVariantCodec.FieldOverride> overrides;
        IReadOnlyList<PrefabVariantCodec.ComponentAddition> addedComponents;
        IReadOnlyList<PrefabVariantCodec.ChildAddition> addedChildren;
        if (originalPath is not null && File.Exists(originalPath))
        {
            HierarchyNode original = SceneJsonCodec.FromJson(
                await File.ReadAllTextAsync(originalPath, cancellationToken), schemas);
            overrides = PrefabVariantCodec.Diff(original, scene);
            addedComponents = PrefabVariantCodec.DiffAddedComponents(original, scene);
            addedChildren = PrefabVariantCodec.DiffAddedChildren(original, scene);
        }
        else
        {
            overrides = Array.Empty<PrefabVariantCodec.FieldOverride>();
            addedComponents = Array.Empty<PrefabVariantCodec.ComponentAddition>();
            addedChildren = Array.Empty<PrefabVariantCodec.ChildAddition>();
        }

        string json = PrefabVariantCodec.ToJson(
            new PrefabVariantCodec.VariantData(originalRef, overrides, addedComponents, addedChildren));
        await File.WriteAllTextAsync(filePath, json, cancellationToken);
    }
}
