using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Authoring;

namespace EmptyEngine.SceneSource.Editor;

/// <summary><c>.scene</c> のシーンインポーター</summary>
public sealed class SceneAssetImporter(ISchemaSource schemas) : INestedPrefabImporter
{
    private sealed record NestedSource(string Guid, string SourcePath);

    private readonly ConcurrentDictionary<string, NestedSource> _nestedByLeafId = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> SupportedExtensions => [".scene"];

    public async Task<AssetImportResult> ImportAsync(AssetImportRequest request, CancellationToken cancellationToken = default)
    {
        string json = await File.ReadAllTextAsync(request.SourcePath, cancellationToken);

        HierarchyNode root;
        var dependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            root = ReadBaked(json, request.AssetsRootPath, request.SourcePath,
                activeGuids: new HashSet<string>(StringComparer.OrdinalIgnoreCase), dependencies);
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException)
        {
            return AssetImportResult.Failed($"Cannot import scene {request.RelativePath}: {ex.Message}");
        }

        var asset = new ImportedScene(request.RelativePath, root, request.SourcePath);
        return AssetImportResult.Succeeded($"Imported scene {request.RelativePath}", asset) with
        {
            Dependencies = dependencies.ToArray(),
        };
    }

    public async Task SaveAsync(HierarchyNode scene, string filePath, CancellationToken cancellationToken = default)
    {
        await SeedNestedTableFromSourceAsync(filePath, cancellationToken);

        string json = SceneJsonCodec.ToJson(scene, child => TryFoldNested(child, filePath));
        await File.WriteAllTextAsync(filePath, json, cancellationToken);
    }

    private async Task SeedNestedTableFromSourceAsync(string filePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath)) return;
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                await File.ReadAllTextAsync(filePath, cancellationToken));
            SeedLeaves(document.RootElement);
        }
        catch (JsonException e)
        {
            Console.Error.WriteLine(
                $"[SceneAssetImporter] Cannot read '{filePath}'; skipping carry-over of nested prefabs"
                + $" (saving will no longer collapse nested prefabs into leaves): {e.Message}");
        }
    }

    private void SeedLeaves(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return;

        if (NestedPrefabCodec.IsLeaf(element))
        {
            NestedPrefabCodec.PrefabLeaf leaf = NestedPrefabCodec.ReadLeaf(element, schemas);
            if (!string.IsNullOrWhiteSpace(leaf.Id) && !string.IsNullOrWhiteSpace(leaf.SourceGuid))
                _nestedByLeafId.TryAdd(leaf.Id, new NestedSource(leaf.SourceGuid, SourcePath: string.Empty));
            return;
        }

        if (element.TryGetProperty("Children", out JsonElement children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in children.EnumerateArray())
                SeedLeaves(child);
        }
    }

    public Task<HierarchyNode> InstantiateNestedAsync(string sourceKey, string sourcePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HierarchyNode baked = ReadSourceBaked(sourcePath, preferredRoot: null,
            activeGuids: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { sourceKey });

        string leafId = Guid.NewGuid().ToString("N");
        HierarchyNode remapped = NestedPrefabCodec.Remap(baked, leafId);
        _nestedByLeafId[leafId] = new NestedSource(sourceKey, sourcePath);

        return Task.FromResult(remapped);
    }

    public bool IsNestedInstanceRoot(string objectId, string? parentObjectId) =>
        NestedPrefabCodec.IsInstanceRoot(objectId, parentObjectId);

    private HierarchyNode ReadBaked(string json, string? preferredRoot, string referencingPath, HashSet<string> activeGuids, ISet<string>? dependencies = null)
    {
        return SceneJsonCodec.FromJson(
            json, schemas, leaf => BakeLeaf(leaf, preferredRoot, referencingPath, activeGuids, dependencies));
    }

    private HierarchyNode BakeLeaf(NestedPrefabCodec.PrefabLeaf leaf, string? preferredRoot, string referencingPath, HashSet<string> activeGuids, ISet<string>? dependencies)
    {
        if (string.IsNullOrWhiteSpace(leaf.Id) || string.IsNullOrWhiteSpace(leaf.SourceGuid))
            throw new FormatException("Nested prefab leaf must have both 'Id' and 'Prefab'.");
        if (!activeGuids.Add(leaf.SourceGuid))
            throw new FormatException($"Nested prefab cycle detected at '{leaf.SourceGuid}'.");

        try
        {
            if (!MetaGuidLocator.TryResolve(preferredRoot, referencingPath, leaf.SourceGuid, out string? sourcePath) || sourcePath is null)
                throw new FormatException($"Cannot resolve nested prefab '{leaf.SourceGuid}'.");

            dependencies?.Add(MetaGuidLocator.TryReadGuidOf(sourcePath) ?? leaf.SourceGuid);

            HierarchyNode baked = ReadSourceBaked(sourcePath, preferredRoot, activeGuids, dependencies);
            HierarchyNode remapped = NestedPrefabCodec.Remap(baked, leaf.Id);
            HierarchyNode merged = PrefabVariantCodec.Merge(
                remapped, schemas, leaf.Overrides, leaf.AddedComponents, leaf.AddedChildren);

            _nestedByLeafId[leaf.Id] = new NestedSource(leaf.SourceGuid, sourcePath);
            return merged;
        }
        finally
        {
            activeGuids.Remove(leaf.SourceGuid);
        }
    }

    private JsonObject? TryFoldNested(HierarchyNode child, string hostFilePath)
    {
        if (NestedPrefabCodec.LeafIdOf(child.ObjectId) is not { } leafId)
            return null;
        if (!_nestedByLeafId.TryGetValue(leafId, out NestedSource? source))
            return null;

        string? sourcePath = source.SourcePath;
        if (!File.Exists(sourcePath))
        {
            if (!MetaGuidLocator.TryResolve(preferredRoot: null, hostFilePath, source.Guid, out sourcePath) || sourcePath is null)
            {
                return NestedPrefabCodec.WriteLeaf(new NestedPrefabCodec.PrefabLeaf(
                    leafId,
                    source.Guid,
                    Array.Empty<PrefabVariantCodec.FieldOverride>(),
                    Array.Empty<PrefabVariantCodec.ComponentAddition>(),
                    Array.Empty<PrefabVariantCodec.ChildAddition>()));
            }
            _nestedByLeafId[leafId] = source with { SourcePath = sourcePath };
        }

        HierarchyNode baked = ReadSourceBaked(sourcePath, preferredRoot: null,
            activeGuids: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { source.Guid });
        HierarchyNode remapped = NestedPrefabCodec.Remap(baked, leafId);
        IReadOnlyList<PrefabVariantCodec.FieldOverride> overrides = PrefabVariantCodec.Diff(remapped, child);
        IReadOnlyList<PrefabVariantCodec.ComponentAddition> addedComponents =
            PrefabVariantCodec.DiffAddedComponents(remapped, child);
        IReadOnlyList<PrefabVariantCodec.ChildAddition> addedChildren =
            PrefabVariantCodec.DiffAddedChildren(remapped, child);

        return NestedPrefabCodec.WriteLeaf(
            new NestedPrefabCodec.PrefabLeaf(
                leafId, source.Guid, overrides, addedComponents, addedChildren));
    }

    private HierarchyNode ReadSourceBaked(
        string sourcePath,
        string? preferredRoot,
        HashSet<string> activeGuids,
        ISet<string>? dependencies = null)
    {
        string json = File.ReadAllText(sourcePath);
        if (!string.Equals(Path.GetExtension(sourcePath), ".variant", StringComparison.OrdinalIgnoreCase))
            return ReadBaked(json, preferredRoot, sourcePath, activeGuids, dependencies);

        PrefabVariantCodec.VariantData variant = PrefabVariantCodec.FromJson(json, schemas);
        if (string.IsNullOrWhiteSpace(variant.Original))
            throw new FormatException($"Variant '{sourcePath}' has no original prefab.");
        if (!activeGuids.Add(variant.Original))
            throw new FormatException($"Nested prefab cycle detected at '{variant.Original}'.");

        try
        {
            if (!MetaGuidLocator.TryResolve(preferredRoot, sourcePath, variant.Original, out string? originalPath) ||
                originalPath is null)
                throw new FormatException($"Cannot resolve original prefab '{variant.Original}' for variant '{sourcePath}'.");

            dependencies?.Add(MetaGuidLocator.TryReadGuidOf(originalPath) ?? variant.Original);
            HierarchyNode original = ReadSourceBaked(originalPath, preferredRoot, activeGuids, dependencies);
            return PrefabVariantCodec.Merge(
                original, schemas, variant.Overrides, variant.AddedComponents, variant.AddedChildren);
        }
        finally
        {
            activeGuids.Remove(variant.Original);
        }
    }
}
