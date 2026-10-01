using System.Numerics;
using EmptyEngine.ObjectModel;
using Xunit;

namespace EmptyEngine.Collision.Tests;

public sealed class Collider3DTests
{
    [Fact]
    public void Sphere_queries_use_all_three_axes()
    {
        var collider = new SphereCollider { Radius = 0.5f };

        Assert.True(collider.Overlap(new Vector3(0f, 0f, 0.75f), 0.25f));
        Assert.False(collider.Overlap(new Vector3(0f, 0f, 0.76f), 0.25f));

        Assert.True(collider.Raycast(new Vector3(0f, 0f, -2f), new Vector3(0f, 0f, 2f), out float t));
        Assert.Equal(0.375f, t, 5);

        Vector3 offset = collider.ResolvePenetration(new Vector3(0f, 0f, 0.25f), 0.5f);
        Assert.Equal(new Vector3(0f, 0f, 0.75f), offset);
    }

    [Fact]
    public void Box_is_a_three_dimensional_obb()
    {
        var collider = new BoxCollider { Size = new Vector3(2f, 4f, 6f) };

        Assert.True(collider.Overlap(new Vector3(0f, 0f, 3.25f), 0.25f));
        Assert.False(collider.Overlap(new Vector3(0f, 0f, 3.26f), 0.25f));

        Assert.True(collider.Raycast(new Vector3(0f, 0f, -5f), new Vector3(0f, 0f, 5f), out float t));
        Assert.Equal(0.2f, t, 5);

        Assert.Equal(new Vector3(1.5f, 0f, 0f), collider.ResolvePenetration(Vector3.Zero, 0.5f));
    }

    [Fact]
    public void Box_uses_the_owners_3d_transform()
    {
        var transform = new TransformComponent { Matrix = Matrix4x4.CreateRotationY(MathF.PI * 0.5f) };
        var collider = new BoxCollider { Size = new Vector3(2f, 2f, 4f) };
        collider.OnCreated(new TestObject(transform, collider));

        Assert.True(collider.Overlap(new Vector3(1.9f, 0f, 0f), 0f));
        Assert.False(collider.Overlap(new Vector3(0f, 0f, 1.1f), 0f));
        Assert.True(collider.Raycast(new Vector3(-3f, 0f, 0f), new Vector3(3f, 0f, 0f), out float t));
        Assert.Equal(1f / 6f, t, 5);
    }

    [Fact]
    public void Box_uses_transforms_from_the_owner_and_all_ancestors()
    {
        var root = new TestObject(new TransformComponent
        {
            Matrix = Matrix4x4.CreateTranslation(10f, 0f, 0f),
        });
        var parent = new TestObject(root, new TransformComponent
        {
            Matrix = Matrix4x4.CreateScale(2f, 1f, 1f)
                   * Matrix4x4.CreateRotationZ(MathF.PI * 0.5f),
        });
        var collider = new BoxCollider { Size = new Vector3(2f, 2f, 2f) };
        var child = new TestObject(parent,
            new TransformComponent { Matrix = Matrix4x4.CreateTranslation(2f, 0f, 0f) },
            collider);
        collider.OnCreated(child);

        Assert.True(collider.Overlap(new Vector3(10f, 5.9f, 0f), 0f));
        Assert.False(collider.Overlap(new Vector3(11.1f, 4f, 0f), 0f));
        Assert.True(collider.Raycast(
            new Vector3(10f, 1f, 0f),
            new Vector3(10f, 7f, 0f),
            out float t));
        Assert.Equal(1f / 6f, t, 5);
    }

