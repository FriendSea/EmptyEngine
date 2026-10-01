using EmptyEngine.Core;
using EmptyEngine.Core.RuntimeLink;
using Xunit;

namespace EmptyEngine.Tests;

public sealed class RuntimeEditorLinkContractTests
{
    [Fact]
    public void Scene_and_asset_calls_follow_the_current_setters()
    {
        var link = new RuntimeEditorLink(_ => { });
        var first = new ProbeScene();
        var second = new ProbeScene();
        var assets = new ProbeAssets();
        link.Scene = first;
        link.Assets = assets;

        ((ISceneSerializer)link).DeserializeSceneNow([1], isPlaying: false);
        Assert.Equal(new byte[] { 1 }, ((ISceneSerializer)link).SerializeScene());
        ((IAssetUpdater)link).RequestUpdate(new AssetKey("first"));

        link.Scene = second;
        link.Assets = null;
        ((ISceneSerializer)link).DeserializeSceneNow([2], isPlaying: true);
        ((IAssetUpdater)link).RequestUpdate(new AssetKey("ignored"));

        Assert.Equal(new byte[] { 1 }, first.Blob);
        Assert.Equal(new byte[] { 2 }, second.Blob);
        Assert.Equal(new[] { "first" }, assets.Keys);
        link.Dispose();
        Assert.False(first.Disposed);
        Assert.False(second.Disposed);
    }

    [Fact]
    public void Missing_scene_is_an_explicit_error()
    {
        using var link = new RuntimeEditorLink(_ => { });

        InvalidOperationException serialize = Assert.Throws<InvalidOperationException>(
            () => ((ISceneSerializer)link).SerializeScene());
        InvalidOperationException deserialize = Assert.Throws<InvalidOperationException>(
            () => ((ISceneSerializer)link).DeserializeSceneNow([], isPlaying: false));

        Assert.Contains(nameof(RuntimeEditorLink.Scene), serialize.Message);
        Assert.Contains(nameof(RuntimeEditorLink.Scene), deserialize.Message);
    }

    private sealed class ProbeScene : ISceneSerializer, IDisposable
    {
        public byte[] Blob { get; private set; } = [];

        public bool Disposed { get; private set; }

        public byte[] SerializeScene() => Blob;

        public Task DeserializeSceneAsync(byte[] blob, bool isPlaying, CancellationToken cancellationToken = default)
        {
            Blob = blob;
            return Task.CompletedTask;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class ProbeAssets : IAssetUpdater
    {
        public List<string> Keys { get; } = [];

        public void RequestUpdate(AssetKey key, CancellationToken cancellationToken = default)
        {
            Keys.Add(key.Value);
        }
    }
}
