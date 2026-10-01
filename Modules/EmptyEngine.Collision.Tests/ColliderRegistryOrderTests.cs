using System.Numerics;
using Xunit;

namespace EmptyEngine.Collision.Tests;

/// <summary>登録・解除を跨いだ在籍と並びの検証</summary>
public sealed class ColliderRegistryOrderTests
{
    private static SphereCollider Sphere(float x) =>
        new() { Radius = 0.25f, Offset = new Vector3(x, 0f, 0f) };

    [Fact]
    public void Registration_order_and_queries_follow_removal_and_re_registration()
    {
        var registry = new ColliderRegistry();
        SphereCollider a = Sphere(0);
        SphereCollider b = Sphere(1);
        SphereCollider c = Sphere(2);
        registry.Register(a);
        registry.Register(b);
        registry.Register(c);
        registry.Register(b);
        registry.Unregister(Sphere(5));
        Assert.Equal(new ICollider[] { a, b, c }, registry.All);
        Assert.Same(b, registry.Find(Vector3.UnitX));

        registry.Unregister(b);
        Assert.Equal(new ICollider[] { a, c }, registry.All);
        Assert.Null(registry.Find(Vector3.UnitX));

        registry.Register(b);
        Assert.Equal(new ICollider[] { a, c, b }, registry.All);
        Assert.Same(b, registry.Find(Vector3.UnitX));

        foreach (ICollider shape in new ICollider[] { a, b, c }) registry.Unregister(shape);
        Assert.Empty(registry.All);
        Assert.Null(registry.Find(Vector3.Zero));
    }

}
