using EmptyEngine.Editor;
using EmptyEngine.Editor.Assets;
using EmptyEngine.Editor.Authoring;
using Xunit;

namespace EmptyEngine.Tests;

/// <summary>ソース実パス → アセットキーの逆引きの検証</summary>
public sealed class AssetCatalogLookupTests
{
    private static AssetCatalog CatalogOf(params ImportedSource[] assets)
    {
        var catalog = new AssetCatalog();
        catalog.Update(assets.ToDictionary(a => a.Key, a => a, StringComparer.Ordinal));
        return catalog;
    }

    private static ImportedAsset Asset(string key, string sourcePath, string localId = "") =>
        new(RelativePath: Path.GetFileName(sourcePath), Value: Probe, SourcePath: sourcePath)
        {
            Key = key,
            LocalId = localId
        };

    private static AuthoringObject Probe =>
        new(TestSchemas.Object("Probe"), new FieldValue());

    [Fact]
    public void ResolvesKeyFromSourcePath()
    {
        AssetCatalog catalog = CatalogOf(
            Asset("guid-a", Path.Combine("C:", "game", "Assets", "hero.png")),
            Asset("guid-b", Path.Combine("C:", "game", "Assets", "villain.png")));

        Assert.True(catalog.TryGetKeyBySourcePath(Path.Combine("C:", "game", "Assets", "villain.png"), out string? key));
        Assert.Equal("guid-b", key);
        Assert.True(catalog.TryGetImportedAsset(key!, out ImportedSource? asset));
        Assert.Equal(Path.Combine("C:", "game", "Assets", "villain.png"), asset!.SourcePath);
    }

    /// <summary>カタログが表示パス順でアセットを返すことを検証する</summary>
    [Fact]
    public void EnumeratesInDisplayPathOrder()
    {
        AssetCatalog catalog = CatalogOf(
            Asset("guid-c", Path.Combine("C:", "game", "Assets", "villain.png")),
            Asset("guid-a", Path.Combine("C:", "game", "Assets", "Hero.png")),
            Asset("guid-b", Path.Combine("C:", "game", "Assets", "map.scene")));

        Assert.Equal(
            ["Hero.png", "map.scene", "villain.png"],
            catalog.EnumerateAssets().Select(e => e.DisplayPath));
    }

    [Fact]
    public void NormalizesSeparatorsAndCase()
    {
        AssetCatalog catalog = CatalogOf(Asset("guid-a", @"C:\game\Assets\sub\hero.png"));

        Assert.True(catalog.TryGetKeyBySourcePath(@"C:\game\Assets\other\..\sub\hero.png", out string? viaDots));
        Assert.Equal("guid-a", viaDots);

        if (OperatingSystem.IsWindows())
        {
            Assert.True(catalog.TryGetKeyBySourcePath(@"c:\GAME\assets\sub\HERO.PNG", out string? viaCase));
            Assert.Equal("guid-a", viaCase);
        }
    }

    [Fact]
    public void PrefersTheAssetWithoutLocalId()
    {
        string source = Path.Combine("C:", "game", "Assets", "atlas.sprite");
        AssetCatalog catalog = CatalogOf(
            Asset("guid-sub-b", source, localId: "b"),
            Asset("guid-main", source),
            Asset("guid-sub-a", source, localId: "a"));

        Assert.True(catalog.TryGetKeyBySourcePath(source, out string? key));
        Assert.Equal("guid-main", key);
    }

    [Fact]
    public void FallsBackToTheFirstSubAssetDeterministically()
    {
        string source = Path.Combine("C:", "game", "Assets", "atlas.sprite");
        AssetCatalog catalog = CatalogOf(
            Asset("guid-sub-b", source, localId: "b"),
            Asset("guid-sub-a", source, localId: "a"));

        Assert.True(catalog.TryGetKeyBySourcePath(source, out string? key));
        Assert.Equal("guid-sub-a", key);
    }

    [Fact]
    public void ReportsMissForUnknownPaths()
    {
        AssetCatalog catalog = CatalogOf(Asset("guid-a", Path.Combine("C:", "game", "Assets", "hero.png")));

        Assert.False(catalog.TryGetKeyBySourcePath(Path.Combine("C:", "game", "Program.cs"), out string? key));
        Assert.Null(key);
        Assert.False(catalog.TryGetKeyBySourcePath("", out _));
    }

    [Fact]
    public void FileNameLookupPrefersTheMainAssetOfOneFile()
    {
        string source = Path.Combine("C:", "game", "Assets", "atlas.sprite");
        AssetCatalog catalog = CatalogOf(
            Asset("guid-sub-a", source, localId: "a"),
            Asset("guid-main", source),
            Asset("other", Path.Combine("C:", "game", "Assets", "hero.png")));

        Assert.True(catalog.TryGetKeyByFileName("atlas.sprite", out string? key));
        Assert.Equal("guid-main", key);
    }

    [Fact]
    public void FileNameLookupRefusesWhenAmbiguous()
    {
        AssetCatalog catalog = CatalogOf(
            Asset("guid-a", Path.Combine("C:", "game", "Assets", "Stage1", "boss.scene")),
            Asset("guid-b", Path.Combine("C:", "game", "Assets", "Stage2", "boss.scene")));

        Assert.False(catalog.TryGetKeyByFileName("boss.scene", out string? key));
        Assert.Null(key);
    }

    [Fact]
    public void FileNameLookupReportsMissForUnknownNames()
    {
        AssetCatalog catalog = CatalogOf(Asset("guid-a", Path.Combine("C:", "game", "Assets", "hero.png")));

        Assert.False(catalog.TryGetKeyByFileName("villain.png", out string? key));
        Assert.Null(key);
        Assert.False(catalog.TryGetKeyByFileName("  ", out _));
    }
}
