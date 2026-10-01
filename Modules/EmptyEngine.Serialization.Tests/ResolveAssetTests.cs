using EmptyEngine.Modules.Testing;
using EmptyEngine.Core;
using EmptyEngine.Generators;
using EmptyEngine.Serialization.Editor;
using EmptyEngine.ObjectModel;
using EmptyEngine.Storage;
using EmptyEngine.Tests;
using EmptyEngine.World;
using Xunit;

namespace EmptyEngine.Serialization.Tests;

public sealed partial class ResolveAssetProbe : ILifecycleAttachable
{
    [ResolveAsset]
    private TestAsset? _thing;

    [ResolveAsset("AltRef")]
    private TestAsset? _other;

    private int _afterCount;

    public TestAsset? ResolvedThing => _thing;
    public TestAsset? ResolvedOther => _other;
    public int AfterCount => _afterCount;

    public void OnDeserialized(IObject owner) => _afterCount++;
}

public abstract class LifecycleProbeBase : ILifecycleAttachable
{
    private int _baseDeserialized;
    private int _baseDestroyed;

    public int BaseDeserializedCount => _baseDeserialized;
    public int BaseDestroyedCount => _baseDestroyed;

    public virtual void OnCreated(IObject owner) { }
    public virtual void OnDeserialized(IObject owner) => _baseDeserialized++;
    public virtual void OnDestroy(IObject owner) => _baseDestroyed++;
}

public sealed partial class DerivedResolveProbe : LifecycleProbeBase
{
    [ResolveAsset]
    private TestAsset? _thing;

    public TestAsset? ResolvedThing => _thing;
}

/// <summary><c>[ResolveAsset]</c> の生成物が実シリアライザ経路で機能することの検証</summary>
public sealed class ResolveAssetTests
{
    [Fact]
    public void Generated_properties_are_the_only_serialized_members()
    {
        Assert.True(typeof(ILifecycleAttachable).IsAssignableFrom(typeof(ResolveAssetProbe)));

        var probe = new ResolveAssetProbe();
        var written = FieldValueCodec.DecodeBytes(
            TypeSerializers.Serialize(typeof(ResolveAssetProbe), probe), CatalogStub.Schema(typeof(ResolveAssetProbe)).Root);
        var keys = written.Entries
            .Select(e => e.Key ?? string.Empty)
            .ToArray();

        Assert.Equal(2, keys.Length);
        Assert.Contains("Thing", keys);
        Assert.Contains("AltRef", keys);
    }

    [Fact]
    public void Reference_is_resolved_into_private_field_on_instantiate()
    {
        var store = AssetStorage.InMemory();
        using (var artifact = new MemoryStream())
        {
            AssetBlob.SerializeTo(artifact, new TestAsset("hello"));
            store.Add("thing.dat", artifact.ToArray());
        }
        store.Add("probe.scene", SceneBlob.Encode([new RootInstanceData("prefab-scene",
            new ObjectData("src", "Probe",
                [new ComponentData(typeof(ResolveAssetProbe).FullName!,
                    new ResolveAssetProbe { Thing = AssetReference<TestAsset>.FromKey("thing.dat") })],
                []))]));
        var resolver = WorldAssetResolver.FromStore(store);

        Assert.True(resolver.TryLoad<IObject>(new AssetReference<IObject>("probe.scene"), out IObject? template));
        var world = new SceneWorld(resolver);
        IObject spawned = world.Instantiate(template!);

        ResolveAssetProbe probe = spawned.GetAttachable<ResolveAssetProbe>()!;
        Assert.Equal("hello", probe.ResolvedThing?.Text);
        Assert.Null(probe.ResolvedOther);
        Assert.Equal(1, probe.AfterCount);

        spawned.Destroy();
        world.FlushPendingDestructions();
        Assert.Null(probe.ResolvedThing);
    }

    [Fact]
    public void Virtual_base_lifecycle_is_chained_from_generated_overrides()
    {
        var store = AssetStorage.InMemory();
        using (var artifact = new MemoryStream())
        {
            AssetBlob.SerializeTo(artifact, new TestAsset("hello"));
            store.Add("thing.dat", artifact.ToArray());
        }
        store.Add("probe.scene", SceneBlob.Encode([new RootInstanceData("prefab-scene",
            new ObjectData("src", "Probe",
                [new ComponentData(typeof(DerivedResolveProbe).FullName!,
                    new DerivedResolveProbe { Thing = AssetReference<TestAsset>.FromKey("thing.dat") })],
                []))]));
        var resolver = WorldAssetResolver.FromStore(store);

        Assert.True(resolver.TryLoad<IObject>(new AssetReference<IObject>("probe.scene"), out IObject? template));
        var world = new SceneWorld(resolver);
        IObject spawned = world.Instantiate(template!);

        DerivedResolveProbe probe = spawned.GetAttachable<DerivedResolveProbe>()!;
        Assert.Equal("hello", probe.ResolvedThing?.Text);
        Assert.True(probe.BaseDeserializedCount >= 1);

        spawned.Destroy();
        world.FlushPendingDestructions();
        Assert.Equal(1, probe.BaseDestroyedCount);
        Assert.Null(probe.ResolvedThing);
    }
}
