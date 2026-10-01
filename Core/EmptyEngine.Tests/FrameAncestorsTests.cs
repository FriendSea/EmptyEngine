using EmptyEngine.Editor.Hosting;
using Xunit;

namespace EmptyEngine.Tests;

/// <summary>エディタ UI を iframe に入れてよい相手の組み立て</summary>
public class FrameAncestorsTests
{
    /// <remarks>自分のペインを iframe に入れるのはエディタ自身なので、宣言が無くても 'self' は要る</remarks>
    [Fact]
    public void SelfIsAlwaysAllowed()
    {
        Assert.Equal("frame-ancestors 'self'", EditorWebRunner.BuildFrameAncestors(null));
    }

    /// <remarks>エンジンは特定の道具を知らない。VSCode も宣言した誰かとして同じ道を通る</remarks>
    /// <remarks>宣言は MSBuild の項目なので <c>;</c> で並ぶ</remarks>
    [Fact]
    public void DeclaredSourcesFollowSelf()
    {
        string policy = EditorWebRunner.BuildFrameAncestors("https://tool.example;vscode-webview:;vscode-file:");

        Assert.Equal("frame-ancestors 'self' https://tool.example vscode-webview: vscode-file:", policy);
    }

    [Fact]
    public void RepeatedSourcesAreListedOnce()
    {
        string policy = EditorWebRunner.BuildFrameAncestors("https://tool.example 'self' https://tool.example");

        Assert.Equal("frame-ancestors 'self' https://tool.example", policy);
    }

    /// <remarks>値はヘッダへそのまま出るので、行を割れる文字を含む宣言は使わない</remarks>
    [Fact]
    public void DeclarationWithControlCharactersIsDropped()
    {
        string policy = EditorWebRunner.BuildFrameAncestors("https://ok.example\r\nX-Evil: 1");

        Assert.Equal("frame-ancestors 'self'", policy);
    }
}
