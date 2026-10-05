using EmptyEngine.Modules.Testing;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Assets;
using EmptyEngine.SceneSource.Editor;
using EmptyEngine.Storage;
using Xunit;

namespace EmptyEngine.SceneSource.Tests;

/// <summary>エクスプローラでソースを移動されたときの guid 追跡の検証</summary>
public sealed class ExplorerMoveRecoveryTests : IDisposable
{
    private readonly string _assetsRoot = Path.Combine(Path.GetTempPath(), "ee-explorermove-" + Guid.NewGuid().ToString("N"));

    public ExplorerMoveRecoveryTests() => Directory.CreateDirectory(_assetsRoot);

    public void Dispose()
    {
        try { Directory.Delete(_assetsRoot, recursive: true); } catch { }
    }

    [Fact]
    public async Task Source_moved_without_its_meta_keeps_its_guid()
    {
        (AssetCatalog firstCatalog, AssetImportService first) = CreateService();
        string path = WriteScene("Thing.scene");
        await first.ImportAllAsync();
        string key = SingleEntry(firstCatalog).Key.Value;

        Directory.CreateDirectory(Path.Combine(_assetsRoot, "Moved"));
        string moved = Path.Combine(_assetsRoot, "Moved", "Thing.scene");
        File.Move(path, moved);

        (AssetCatalog secondCatalog, AssetImportService second) = CreateService();
        IReadOnlyList<AssetImportResult> results = await second.ImportIfChangedAsync();

        Assert.Empty(results);
        Assert.Equal(key, SingleEntry(secondCatalog).Key.Value);
        Assert.Equal("Moved/Thing.scene", SingleEntry(secondCatalog).DisplayPath);
        Assert.True(File.Exists(moved + ".meta"));
        Assert.False(File.Exists(path + ".meta"));
    }

    [Fact]
    public async Task Source_renamed_without_its_meta_keeps_its_guid()
    {
        (AssetCatalog firstCatalog, AssetImportService first) = CreateService();
        string path = WriteScene("Thing.scene");
        await first.ImportAllAsync();
        string key = SingleEntry(firstCatalog).Key.Value;

        string renamed = Path.Combine(_assetsRoot, "Player.scene");
        File.Move(path, renamed);

        (AssetCatalog secondCatalog, AssetImportService second) = CreateService();
        await second.ImportIfChangedAsync();

        Assert.Equal(key, SingleEntry(secondCatalog).Key.Value);
        Assert.Equal("Player.scene", SingleEntry(secondCatalog).DisplayPath);
        Assert.True(File.Exists(renamed + ".meta"));
    }

    [Fact]
    public async Task Deleted_meta_is_restored_with_the_original_guid()
    {
        (AssetCatalog firstCatalog, AssetImportService first) = CreateService();
        string path = WriteScene("Thing.scene");
        await first.ImportAllAsync();
        string key = SingleEntry(firstCatalog).Key.Value;

        File.Delete(path + ".meta");

        (AssetCatalog secondCatalog, AssetImportService second) = CreateService();
        await second.ImportIfChangedAsync();

        Assert.True(File.Exists(path + ".meta"));
        Assert.Equal(key, SingleEntry(secondCatalog).Key.Value);
    }

    [Fact]
    public async Task Copy_does_not_steal_the_guid_of_the_living_original()
    {
        (AssetCatalog firstCatalog, AssetImportService first) = CreateService();
        string path = WriteScene("Thing.scene");
        await first.ImportAllAsync();
        string key = SingleEntry(firstCatalog).Key.Value;

        string copy = Path.Combine(_assetsRoot, "Thing (copy).scene");
        File.Copy(path, copy);

        (AssetCatalog secondCatalog, AssetImportService second) = CreateService();
        await second.ImportIfChangedAsync();

        var keys = secondCatalog.EnumerateAssets().ToDictionary(e => e.DisplayPath, e => e.Key.Value);
        Assert.Equal(key, keys["Thing.scene"]);
        Assert.NotEqual(key, keys["Thing (copy).scene"]);
    }

