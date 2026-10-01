using EmptyEngine.Editor.Hosting;
using EmptyEngine.Host;
using Xunit;

namespace EmptyEngine.Tests;

/// <summary>エディタ UI の在り処が Host へ渡る 1 行の両端での検証</summary>
public class EditorUiAnnouncementTests
{
    private static string Compose(string url) => EditorWebRunner.ListeningPrefix + url + "/";

    /// <summary>ビルドを抱えた dotnet run が改行無しで残し、次の 1 行の頭へ糊付けされる進捗の制御列</summary>
    private const string ProgressEscape = "\e]9;4;0;\e\\";

    [Theory]
    [InlineData(5199)]
    public void HostReadsTheAddressTheEditorAnnounces(int port)
    {
        Uri? parsed = HostSession.ParseEditorListening(Compose($"http://127.0.0.1:{port}"));

        Assert.NotNull(parsed);
        Assert.Equal(port, parsed!.Port);
        Assert.Equal($"http://127.0.0.1:{port}", parsed.GetLeftPart(System.UriPartial.Authority));
    }

    [Fact]
    public void TheProgressEscapeGluedToTheFrontDoesNotHideTheAddress()
    {
        Uri? parsed = HostSession.ParseEditorListening(ProgressEscape + Compose("http://127.0.0.1:5170"));

        Assert.NotNull(parsed);
        Assert.Equal(5170, parsed!.Port);
    }

    [Theory]
    [InlineData("Asset import finished. assets=12")]
    [InlineData("info: Microsoft.Hosting.Lifetime[14] Now listening on: http://127.0.0.1:5170")]
    [InlineData("Editor UI listening on")]
    public void OtherEditorLinesAreNotTakenForTheSignal(string line)
    {
        Assert.Null(HostSession.ParseEditorListening(line));
    }
}
