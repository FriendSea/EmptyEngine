using Xunit;

namespace EmptyEngine.Windowing.Tests;

public sealed class WindowPlacementStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "EmptyEngine.WindowPlacementStoreTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Placement_round_trips()
    {
        string path = Path.Combine(_directory, "placement.json");
        var store = new WindowPlacementStore(path);
        var expected = new WindowPlacement(-1200, 240, 1280, 720);

        store.Write(expected);

        Assert.Equal(expected, store.Read());
    }

    [Fact]
    public void Custom_store_path_is_relative_to_the_executable()
    {
        WindowPlacementStore store = WindowPlacementStore.ForWindow("Data/window.save");

        Assert.Equal(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Data", "window.save")),
            store.FilePath);
    }

    [Fact]
    public void Absolute_store_path_is_rejected()
    {
        string absolutePath = Path.GetFullPath(Path.Combine(_directory, "window.save"));

        Assert.Throws<ArgumentException>(() => WindowPlacementStore.ForWindow(absolutePath));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"X\":0,\"Y\":0,\"Width\":0,\"Height\":720}")]
    [InlineData("{\"X\":0,\"Y\":0,\"Width\":1280,\"Height\":-1}")]
    public void Invalid_placement_is_ignored(string contents)
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "placement.json");
        File.WriteAllText(path, contents);

        Assert.Null(new WindowPlacementStore(path).Read());
    }

    [Theory]
    [InlineData(-100, 100, 1280, 720, true)]
    [InlineData(-1250, 100, 1280, 720, false)]
    [InlineData(1890, 100, 1280, 720, false)]
    [InlineData(100, -700, 1280, 720, false)]
    public void Visibility_requires_an_accessible_part_of_the_window(
        int x,
        int y,
        int width,
        int height,
        bool expected)
    {
        var placement = new WindowPlacement(x, y, width, height);

        Assert.Equal(expected, placement.HasVisibleArea(0, 0, 1920, 1080));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