    [Fact]
    public async Task Move_is_recovered_by_signature_when_no_file_identity_is_available()
    {
        (AssetCatalog firstCatalog, AssetImportService first) = CreateService(NoFileIdentity.Instance);
        string path = WriteScene("Thing.scene");
        await first.ImportAllAsync();
        string key = SingleEntry(firstCatalog).Key.Value;

        Directory.CreateDirectory(Path.Combine(_assetsRoot, "Moved"));
        string moved = Path.Combine(_assetsRoot, "Moved", "Thing.scene");
        File.Move(path, moved);

        (AssetCatalog secondCatalog, AssetImportService second) = CreateService(NoFileIdentity.Instance);
        await second.ImportIfChangedAsync();

        Assert.Equal(key, SingleEntry(secondCatalog).Key.Value);
        Assert.Equal("Moved/Thing.scene", SingleEntry(secondCatalog).DisplayPath);
    }

    [Fact]
    public async Task Ambiguous_signatures_get_new_guids_instead_of_a_wrong_one()
    {
        (AssetCatalog firstCatalog, AssetImportService first) = CreateService(NoFileIdentity.Instance);
        string a = WriteScene("A.scene", objectId: "same");
        string b = WriteScene("B.scene", objectId: "same");
        Assert.Equal(new FileInfo(a).Length, new FileInfo(b).Length);
        await first.ImportAllAsync();
        var originalKeys = firstCatalog.EnumerateAssets().Select(e => e.Key.Value).ToHashSet();

        Directory.CreateDirectory(Path.Combine(_assetsRoot, "Moved"));
        File.Move(a, Path.Combine(_assetsRoot, "Moved", "A.scene"));
        File.Move(b, Path.Combine(_assetsRoot, "Moved", "B.scene"));

        (AssetCatalog secondCatalog, AssetImportService second) = CreateService(NoFileIdentity.Instance);
        await second.ImportIfChangedAsync();

        var newKeys = secondCatalog.EnumerateAssets().Select(e => e.Key.Value).ToHashSet();
        Assert.Equal(2, newKeys.Count);
        Assert.Empty(newKeys.Intersect(originalKeys));
    }

    [Fact]
    public async Task Ambiguous_signatures_are_still_resolved_by_file_identity()
    {
        if (!OperatingSystem.IsWindows()) return;

        (AssetCatalog firstCatalog, AssetImportService first) = CreateService();
        string a = WriteScene("A.scene", objectId: "same");
        string b = WriteScene("B.scene", objectId: "same");
        await first.ImportAllAsync();
        var before = firstCatalog.EnumerateAssets().ToDictionary(e => e.DisplayPath, e => e.Key.Value);

        Directory.CreateDirectory(Path.Combine(_assetsRoot, "Moved"));
        File.Move(a, Path.Combine(_assetsRoot, "Moved", "A.scene"));
        File.Move(b, Path.Combine(_assetsRoot, "Moved", "B.scene"));

        (AssetCatalog secondCatalog, AssetImportService second) = CreateService();
        await second.ImportIfChangedAsync();

        var after = secondCatalog.EnumerateAssets().ToDictionary(e => e.DisplayPath, e => e.Key.Value);
        Assert.Equal(before["A.scene"], after["Moved/A.scene"]);
        Assert.Equal(before["B.scene"], after["Moved/B.scene"]);
    }

    [Fact]
    public async Task Full_reimport_restores_tracking_for_stamps_without_identity()
    {
        (AssetCatalog firstCatalog, AssetImportService first) = CreateService();
        string path = WriteScene("Thing.scene");
        await first.ImportAllAsync();
        string key = SingleEntry(firstCatalog).Key.Value;
        Assert.True(AnyStampHasIdentity());
        StripIdentityLines();

        (_, AssetImportService second) = CreateService();
        Assert.Empty(await second.ImportIfChangedAsync());
        Assert.False(AnyStampHasIdentity());

        await second.ImportAllAsync();
        Assert.True(AnyStampHasIdentity());

        Directory.CreateDirectory(Path.Combine(_assetsRoot, "Moved"));
        string moved = Path.Combine(_assetsRoot, "Moved", "Thing.scene");
        File.Move(path, moved);

        (AssetCatalog thirdCatalog, AssetImportService third) = CreateService();
        await third.ImportIfChangedAsync();

        Assert.Equal(key, SingleEntry(thirdCatalog).Key.Value);
        Assert.Equal("Moved/Thing.scene", SingleEntry(thirdCatalog).DisplayPath);
    }

