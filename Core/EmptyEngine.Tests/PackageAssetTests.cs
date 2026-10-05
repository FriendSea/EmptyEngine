using EmptyEngine.Core;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Assets;
using EmptyEngine.Editor.Authoring;
using Xunit;

namespace EmptyEngine.Tests;

/// <summary>参照先のパッケージが同梱するソースアセットの取り込み</summary>
public sealed class PackageAssetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ee-package-" + Guid.NewGuid().ToString("N"));
    private readonly string _assetsRoot;
    private readonly string _packageRoot;

    public PackageAssetTests()
    {
        _assetsRoot = Path.Combine(_root, "Assets");
        _packageRoot = Path.Combine(_root, "packages", "probe.pack", "1.0.0", "assets", "Probe.Pack");
        Directory.CreateDirectory(_assetsRoot);
        Directory.CreateDirectory(Path.Combine(_packageRoot, "Sub"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>パッケージのアセットは Packages/名前/ の下に並び、プロジェクトのものと一緒に引ける。</summary>
    [Fact]
    public async Task Package_assets_are_listed_under_the_package_name()
    {
        File.WriteAllText(Path.Combine(_assetsRoot, "Own.probe"), "own");
        WriteShipped(Path.Combine("Sub", "Shipped.probe"), "shipped");

        var catalog = new AssetCatalog();
        await CreateService(catalog).ImportAllAsync();

        Assert.Equal(
            ["Own.probe", "Packages/Probe.Pack/Sub/Shipped.probe"],
            catalog.EnumerateAssets().Select(entry => entry.DisplayPath));
    }

    /// <summary>パッケージへは .meta を書かず、.meta の無いソースは取り込まない。</summary>
    [Fact]
    public async Task Package_assets_without_meta_are_skipped()
    {
        string shipped = Path.Combine(_packageRoot, "Shipped.probe");
        File.WriteAllText(shipped, "shipped");

        var catalog = new AssetCatalog();
        await CreateService(catalog).ImportAllAsync();

        Assert.False(File.Exists(shipped + ".meta"));
        Assert.Empty(catalog.EnumerateAssets());
    }

    /// <summary>guid は、プロジェクトのアセットと同じく隣の .meta から読む。</summary>
    [Fact]
    public async Task Shipped_meta_decides_the_guid()
    {
        string guid = WriteShipped("Shipped.probe", "shipped");

        var catalog = new AssetCatalog();
        await CreateService(catalog).ImportAllAsync();

        Assert.Equal(guid, catalog.EnumerateAssets().Single().Key.Value);
    }

    /// <summary>更新時刻が古いままでも、パッケージの中身が入れ替われば取り込み直す。</summary>
    [Fact]
    public async Task Replaced_package_content_is_reimported_even_with_an_older_timestamp()
    {
        string shipped = Path.Combine(_packageRoot, "Shipped.probe");
        WriteShipped("Shipped.probe", "version one");
        File.SetLastWriteTimeUtc(shipped, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        AssetImportService service = CreateService(new AssetCatalog());
        await service.ImportAllAsync();
        Assert.Empty(await service.ImportIfChangedAsync());

        File.WriteAllText(shipped, "version two, longer");
        File.SetLastWriteTimeUtc(shipped, new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        AssetImportResult result = Assert.Single(await service.ImportIfChangedAsync());
        Assert.True(result.Success, result.Message);
    }

    /// <summary>書き戻せるインポータでも、パッケージのアセットは読み取り専用になる。</summary>
    [Fact]
    public async Task Package_assets_are_read_only_in_the_inspector()
    {
        File.WriteAllText(Path.Combine(_assetsRoot, "Own.probe"), "own");
        WriteShipped("Shipped.probe", "shipped");

        var catalog = new AssetCatalog();
        AssetImportService service = CreateService(catalog);
        await service.ImportAllAsync();
        var vm = EditorFixture.NewEditor(assets: catalog, imports: service, layout: Layout);

        vm.SelectAsset(KeyOf(catalog, "Own.probe"));
        Assert.True(vm.Asset.IsEditable);

        vm.SelectAsset(KeyOf(catalog, "Packages/Probe.Pack/Shipped.probe"));
        Assert.False(vm.Asset.IsEditable);
        Assert.All(vm.Asset.Inspected!.Fields, field => Assert.True(field.IsReadOnly));
    }

    /// <summary>置き場のパスが区切り文字で終わっていても、その下は読み取り専用のまま</summary>
    [Fact]
    public void A_trailing_separator_on_the_package_path_keeps_it_read_only()
    {
        var layout = new ProjectAssetLayout(
            _assetsRoot, [ProjectAssetLayout.Package("Probe.Pack", _packageRoot + Path.DirectorySeparatorChar)]);

        Assert.True(layout.IsReadOnlySource(Path.Combine(_packageRoot, "Shipped.probe")));
        Assert.False(layout.IsReadOnlySource(Path.Combine(_assetsRoot, "Own.probe")));
    }

    private ProjectAssetLayout Layout => new(_assetsRoot, [ProjectAssetLayout.Package("Probe.Pack", _packageRoot)]);

    private AssetImportService CreateService(AssetCatalog catalog) =>
        new(catalog, Layout.Sources, [new ProbeImporter()], Path.Combine(_root, "import-stamps"));

    /// <summary>.meta を同梱したパッケージのソースを書く</summary>
    private string WriteShipped(string relativePath, string content)
    {
        string path = Path.Combine(_packageRoot, relativePath);
        string guid = Guid.NewGuid().ToString("N");
        File.WriteAllText(path, content);
        File.WriteAllText(path + ".meta", $"{{\"guid\":\"{guid}\"}}");
        return guid;
    }

    private static AssetKey KeyOf(AssetCatalog catalog, string displayPath) =>
        catalog.EnumerateAssets().Single(entry => entry.DisplayPath == displayPath).Key;

    private static AuthoringObject Probe(string name)
    {
        ObjectSchema schema = TestSchemas.Object("Probe", ("Name", TestSchemas.Scalar("System.String")));
        var data = new FieldValue();
        data.Add("Name", new FieldValue { Text = name });
        return new AuthoringObject(schema, data);
    }

    /// <summary>書き戻しに対応する取り込み</summary>
    private sealed class ProbeImporter : IAssetImporter
    {
        public IReadOnlyCollection<string> SupportedExtensions => [".probe"];

        public bool IsSaveSupported => true;

        public Task<AssetImportResult> ImportAsync(
            AssetImportRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(AssetImportResult.Succeeded(
                "probe",
                new ImportedAsset(request.RelativePath, Probe(File.ReadAllText(request.SourcePath)), request.SourcePath)));

        public Task SaveAsync(AuthoringObject asset, string sourcePath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
