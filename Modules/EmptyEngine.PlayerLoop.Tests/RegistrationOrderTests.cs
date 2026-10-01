using EmptyEngine.ObjectModel;
using EmptyEngine.WebGpu;
using EmptyEngine.World;
using Xunit;

namespace EmptyEngine.PlayerLoop.Tests;

/// <summary>更新順を控えるだけのコンポーネント</summary>
internal sealed class OrderProbe(DefaultPlayerLoopRegistry registry, string name, List<string> trace) : UpdatableComponent(registry)
{
    protected override void Update(IObject owner, in UpdateContext context)
        => trace.Add(name);
}

/// <summary>登録・解除を跨いだ在籍と並びの検証</summary>
/// <remarks>登録解除後も残った要素の登録順が維持されることを検証する。</remarks>
public sealed class RegistrationOrderTests
{
    private static GameObject Owner(string id) => new(id, id, []);

    [Fact]
    public void Renderer_registration_keeps_survivor_order_and_appends_re_registered_items()
    {
        var world = new RenderWorld();
        var a = new SpriteComponent(world);
        var b = new SpriteComponent(world);
        var c = new SpriteComponent(world);
        world.RegisterRenderer(a);
        world.RegisterRenderer(b);
        world.RegisterRenderer(c);
        world.RegisterRenderer(b);
        Assert.Equal(new ISpriteRenderer[] { a, b, c }, world.Renderers);
        world.UnregisterRenderer(b);
        Assert.Equal(new ISpriteRenderer[] { a, c }, world.Renderers);
        world.RegisterRenderer(b);
        Assert.Equal(new ISpriteRenderer[] { a, c, b }, world.Renderers);
    }

    [Fact]
    public void The_active_camera_falls_through_to_the_next_registration()
    {
        var world = new RenderWorld();
        var first = new CameraComponent(world);
        var second = new CameraComponent(world);
        world.RegisterCamera(first);
        world.RegisterCamera(second);
        Assert.Same(first, world.ActiveCamera);

        world.UnregisterCamera(first);

        Assert.Same(second, world.ActiveCamera);
    }

    [Fact]
    public void Ticks_follow_registration_order_through_removal_re_registration_and_teardown()
    {
        var registry = new DefaultPlayerLoopRegistry();
        var trace = new List<string>();
        OrderProbe Start(string name)
        {
            var probe = new OrderProbe(registry, name, trace);
            probe.OnCreated(Owner(name));
            return probe;
        }
        OrderProbe a = Start("a");
        OrderProbe b = Start("b");
        OrderProbe c = Start("c");
        registry.Tick(1.0 / 60.0);
        Assert.Equal(["a", "b", "c"], trace);

        b.OnDestroy(Owner("b"));
        trace.Clear();
        registry.Tick(1.0 / 60.0);
        Assert.Equal(["a", "c"], trace);

        b.OnCreated(Owner("b"));
        trace.Clear();
        registry.Tick(1.0 / 60.0);
        Assert.Equal(["a", "c", "b"], trace);

        foreach (OrderProbe probe in new[] { a, b, c }) probe.OnDestroy(Owner("owner"));
        trace.Clear();
        registry.Tick(1.0 / 60.0);
        Assert.Empty(trace);
    }

}