    [Fact]
    public async Task Brand_new_source_still_gets_a_fresh_guid()
    {
        (AssetCatalog firstCatalog, AssetImportService first) = CreateService();
        WriteScene("Thing.scene");
        await first.ImportAllAsync();
        string key = SingleEntry(firstCatalog).Key.Value;

        WriteScene("Other.scene", objectId: "other");

        (AssetCatalog secondCatalog, AssetImportService second) = CreateService();
        await second.ImportIfChangedAsync();

        var keys = secondCatalog.EnumerateAssets().ToDictionary(e => e.DisplayPath, e => e.Key.Value);
        Assert.Equal(key, keys["Thing.scene"]);
        Assert.NotEqual(key, keys["Other.scene"]);
    }

    [Fact]
    public async Task Deleted_source_removes_artifact_stamp_meta_and_catalog_entry()
    {
        (AssetCatalog catalog, AssetImportService service) = CreateService();
        string path = WriteScene("Thing.scene");
        await service.ImportAllAsync();
        string key = SingleEntry(catalog).Key.Value;
        string artifact = Path.Combine(_assetsRoot, ".artifacts", "store", ImportedAssetsLayout.ArtifactPath(key));
        string stamp = Path.Combine(StampRoot, key + ".stamp");
        var changed = new List<string>();
        service.ArtifactsChanged += keys => changed.AddRange(keys);

        Assert.True(File.Exists(artifact));
        Assert.True(File.Exists(stamp));
        Assert.True(File.Exists(path + ".meta"));

        File.Delete(path);
        IReadOnlyList<AssetImportResult> results = await service.ImportIfChangedAsync();

        Assert.Empty(results);
        Assert.Empty(catalog.EnumerateAssets());
        Assert.False(File.Exists(artifact));
        Assert.False(File.Exists(stamp));
        Assert.False(File.Exists(path + ".meta"));
        Assert.Equal(new[] { key }, changed);
    }

    /// <summary>ファイルの固有 ID を取得できない環境を表す。</summary>
    private sealed class NoFileIdentity : IFileIdentity
    {
        public static readonly NoFileIdentity Instance = new();

        public string? TryGet(string fullPath) => null;
    }

    private string StampRoot => Path.Combine(_assetsRoot, ".artifacts", "import-stamps");

    private bool AnyStampHasIdentity() =>
        Directory.EnumerateFiles(StampRoot, "*.stamp")
            .Any(s => File.ReadLines(s).Any(l => l.StartsWith("id:", StringComparison.Ordinal)));

    private void StripIdentityLines()
    {
        foreach (string stamp in Directory.EnumerateFiles(StampRoot, "*.stamp"))
        {
            File.WriteAllLines(stamp,
                File.ReadAllLines(stamp).Where(l => !l.StartsWith("id:", StringComparison.Ordinal)));
        }
    }

    private (AssetCatalog Catalog, AssetImportService Service) CreateService(IFileIdentity? fileIdentity = null)
    {
        string artifacts = Path.Combine(_assetsRoot, ".artifacts");
        var catalog = new AssetCatalog();
        var service = new AssetImportService(catalog, new ProjectAssetLayout(_assetsRoot).Sources, new IAssetImporter[] { new SceneAssetImporter(CatalogStub.Schemas, catalog) }, Path.Combine(artifacts, "import-stamps"), fileIdentity: fileIdentity);
        service.SetArtifacts(TestArtifacts.At(Path.Combine(artifacts, "store")));
        return (catalog, service);
    }

    private string WriteScene(string relativePath, string objectId = "obj-root")
    {
        string path = Path.Combine(_assetsRoot, relativePath);
        File.WriteAllText(path, SceneJsonCodec.ToJson(new HierarchyNode(objectId, "Thing")));
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        return path;
    }

    private static CatalogEntry SingleEntry(AssetCatalog catalog) =>
        Assert.Single(catalog.EnumerateAssets());
}
