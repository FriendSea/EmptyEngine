using EmptyEngine.Modules.Testing;
using EmptyEngine.Serialization;
using EmptyEngine.Core;
using EmptyEngine.ObjectModel;
using EmptyEngine.Storage;
using EmptyEngine.Tests;
using Xunit;

namespace EmptyEngine.World.Tests;

/// <summary>アセット実体の生成規約の検証</summary>
public sealed class AssetConstructorTests
{
    /// <summary>テスト用の最小コンテナ</summary>
    private sealed class Services(params object[] instances) : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => Array.Find(instances, i => serviceType.IsInstanceOfType(i));
    }

    private const string Key = "assets/probe.dat";

    private static WorldAssetResolver ResolverWith(object asset, IServiceProvider? services = null)
    {
        using var blob = new MemoryStream();
        AssetBlob.SerializeTo(blob, asset);
        var store = AssetStorage.InMemory();
        store.Add(Key, blob.ToArray());
        return new WorldAssetResolver(store, services);
    }

    private static T Resolve<T>(WorldAssetResolver resolver) where T : class
    {
        Assert.True(resolver.TryLoad(new AssetReference<T>(Key), out T? asset));
        return asset!;
    }

    [Fact]
    public void Constructor_runs_when_the_asset_is_resolved()
    {
        WorldAssetResolver resolver = ResolverWith(new CtorProbeAsset { Value = 5 });
        CtorProbeAsset.Constructed = 0;

        CtorProbeAsset probe = Resolve<CtorProbeAsset>(resolver);

        Assert.Equal(1, CtorProbeAsset.Constructed);
        Assert.True(probe.CtorRan);
        Assert.Equal(5, probe.Value);
        Assert.Equal("initialized", probe.FromFieldInitializer);
        Assert.Same(probe, Resolve<CtorProbeAsset>(resolver));
        Assert.Equal(1, CtorProbeAsset.Constructed);
    }

    [Fact]
    public void Constructor_parameters_are_resolved_from_the_container()
    {
        var clock = new Clock();
        WorldAssetResolver resolver = ResolverWith(new InjectedProbeAsset(clock), new Services(clock));

        Assert.Same(clock, Resolve<InjectedProbeAsset>(resolver).Clock);
    }

    [Fact]
    public void Unresolvable_parameter_fails_the_resolution_instead_of_injecting_null()
    {
        WorldAssetResolver resolver = ResolverWith(new InjectedProbeAsset(new Clock()));

        Assert.False(resolver.TryLoad(new AssetReference<InjectedProbeAsset>(Key), out InjectedProbeAsset? asset));
        Assert.Null(asset);
    }

    [Fact]
    public void Binary_body_is_attached_to_the_constructed_instance()
    {
        var store = AssetStorage.InMemory();
        using (var blob = new MemoryStream())
        {
            AssetBlob.SerializeTo(blob, new BinaryProbeAsset { Payload = new BytesAssetBinary([7, 8, 9]) });
            store.Add(Key, blob.ToArray());
        }

        var resolver = new WorldAssetResolver(store);
        BinaryProbeAsset asset = Resolve<BinaryProbeAsset>(resolver);

        Assert.True(asset.CtorRan);
        Assert.NotNull(asset.Payload);
        using Stream stream = asset.Payload.OpenRead();
        byte[] body = new byte[3];
        stream.ReadExactly(body);
        Assert.Equal(new byte[] { 7, 8, 9 }, body);
    }
}

/// <summary>ctor が走ったかを観測するアセット</summary>
[Asset]
public sealed class CtorProbeAsset
{
    public static int Constructed;

    private readonly bool _ctorRan;

    public CtorProbeAsset()
    {
        _ctorRan = true;
        Constructed++;
    }

    /// <summary>計算プロパティ＝シリアライズ契約の対象外</summary>
    public bool CtorRan => _ctorRan;

    /// <summary>復元時にも値が維持される、シリアライズ対象外のフィールド</summary>
    private readonly string _fromFieldInitializer = "initialized";

    public string FromFieldInitializer => _fromFieldInitializer;

    public int Value { get; set; }
}

/// <summary>引数付き ctor を持つアセット</summary>
[Asset]
public sealed class InjectedProbeAsset(Clock clock)
{
    private readonly Clock _clock = clock;

    public Clock Clock => _clock;

    public int Value { get; set; }
}

/// <summary>本体（<see cref="IAssetBinary"/>）と ctor を併せ持つアセット</summary>
[Asset]
public sealed class BinaryProbeAsset
{
    private readonly bool _ctorRan;

    public BinaryProbeAsset() => _ctorRan = true;

    public bool CtorRan => _ctorRan;

    public IAssetBinary Payload = null!;
}
