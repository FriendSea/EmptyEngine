using EmptyEngine.Modules.Testing;
using System.Numerics;
using EmptyEngine.Core;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Assets;
using EmptyEngine.Graphics.Editor;
using Xunit;

namespace EmptyEngine.Graphics.Tests;

/// <summary><c>.sprite</c> 取り込みと <see cref="SpriteAsset"/> のアーティファクト往復の検証</summary>
public sealed class SpriteImportTests
{
    [Fact]
    public async Task Importer_reads_shared_texture_scale_and_per_sprite_region_pivot_name()
    {
        string dir = CreateTempDir();
        try
        {
            string spritePath = Path.Combine(dir, "Player.sprite");
            await File.WriteAllTextAsync(spritePath, """
                {
                  "texture": "10a866e7061c49908225f9e6fe713f33",
                  "scale": 2.0,
                  "sprites": [
                    {
                      "name": "left",
                      "region": { "x": 0.25, "y": 0.5, "width": 0.5, "height": 0.25 },
                      "pivot": { "x": 0.0, "y": 1.0 },
                      "animationSpeed": 12.0,
                      "frameCount": 4
                    }
                  ]
                }
                """);

            AssetImportResult result = await new SpriteImporter(CatalogStub.Schemas)
                .ImportAsync(new AssetImportRequest(spritePath, "Player.sprite", dir));

            Assert.True(result.Success, result.Message);
            ImportedSource element = Assert.Single(result.Assets);
            Assert.Equal("left", element.LocalId);
            SpriteAsset sprite = await AuthoringTestHelpers.ResolveAsync<SpriteAsset>(
                dir, AuthoringTestHelpers.AssetOf(result));
            Assert.Equal("10a866e7061c49908225f9e6fe713f33", sprite.Texture.AssetKey);
            Assert.Equal(new Rect(0.25f, 0.5f, 0.5f, 0.25f), sprite.Region);
            Assert.Equal(new Vector2(0f, 1f), sprite.Pivot);
            Assert.Equal(2.0f, sprite.Scale);
            Assert.Equal(12.0f, sprite.AnimationSpeed);
            Assert.Equal(4, sprite.FrameCount);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Importer_returns_one_asset_per_sprite_sharing_texture_and_scale()
    {
        string dir = CreateTempDir();
        try
        {
            string spritePath = Path.Combine(dir, "Atlas.sprite");
            await File.WriteAllTextAsync(spritePath, """
                {
                  "texture": "tex",
                  "scale": 0.5,
                  "sprites": [
                    { "name": "left",  "region": { "x": 0.0, "y": 0.0, "width": 0.5, "height": 1.0 } },
                    { "name": "right", "region": { "x": 0.5, "y": 0.0, "width": 0.5, "height": 1.0 } }
                  ]
                }
                """);

            AssetImportResult result = await new SpriteImporter(CatalogStub.Schemas)
                .ImportAsync(new AssetImportRequest(spritePath, "Atlas.sprite", dir));

            Assert.True(result.Success, result.Message);
            Assert.Equal(2, result.Assets.Count);
            Assert.Equal(new[] { "left", "right" }, result.Assets.Select(a => a.LocalId));
            SpriteAsset left = await AuthoringTestHelpers.ResolveAsync<SpriteAsset>(
                dir, AuthoringTestHelpers.AssetOf(result), "left");
            SpriteAsset right = await AuthoringTestHelpers.ResolveAsync<SpriteAsset>(
                dir, AuthoringTestHelpers.AssetOf(result, 1), "right");

            foreach (SpriteAsset s in (SpriteAsset[])[left, right])
            {
                Assert.Equal("tex", s.Texture.AssetKey);
                Assert.Equal(0.5f, s.Scale);
            }

            Assert.Equal(new Rect(0f, 0f, 0.5f, 1f), left.Region);
            Assert.Equal(new Rect(0.5f, 0f, 0.5f, 1f), right.Region);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Importer_rejects_multiple_sprites_without_names()
    {
        string dir = CreateTempDir();
        try
        {
            string spritePath = Path.Combine(dir, "Bad.sprite");
            await File.WriteAllTextAsync(spritePath, """
                { "texture": "a", "sprites": [ { }, { } ] }
                """);

            AssetImportResult result = await new SpriteImporter(CatalogStub.Schemas)
                .ImportAsync(new AssetImportRequest(spritePath, "Bad.sprite", dir));

            Assert.False(result.Success);
            Assert.Empty(result.Assets);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Importer_defaults_region_pivot_scale_and_allows_omitted_name_for_single_sprite()
    {
        string dir = CreateTempDir();
        try
        {
            string spritePath = Path.Combine(dir, "Bare.sprite");
            await File.WriteAllTextAsync(spritePath, """{ "texture": "tex-key", "sprites": [ { } ] }""");

            AssetImportResult result = await new SpriteImporter(CatalogStub.Schemas)
                .ImportAsync(new AssetImportRequest(spritePath, "Bare.sprite", dir));

            ImportedSource element = Assert.Single(result.Assets);
            Assert.Equal(string.Empty, element.LocalId);
            SpriteAsset sprite = await AuthoringTestHelpers.ResolveAsync<SpriteAsset>(
                dir, AuthoringTestHelpers.AssetOf(result));
            Assert.Equal("tex-key", sprite.Texture.AssetKey);
            Assert.Equal(Rect.Full, sprite.Region);
            Assert.Equal(new Vector2(0.5f, 0.5f), sprite.Pivot);
            Assert.Equal(1f, sprite.Scale);
            Assert.Equal(0f, sprite.AnimationSpeed);
            Assert.Equal(1, sprite.FrameCount);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Catalog_display_path_distinguishes_sub_sprites_in_same_file()
    {
        string dir = CreateTempDir();
        try
        {
            const string sourceGuid = "0123456789abcdef0123456789abcdef";
            string spritePath = Path.Combine(dir, "Hero.sprite");
            await File.WriteAllTextAsync(spritePath, """
                {
                  "texture": "tex",
                  "sprites": [
                    { "name": "Idle", "region": { "x": 0.0, "y": 0.0, "width": 0.5, "height": 1.0 } },
                    { "name": "Dash", "region": { "x": 0.5, "y": 0.0, "width": 0.5, "height": 1.0 } }
                  ]
                }
                """);
            await File.WriteAllTextAsync(spritePath + ".meta", $$"""{"guid":"{{sourceGuid}}"}""");

            var catalog = new AssetCatalog();
            var service = new AssetImportService(catalog, dir, new IAssetImporter[] { new SpriteImporter(CatalogStub.Schemas) });
            await service.ImportAllAsync();

            var entries = catalog.EnumerateAssets().ToList();
            Assert.Equal(2, entries.Count);

            foreach (CatalogEntry entry in entries)
            {
                Assert.True(catalog.TryGetDisplayPath(entry.Key.Value, out string? displayPath));
                Assert.Equal(entry.DisplayPath, displayPath);
            }

            var displayPaths = entries.Select(e => e.DisplayPath).OrderBy(p => p).ToArray();
            Assert.Equal(new[] { "Hero.sprite/Dash", "Hero.sprite/Idle" }, displayPaths);

            var keys = entries.Select(e => e.Key.Value).OrderBy(k => k).ToArray();
            Assert.Equal(new[] { sourceGuid + "/Dash", sourceGuid + "/Idle" }, keys);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task OnResolveAssets_resolves_texture_into_ResolvedTexture()
    {
        var texture = new TextureAsset { Width = 4, Height = 4, Format = TexturePixelFormat.Rgba8Unorm };
        var sprite = new SpriteAsset { Texture = new AssetReference<TextureAsset>("tex-key") };

        Assert.Null(sprite.ResolvedTexture);
        await sprite.OnResolveAssetsAsync(new StubTextureResolver("tex-key", texture));

        Assert.Same(texture, sprite.ResolvedTexture);
    }

    [Fact]
    public async Task OnResolveAssets_leaves_ResolvedTexture_null_when_texture_unset()
    {
        var sprite = new SpriteAsset();

        await sprite.OnResolveAssetsAsync(new StubTextureResolver("tex-key", new TextureAsset()));

        Assert.Null(sprite.ResolvedTexture);
    }

    private static string CreateTempDir()
    {
        string path = Path.Combine(Path.GetTempPath(), "ee-sprite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>指定キーにだけ 1 枚のテクスチャを返す最小リゾルバ</summary>
    private sealed class StubTextureResolver(string key, TextureAsset texture) : IAssetResolver
    {
        public ValueTask<TAsset?> ResolveAsync<TAsset>(
            AssetReference<TAsset> assetReference, CancellationToken cancellationToken = default) where TAsset : class
            => new(TryResolve(assetReference, out TAsset? asset) ? asset : null);

        public bool TryResolve<TAsset>(AssetReference<TAsset> assetReference, out TAsset? asset)
            where TAsset : class
        {
            if (assetReference.AssetKey == key && texture is TAsset typed)
            {
                asset = typed;
                return true;
            }
            asset = null;
            return false;
        }
    }
}
