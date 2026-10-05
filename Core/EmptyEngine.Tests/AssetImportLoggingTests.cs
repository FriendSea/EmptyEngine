using EmptyEngine.Editor;
using EmptyEngine.Editor.Assets;
using Xunit;

namespace EmptyEngine.Tests;

public sealed class AssetImportLoggingTests : IDisposable
{
    private readonly string _assetsRoot =
        Path.Combine(Path.GetTempPath(), "ee-import-log-" + Guid.NewGuid().ToString("N"));

    public AssetImportLoggingTests() => Directory.CreateDirectory(_assetsRoot);

    [Fact]
    public async Task Importer_exception_is_logged_with_its_details()
    {
        string sourcePath = Path.Combine(_assetsRoot, "Broken.throwing");
        File.WriteAllText(sourcePath, "broken");
        var logs = new RecordingLogger<AssetImportService>();
        var service = new AssetImportService(new AssetCatalog(), new ProjectAssetLayout(_assetsRoot).Sources, [new ThrowingImporter()], Path.Combine(_assetsRoot, ".import-stamps"), logs);

        AssetImportResult result = Assert.Single(await service.ImportAllAsync());

        Assert.False(result.Success);
        string log = Assert.Single(logs, line => line.StartsWith("Asset import exception", StringComparison.Ordinal));
        Assert.Contains("Broken.throwing", log);
        Assert.Contains(nameof(InvalidOperationException), log);
        Assert.Contains("Outer import failure", log);
        Assert.Contains("Inner import failure", log);
        Assert.Contains(nameof(ThrowingImporter.ImportAsync), log);
    }

    [Fact]
    public async Task Importer_failure_result_is_logged_with_its_message()
    {
        string sourcePath = Path.Combine(_assetsRoot, "Broken.failing");
        File.WriteAllText(sourcePath, "broken");
        var logs = new RecordingLogger<AssetImportService>();
        var service = new AssetImportService(new AssetCatalog(), new ProjectAssetLayout(_assetsRoot).Sources, [new FailingImporter()], Path.Combine(_assetsRoot, ".import-stamps"), logs);

        AssetImportResult result = Assert.Single(await service.ImportAllAsync());

        Assert.False(result.Success);
        string log = Assert.Single(logs, line => line.StartsWith("Import failed", StringComparison.Ordinal));
        Assert.Contains("Broken.failing", log);
        Assert.Contains("Expected import failure", log);
    }

    public void Dispose()
    {
        if (Directory.Exists(_assetsRoot)) Directory.Delete(_assetsRoot, recursive: true);
    }

    private sealed class ThrowingImporter : IAssetImporter
    {
        public IReadOnlyCollection<string> SupportedExtensions => [".throwing"];

        public async Task<AssetImportResult> ImportAsync(
            AssetImportRequest request,
            CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw new InvalidOperationException("Outer import failure", new FormatException("Inner import failure"));
        }
    }

    private sealed class FailingImporter : IAssetImporter
    {
        public IReadOnlyCollection<string> SupportedExtensions => [".failing"];

        public Task<AssetImportResult> ImportAsync(
            AssetImportRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(AssetImportResult.Failed("Expected import failure"));
    }
}
