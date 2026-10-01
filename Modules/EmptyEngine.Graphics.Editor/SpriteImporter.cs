using System.Numerics;
using System.Text.Json;
using EmptyEngine.Core;
using EmptyEngine.Editor.Assets;
using EmptyEngine.Editor.Authoring;
using EmptyEngine.Editor;

namespace EmptyEngine.Graphics.Editor;

/// <summary><c>.sprite</c>（JSON）の <see cref="SpriteAsset"/> としての取り込み</summary>
/// <remarks><c>texture</c>・<c>scale</c> は全スプライト共通でトップレベルに置き、切り出す矩形と中心だけを <c>sprites</c> 配列に並べる</remarks>
public sealed class SpriteImporter(ISchemaSource schemas) : IAssetImporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public IReadOnlyCollection<string> SupportedExtensions => [".sprite"];

    public async Task<AssetImportResult> ImportAsync(AssetImportRequest request, CancellationToken cancellationToken = default)
    {
        await using FileStream stream = File.OpenRead(request.SourcePath);
        SpriteSheet? sheet;
        try
        {
            sheet = await JsonSerializer.DeserializeAsync<SpriteSheet>(stream, JsonOptions, cancellationToken);
        }
        catch (JsonException ex)
        {
            return AssetImportResult.Failed($"Invalid .sprite JSON ({request.RelativePath}): {ex.Message}");
        }

        if (sheet?.Sprites is not { Count: > 0 } sprites)
        {
            return AssetImportResult.Failed($"Empty .sprite source (no sprites): {request.RelativePath}");
        }

        var texture = new AssetReference<TextureAsset>(sheet.Texture ?? string.Empty);
        float scale = sheet.Scale ?? 1f;

        var assets = new List<ImportedAsset>(sprites.Count);
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (SpriteEntry sprite in sprites)
        {
            string name = sprite.Name ?? string.Empty;
            if (sprites.Count > 1 && string.IsNullOrWhiteSpace(name))
                return AssetImportResult.Failed($"Sprite 'name' is required when a .sprite has multiple sprites: {request.RelativePath}");
            if (!seenNames.Add(name))
                return AssetImportResult.Failed($"Duplicate sprite name '{name}' in {request.RelativePath}");

            var asset = new SpriteAsset
            {
                Texture = texture,
                Scale = scale,
                Region = sprite.Region is { } r ? new Rect(r.X, r.Y, r.Width, r.Height) : Rect.Full,
                Pivot = sprite.Pivot is { } p ? new Vector2(p.X, p.Y) : new Vector2(0.5f, 0.5f),
                AnimationSpeed = sprite.AnimationSpeed ?? 0f,
                FrameCount = sprite.FrameCount ?? 1,
            };

            assets.Add(new ImportedAsset(request.RelativePath, ImporterUtils.FromClr(asset, schemas), request.SourcePath)
            {
                LocalId = name,
            });
        }

        return AssetImportResult.Succeeded(
            $"Imported {assets.Count} sprite(s) {request.RelativePath}",
            assets);
    }

    private sealed record SpriteSheet(string? Texture, float? Scale, List<SpriteEntry>? Sprites);

    private sealed record SpriteEntry(string? Name, RectSource? Region, PointSource? Pivot, float? AnimationSpeed, int? FrameCount);

    private sealed record RectSource(float X, float Y, float Width, float Height);

    private sealed record PointSource(float X, float Y);
}
