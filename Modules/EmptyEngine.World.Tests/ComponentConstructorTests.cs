using EmptyEngine.ObjectModel;
using EmptyEngine.Tests;
using Xunit;

namespace EmptyEngine.World.Tests;

/// <summary>ヒエラルキー構築時のコンストラクタ呼び出しと DI 注入の契約検証</summary>
public sealed class ComponentConstructorTests
{
    /// <summary>テスト用の最小コンテナ</summary>
    private sealed class Services(params object[] instances) : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => Array.Find(instances, i => serviceType.IsInstanceOfType(i));
    }

    private static byte[] SceneWith(params ComponentData[] components)
        => SceneBlob.Encode([new RootInstanceData("inst", new ObjectData("obj", "O", components, []))]);

    private static ComponentData Component(IAttachable component)
        => new(component.GetType().FullName!, component);

    private static T Attached<T>(SceneWorld world) where T : class, IAttachable
        => Assert.IsType<T>(((IObject)world.Roots[0]).GetAttachable<T>());

    [Fact]
    public void Only_components_new_to_the_world_get_new_instances()
    {
        byte[] initial = SceneWith(Component(new CtorProbe { Value = 5 }));
        byte[] updated = SceneWith(Component(new CtorProbe { Value = 6 }));
        byte[] added = SceneWith(Component(new CtorProbe { Value = 6 }), Component(new CtorProbe { Value = 9 }));
        using var world = new SceneWorld();

        world.DeserializeSceneNow(initial, isPlaying: true);
        CtorProbe first = Attached<CtorProbe>(world);
        Assert.True(first.CtorRan);
        Assert.Equal(5, first.Value);

        world.DeserializeSceneNow(updated, isPlaying: true);
        Assert.Same(first, Attached<CtorProbe>(world));
        Assert.Equal(6, first.Value);

        world.DeserializeSceneNow(added, isPlaying: true);
        CtorProbe[] components = world.Roots[0].GetAllAttachables().OfType<CtorProbe>().ToArray();
        Assert.Same(first, components[0]);
        Assert.True(components[1].CtorRan);
        Assert.Equal(new[] { 6, 9 }, components.Select(c => c.Value));
    }

    [Fact]
    public void Constructor_parameters_are_resolved_from_the_container()
    {
        var clock = new Clock();
        var world = new SceneWorld(assetResolver: null, log: null, services: new Services(clock));

        world.DeserializeSceneNow(SceneWith(Component(new InjectedProbe(clock))), isPlaying: true);

        Assert.Same(clock, Attached<InjectedProbe>(world).Clock);
    }

    [Fact]
    public void Unresolvable_parameter_reports_the_component_type()
    {
        var world = new SceneWorld();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => world.DeserializeSceneNow(SceneWith(Component(new InjectedProbe(new Clock()))), isPlaying: true));

        Assert.Contains(typeof(InjectedProbe).FullName!, error.Message);
    }

    [Fact]
    public void Instantiate_runs_the_constructor_on_the_copy()
    {
        byte[] scene = SceneWith(Component(new CtorProbe { Value = 1 }));
        var world = new SceneWorld();
        world.DeserializeSceneNow(scene, isPlaying: true);
        int before = CtorProbe.Constructed;

        IObject copy = world.Instantiate(world.Roots[0]);

        Assert.Equal(before + 1, CtorProbe.Constructed);
        Assert.NotSame(Attached<CtorProbe>(world), copy.GetAttachable<CtorProbe>());
        Assert.True(copy.GetAttachable<CtorProbe>()!.CtorRan);
    }
}

/// <summary>ctor が走ったか（＋走った回数）を観測するコンポーネント</summary>
public sealed class CtorProbe : IAttachable
{
    public static int Constructed;

    private readonly bool _ctorRan;

    public CtorProbe()
    {
        _ctorRan = true;
        Constructed++;
    }

    /// <summary>計算プロパティ＝シリアライズ契約の対象外</summary>
    public bool CtorRan => _ctorRan;

    public int Value { get; set; }
}

/// <summary>DI コンテナから引く依存</summary>
public sealed class Clock
{
    public int Frame { get; set; }
}

/// <summary>引数付き ctor を持つコンポーネント</summary>
public sealed class InjectedProbe(Clock clock) : IAttachable
{
    private readonly Clock _clock = clock;

    public Clock Clock => _clock;

    public int Value { get; set; }
}
