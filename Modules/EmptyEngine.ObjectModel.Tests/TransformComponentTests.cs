using System.Numerics;
using Xunit;

namespace EmptyEngine.ObjectModel.Tests;

/// <summary><see cref="TransformComponent.Scale"/> の符号復元の検証</summary>
public sealed class TransformComponentTests
{
    private static Matrix4x4 Trs(Vector3 position, Quaternion rotation, Vector3 scale)
        => Matrix4x4.CreateScale(scale)
         * Matrix4x4.CreateFromQuaternion(rotation)
         * Matrix4x4.CreateTranslation(position);

    [Fact]
    public void Scale_stays_positive_under_x_rotation_past_90()
    {
        var rotation = Quaternion.CreateFromYawPitchRoll(0f, 120f * MathF.PI / 180f, 0f);
        var transform = new TransformComponent { Matrix = Trs(Vector3.Zero, rotation, Vector3.One) };

        Assert.Equal(Vector3.One.X, transform.Scale.X, precision: 4);
        Assert.Equal(Vector3.One.Y, transform.Scale.Y, precision: 4);
        Assert.Equal(Vector3.One.Z, transform.Scale.Z, precision: 4);
    }

    [Fact]
    public void Scale_roundtrip_stays_positive_across_rotation_quadrants()
    {
        var position = new Vector3(1, 2, 3);
        var expected = new Vector3(2, 3, 4);
        var transform = new TransformComponent { Matrix = Trs(position, Quaternion.Identity, expected) };
        foreach (int degrees in new[] { 89, 90, 91, 179, 180, 181, 269, 270, 271 })
        {
            Vector3 previous = transform.Scale;
            var rotation = Quaternion.CreateFromYawPitchRoll(degrees * MathF.PI / 180f, 0f, 0f);
            transform.Matrix = Trs(position, rotation, previous);
            Assert.Equal(expected.X, transform.Scale.X, precision: 3);
            Assert.Equal(expected.Y, transform.Scale.Y, precision: 3);
            Assert.Equal(expected.Z, transform.Scale.Z, precision: 3);
        }
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(30f)]
    [InlineData(170f)]
    public void Negative_x_scale_survives_z_rotation(float degrees)
    {
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, degrees * MathF.PI / 180f);
        var transform = new TransformComponent { Matrix = Trs(Vector3.Zero, rotation, new Vector3(-2f, 3f, 1f)) };

        Vector3 scale = transform.Scale;

        Assert.Equal(-2f, scale.X, precision: 4);
        Assert.Equal(3f, scale.Y, precision: 4);
        Assert.Equal(1f, scale.Z, precision: 4);
    }

    [Fact]
    public void Negative_y_scale_is_normalized_to_negative_x()
    {
        var transform = new TransformComponent { Matrix = Trs(Vector3.Zero, Quaternion.Identity, new Vector3(2f, -3f, 1f)) };

        Vector3 scale = transform.Scale;

        Assert.Equal(-2f, scale.X, precision: 4);
        Assert.Equal(3f, scale.Y, precision: 4);
    }

    [Fact]
    public void World_position_uses_all_ancestor_transforms()
    {
        var rootTransform = new TransformComponent
        {
            Matrix = Matrix4x4.CreateTranslation(10f, 0f, 0f),
        };
        var root = new TestObject(null, rootTransform);
        var parentTransform = new TransformComponent
        {
            Matrix = Matrix4x4.CreateScale(2f, 1f, 1f)
                   * Matrix4x4.CreateRotationZ(MathF.PI * 0.5f),
        };
        var parent = new TestObject(root, parentTransform);
        var transform = new TransformComponent { Position = new Vector3(2f, 0f, 0f) };
        var child = new TestObject(parent, transform);
        transform.OnCreated(child);

        Assert.Equal(new Vector3(10f, 4f, 0f), transform.WorldPosition);
    }

    [Fact]
    public void Setting_world_position_converts_it_to_parent_local_space()
    {
        var parentTransform = new TransformComponent
        {
            Matrix = Matrix4x4.CreateScale(2f, 1f, 1f)
                   * Matrix4x4.CreateRotationZ(MathF.PI * 0.5f)
                   * Matrix4x4.CreateTranslation(10f, 0f, 0f),
        };
        var parent = new TestObject(null, parentTransform);
        var transform = new TransformComponent();
        var child = new TestObject(parent, transform);
        transform.OnCreated(child);

        transform.WorldPosition = new Vector3(10f, 4f, 0f);

        Assert.Equal(new Vector3(2f, 0f, 0f), transform.Position);
        Assert.Equal(new Vector3(10f, 4f, 0f), transform.WorldPosition);
    }

    private sealed class TestObject(IObject? parent, params IAttachable[] attachables) : IObject
    {
        public IObject? Parent { get; } = parent;

        public T? GetAttachable<T>() where T : class, IAttachable
            => attachables.OfType<T>().FirstOrDefault();

        public IEnumerable<IAttachable> GetAllAttachables() => attachables;
    }
}
