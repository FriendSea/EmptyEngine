using EmptyEngine.Modules.Testing;
using System.Buffers;
using System.Text;
using EmptyEngine.Core;
using EmptyEngine.ObjectModel;
using EmptyEngine.Storage;
using EmptyEngine.Tests;
using MessagePack;
using Xunit;

namespace EmptyEngine.World.Tests;

/// <summary>blob にキーの無いメンバが ctor の値のまま残ることの検証</summary>
public sealed class ConstructorDefaultTests
{
    /// <summary><c>Data</c> が空 map のコンポーネント 1 つだけのシーン</summary>
    private static byte[] SceneWithEmptyData(string typeName)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        writer.WriteMapHeader(1);
        writer.Write("Roots");
        writer.WriteArrayHeader(1);
        writer.WriteMapHeader(2);
        writer.Write("InstanceId");
        writer.Write("inst");
        writer.Write("Object");
        writer.WriteMapHeader(5);
        writer.Write("Id");
        writer.Write("obj");
        writer.Write("Name");
        writer.Write("Object");
        writer.Write("Active");
        writer.Write(true);
        writer.Write("Components");
        writer.WriteArrayHeader(1);
        writer.WriteMapHeader(2);
        writer.Write("TypeName");
        writer.Write(typeName);
        writer.Write("Data");
        writer.WriteMapHeader(0);
        writer.Write("Children");
        writer.WriteArrayHeader(0);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static readonly byte[] Scene = SceneWithEmptyData(typeof(DefaultedProbe).FullName!);

    private static void AssertCtorValues(DefaultedProbe? probe)
    {
        Assert.NotNull(probe);
        Assert.Equal(42, probe.Value);
        Assert.Equal("from-ctor", probe.Text);
    }

    [Fact]
    public void A_component_entering_the_world_keeps_ctor_values_for_absent_keys()
    {
        using var world = new SceneWorld();

        world.DeserializeSceneNow(Scene, isPlaying: true);

        AssertCtorValues(world.Roots[0].GetAttachable<DefaultedProbe>());
    }

    [Fact]
    public async Task A_prefab_clone_keeps_ctor_values_for_absent_keys()
    {
        var store = AssetStorage.InMemory();
        store.Add("prefab", Scene);
        using var world = new SceneWorld(WorldAssetResolver.FromStore(store), log: _ => { });

        IObject spawned = await world.InstantiateAsync(new AssetReference<IObject>("prefab"));

        AssertCtorValues(spawned.GetAttachable<DefaultedProbe>());
    }

    [Fact]
    public void A_startup_scene_keeps_ctor_values_for_absent_keys()
    {
        var store = AssetStorage.InMemory();
        store.Add("start.scene", Scene);
        store.Add(StartupScene.ManifestKey, Encoding.UTF8.GetBytes("""["start.scene"]"""));
        using var world = new SceneWorld();

        world.LoadAllIntoNow(store);

        AssertCtorValues(world.Roots[0].GetAttachable<DefaultedProbe>());
    }

    [Fact]
    public void A_type_whose_ctor_arguments_are_unavailable_fails_at_decode()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => SceneBlob.Decode(SceneWithEmptyData(typeof(InjectedProbe).FullName!)));

        Assert.Contains(typeof(InjectedProbe).FullName!, error.Message);
    }

    [Fact]
    public void The_decoded_instance_is_the_one_that_enters_the_world()
    {
        using var world = new SceneWorld();
        world.DeserializeSceneNow(SceneWithEmptyData(typeof(CountedProbe).FullName!), isPlaying: true);

        Assert.Equal(1, CountedProbe.Constructed);
    }
}

/// <summary>シリアライズ対象メンバに初期化子で値を入れるコンポーネント</summary>
public sealed class DefaultedProbe : IAttachable
{
    public int Value { get; set; } = 42;

    public string Text { get; set; } = "from-ctor";
}

/// <summary>ctor の回数を数えるコンポーネント（このテスト専用）</summary>
public sealed class CountedProbe : IAttachable
{
    public static int Constructed;

    public CountedProbe() => Constructed++;

    public int Value { get; set; }
}
