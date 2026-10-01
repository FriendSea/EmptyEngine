using EmptyEngine.Core;
using EmptyEngine.ObjectModel;

namespace EmptyEngine.Modules.Testing;

/// <summary>参照先を解決フックで読み込む部品</summary>
public sealed class HookedSprite : IAttachable, IAssetResolutionHook, ILifecycleAttachable
{
    private TestAsset? _resolved;
    private string? _seenOnDeserialized;

    public AssetReference<TestAsset> Asset { get; set; }

    public TestAsset? Resolved => _resolved;

    /// <summary><c>OnDeserialized</c> の時点で見えていた解決済みアセットの中身</summary>
    public string? SeenOnDeserialized => _seenOnDeserialized;

    public async ValueTask OnResolveAssetsAsync(IAssetResolver resolver) => _resolved = await resolver.ResolveAsync(Asset);

    public void OnCreated(IObject owner)
    {
    }

    public void OnDeserialized(IObject owner) => _seenOnDeserialized = _resolved?.Text;

    public void OnDestroy(IObject owner)
    {
    }
}
