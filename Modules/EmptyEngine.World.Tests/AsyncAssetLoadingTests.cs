using EmptyEngine.Modules.Testing;
using EmptyEngine.Serialization;
using EmptyEngine.Core;
using EmptyEngine.ObjectModel;
using EmptyEngine.Storage;
using EmptyEngine.Tests;
using Xunit;

namespace EmptyEngine.World.Tests;

/// <summary>読み込みを非同期にした解決と複製の検証</summary>
public sealed class AsyncAssetLoadingTests
{
    [Fact]
    public void TryResolve_answers_only_what_is_already_loaded()
    {
        var store = AssetStorage.InMemory();
        store.Add("asset", Serialize("loaded"));
        var resolver = WorldAssetResolver.FromStore(store);
        var reference = new AssetReference<TestAsset>("asset");

        Assert.False(resolver.TryResolve(reference, out _));
        Assert.True(resolver.TryLoad(reference, out TestAsset? loaded));
        Assert.True(resolver.TryResolve(reference, out TestAsset? cached));
        Assert.Same(loaded, cached);
    }

    [Fact]
    public void Resolving_a_prefab_loads_what_its_components_reference()
    {
        var store = AssetStorage.InMemory();
        store.Add("sprite", Serialize("for the clone"));
        store.Add("prefab", Prefab("sprite"));
        var resolver = WorldAssetResolver.FromStore(store);

        Assert.True(resolver.TryLoad(new AssetReference<IObject>("prefab"), out IObject? template));

        Assert.True(resolver.TryResolve(new AssetReference<TestAsset>("sprite"), out _));
        IObject spawned = new SceneWorld(resolver).Instantiate(template!);
        Assert.Equal("for the clone", spawned.GetAttachable<HookedSprite>()!.SeenOnDeserialized);
    }

    [Fact]
    public void Assets_that_reference_each_other_resolve_without_waiting_on_themselves()
    {
        var store = AssetStorage.InMemory();
        store.Add("a", SerializeLoop("b"));
        store.Add("b", SerializeLoop("a"));
        var resolver = WorldAssetResolver.FromStore(store);

        Assert.True(resolver.TryLoad(new AssetReference<TestLoopAsset>("a"), out TestLoopAsset? a));
        Assert.NotNull(a!.Resolved);
        Assert.Same(a, a.Resolved!.Resolved);
    }

    [Fact]
    public void Synchronous_instantiate_refuses_a_clone_whose_assets_are_not_loaded()
    {
        var resolver = new GatedResolver();
        var world = new SceneWorld(resolver, log: _ => { });

        Assert.Throws<InvalidOperationException>(() => world.Instantiate(resolver.Template));
        Assert.Empty(world.Roots);
    }

    [Fact]
    public async Task InstantiateAsync_adds_the_clone_after_its_assets_load()
    {
        var resolver = new GatedResolver();
        var world = new SceneWorld(resolver, log: _ => { });

        ValueTask<IObject> spawning = world.InstantiateAsync(new AssetReference<IObject>("prefab"));
        Assert.False(spawning.IsCompleted);
        Assert.Empty(world.Roots);

        resolver.Open();
        IObject spawned = await spawning;

        Assert.Single(world.Roots);
        Assert.Equal("gated", spawned.GetAttachable<HookedSprite>()!.SeenOnDeserialized);
        Assert.Equal("gated", world.Instantiate(resolver.Template).GetAttachable<HookedSprite>()!.SeenOnDeserialized);
    }

    private static byte[] Prefab(string spriteKey) => SceneBlob.Encode([new RootInstanceData("prefab-scene",
        new ObjectData("src", "Prefab",
            [new ComponentData(typeof(HookedSprite).FullName!,
                new HookedSprite { Asset = AssetReference<TestAsset>.FromKey(spriteKey) })],
            []))]);

    private static byte[] Serialize(string text)
    {
        using var blob = new MemoryStream();
        AssetBlob.SerializeTo(blob, new TestAsset(text));
        return blob.ToArray();
    }

    private static byte[] SerializeLoop(string next)
    {
        using var blob = new MemoryStream();
        AssetBlob.SerializeTo(blob, new TestLoopAsset { Next = AssetReference<TestLoopAsset>.FromKey(next) });
        return blob.ToArray();
    }

    /// <summary>開くまでアセットの読み込みを止めておくリゾルバ</summary>
    private sealed class GatedResolver : IAssetResolver
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Dictionary<string, object> _loaded = new(StringComparer.Ordinal);

        public GameObject Template { get; } = new("src", "Prefab",
            [new HookedSprite { Asset = AssetReference<TestAsset>.FromKey("gated") }]);

        public void Open() => _gate.SetResult();

        public ValueTask<TAsset?> ResolveAsync<TAsset>(
            AssetReference<TAsset> assetReference, CancellationToken cancellationToken = default) where TAsset : class
        {
            if (typeof(TAsset) == typeof(IObject))
                return new(Template as TAsset);
            if (TryResolve(assetReference, out TAsset? loaded))
                return new(loaded);
            return new(LoadAsync<TAsset>(assetReference.AssetKey));
        }

        public bool TryResolve<TAsset>(AssetReference<TAsset> assetReference, out TAsset? asset) where TAsset : class
        {
            asset = _loaded.TryGetValue(assetReference.AssetKey, out object? value) ? value as TAsset : null;
            return asset is not null;
        }

        private async Task<TAsset?> LoadAsync<TAsset>(string key) where TAsset : class
        {
            await _gate.Task;
            _loaded[key] = new TestAsset(key);
            return _loaded[key] as TAsset;
        }
    }
}

/// <summary>別のアセットをフックで解決し合うテスト用アセット</summary>
[Asset]
public sealed class TestLoopAsset : IAssetResolutionHook
{
    private TestLoopAsset? _resolved;

    public AssetReference<TestLoopAsset> Next { get; set; } = new();

    public TestLoopAsset? Resolved => _resolved;

    public async ValueTask OnResolveAssetsAsync(IAssetResolver resolver) => _resolved = await resolver.ResolveAsync(Next);
}