    [Fact]
    public void Capsule_raycast_hits_the_cylindrical_body_in_depth()
    {
        var collider = new CapsuleCollider
        {
            Radius = 0.5f,
            Bottom = -1f,
            Top = 1f,
        };

        Assert.True(collider.Overlap(new Vector3(0f, 0f, 0.4f), 0.1f));
        Assert.False(collider.Overlap(new Vector3(0f, 0f, 0.61f), 0.1f));
        Assert.True(collider.Raycast(new Vector3(0f, 0f, -2f), new Vector3(0f, 0f, 2f), out float t));
        Assert.Equal(0.375f, t, 5);
        Assert.True(collider.Raycast(new Vector3(0f, 2f, 0f), new Vector3(0f, 1.5f, 0f), out float endpointT));
        Assert.Equal(1f, endpointT, 5);
    }

    [Fact]
    public void Registry_returns_nearest_hit_for_a_3d_segment()
    {
        var registry = new ColliderRegistry();
        var far = new SphereCollider { Radius = 1f, Offset = new Vector3(0f, 0f, 3f) };
        var near = new SphereCollider { Radius = 1f };
        registry.Register(far);
        registry.Register(near);

        ICollider? hit = registry.Raycast(
            new Vector3(0f, 0f, -5f),
            new Vector3(0f, 0f, 5f),
            out float t);

        Assert.Same(near, hit);
        Assert.Equal(0.4f, t, 5);
    }

    [Fact]
    public void Vector2_overloads_keep_xy_plane_queries_concise()
    {
        ICollider collider = new BoxCollider { Size = Vector3.One };

        Assert.True(collider.Overlap(Vector2.Zero, 0f));
        Assert.True(collider.Raycast(new Vector2(-1f, 0f), new Vector2(1f, 0f), out float t));
        Assert.Equal(0.25f, t, 5);
    }

    [Fact]
    public void Box_follows_its_owner_after_the_transform_moves()
    {
        var transform = new TransformComponent { Matrix = Matrix4x4.Identity };
        var collider = new BoxCollider { Size = Vector3.One };
        collider.OnCreated(new TestObject(transform, collider));

        Assert.True(collider.Overlap(Vector3.Zero, 0f));

        transform.Matrix = Matrix4x4.CreateTranslation(10f, 0f, 0f);
        collider.Sync();

        Assert.False(collider.Overlap(Vector3.Zero, 0f));
        Assert.True(collider.Overlap(new Vector3(10f, 0f, 0f), 0f));
    }

    [Fact]
    public void Box_follows_its_own_size_and_offset_after_they_change()
    {
        var collider = new BoxCollider { Size = Vector3.One };

        Assert.False(collider.Overlap(new Vector3(2f, 0f, 0f), 0f));

        collider.Size = new Vector3(6f, 1f, 1f);
        collider.Sync();
        Assert.True(collider.Overlap(new Vector3(2f, 0f, 0f), 0f));

        collider.Offset = new Vector3(-10f, 0f, 0f);
        collider.Sync();
        Assert.False(collider.Overlap(new Vector3(2f, 0f, 0f), 0f));
        Assert.True(collider.Overlap(new Vector3(-10f, 0f, 0f), 0f));
    }

    [Fact]
    public void Collider_takes_its_place_from_the_transform_at_creation()
    {
        var transform = new TransformComponent { Matrix = Matrix4x4.CreateTranslation(10f, 0f, 0f) };
        var collider = new SphereCollider { Radius = 0.5f };
        collider.OnCreated(new TestObject(transform, collider));

        transform.Matrix = Matrix4x4.CreateTranslation(50f, 0f, 0f);

        Assert.True(collider.Overlap(new Vector3(10f, 0f, 0f), 0f));
        Assert.False(collider.Overlap(new Vector3(50f, 0f, 0f), 0f));
    }

    private sealed class TestObject : IObject
    {
        private readonly IAttachable[] _attachables;

        public TestObject(params IAttachable[] attachables)
            : this(null, attachables)
        {
        }

        public TestObject(IObject? parent, params IAttachable[] attachables)
        {
            Parent = parent;
            _attachables = attachables;
        }

        public IObject? Parent { get; }

        public T? GetAttachable<T>() where T : class, IAttachable
            => _attachables.OfType<T>().FirstOrDefault();

        public IEnumerable<IAttachable> GetAllAttachables() => _attachables;
    }
}
