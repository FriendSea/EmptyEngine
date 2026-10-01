using EmptyEngine.Editor.ViewModels;
using Xunit;

namespace EmptyEngine.Tests;

/// <summary>ヒエラルキーへ落とされた 1 件の読み取りの検証</summary>
public sealed class DroppedItemTests
{
    private static DroppedItem Parse(string raw)
    {
        DroppedItem? item = DroppedItem.Parse(raw);
        Assert.NotNull(item);
        return item.Value;
    }

    [Fact]
    public void ReadsFileUri()
    {
        if (!OperatingSystem.IsWindows()) return;

        DroppedItem item = Parse("file:///C:/game/Assets/Player.scene");

        Assert.Equal(@"C:\game\Assets\Player.scene", item.FullPath);
        Assert.Equal("Player.scene", item.FileName);
    }

    [Fact]
    public void DecodesPercentEscapesIncludingTheDriveColon()
    {
        if (!OperatingSystem.IsWindows()) return;

        DroppedItem item = Parse("file:///c%3A/game/Assets/Boss%20Stage.scene");

        Assert.Equal(@"c:\game\Assets\Boss Stage.scene", item.FullPath);
        Assert.Equal("Boss Stage.scene", item.FileName);
    }

    [Fact]
    public void ReadsPlainPath()
    {
        if (!OperatingSystem.IsWindows()) return;

        DroppedItem item = Parse(@"C:\game\Assets\Player.scene");

        Assert.Equal(@"C:\game\Assets\Player.scene", item.FullPath);
        Assert.Equal("Player.scene", item.FileName);
    }

    [Fact]
    public void KeepsBareFileNameWithoutAPath()
    {
        DroppedItem item = Parse("Player.scene");

        Assert.Null(item.FullPath);
        Assert.Equal("Player.scene", item.FileName);
    }

    [Fact]
    public void TakesTheNameOutOfARelativePath()
    {
        DroppedItem item = Parse("Assets/Stage/Player.scene");

        Assert.Null(item.FullPath);
        Assert.Equal("Player.scene", item.FileName);
    }

    [Fact]
    public void RejectsNonFileUris()
    {
        Assert.Null(DroppedItem.Parse("https://example.com/Player.scene"));
    }

    [Fact]
    public void RejectsEmptyInput()
    {
        Assert.Null(DroppedItem.Parse(null));
        Assert.Null(DroppedItem.Parse("   "));
        Assert.Null(DroppedItem.Parse("Assets/Stage/"));
    }
}
