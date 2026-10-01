using EmptyEngine.Editor.Distribution;
using Xunit;

namespace EmptyEngine.Tests;

/// <summary>配布先として受け取るディレクトリの絞り込み</summary>
/// <remarks>宛先はビルドコマンドの一部になるので、外を書けるもの・コマンド行を割れるものを弾く</remarks>
public class DistributionDestinationTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "emptyengine-project"));

    [Fact]
    public void DirectoryBelowTheProjectIsAccepted()
    {
        Assert.Equal(Path.Combine(Root, "dist"), DistributionPipeline.ResolveDestination("dist", Root));
    }

    [Fact]
    public void NestedDirectoryBelowTheProjectIsAccepted()
    {
        string expected = Path.Combine(Root, "dist-web", "StreamingAssets");

        Assert.Equal(expected, DistributionPipeline.ResolveDestination(expected, Root));
    }

    /// <remarks>publish はここを消しに来るので、プロジェクト自身は配布先にできない</remarks>
    [Fact]
    public void TheProjectItselfIsRejected()
    {
        Assert.Throws<ArgumentException>(() => DistributionPipeline.ResolveDestination(".", Root));
    }

    [Fact]
    public void AncestorOfTheProjectIsRejected()
    {
        Assert.Throws<ArgumentException>(() => DistributionPipeline.ResolveDestination("..", Root));
    }

    /// <remarks>ログイン時に走る場所へ publish されるのを止める</remarks>
    [Theory]
    [InlineData("../../elsewhere")]
    [InlineData("../sibling")]
    public void DirectoryOutsideTheProjectIsRejected(string destination)
    {
        Assert.Throws<ArgumentException>(() => DistributionPipeline.ResolveDestination(destination, Root));
    }

    /// <remarks>宛先はコマンド行へそのまま入る＝引用符を許すとビルドへ引数を足せる</remarks>
    [Fact]
    public void DestinationWithAQuoteIsRejected()
    {
        Assert.Throws<ArgumentException>(
            () => DistributionPipeline.ResolveDestination("dist\" -p:Injected=1 \"", Root));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyDestinationIsRejected(string destination)
    {
        Assert.Throws<ArgumentException>(() => DistributionPipeline.ResolveDestination(destination, Root));
    }
}
