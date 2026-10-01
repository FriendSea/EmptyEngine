using System.Numerics;
using Xunit;

namespace EmptyEngine.ObjectModel.Tests;

public sealed class CoordinateConventionTests
{
    [Fact]
    public void TransformDirection_applies_rotation_without_translation_or_scale()
    {
        var transform = new TransformComponent();
        Assert.Equal(Vector3.UnitX, transform.TransformDirection(Vector3.UnitX));
        Assert.Equal(Vector3.UnitY, transform.TransformDirection(Vector3.UnitY));
        Assert.Equal(Vector3.UnitZ, transform.TransformDirection(Vector3.UnitZ));
        Matrix4x4 rotation = Matrix4x4.CreateRotationY(MathF.PI * 0.5f);
        transform.Matrix = Matrix4x4.CreateScale(3f, 4f, 5f)
                   * rotation
                   * Matrix4x4.CreateTranslation(10f, 20f, 30f);
        Vector3 expected = Vector3.TransformNormal(Vector3.UnitZ, rotation);

        Assert.True(Vector3.Distance(expected, transform.TransformDirection(Vector3.UnitZ)) < 1e-5f);
    }

    [Fact]
    public void Perspective_maps_semantic_forward_near_and_far_to_webgpu_depth()
    {
        const float near = 0.25f;
        const float far = 100f;
        Matrix4x4 projection = CoordinateConvention.CreatePerspectiveFieldOfView(
            MathF.PI / 3f,
            16f / 9f,
            near,
            far);

        float zSign = CoordinateConvention.IsLeftHanded ? 1f : -1f;
        AssertDepth(0f, Vector3.UnitZ * (zSign * near), projection);
        AssertDepth(1f, Vector3.UnitZ * (zSign * far), projection);
    }

    [Fact]
    public void Orthographic_maps_semantic_forward_near_and_far_to_webgpu_depth()
    {
        const float near = 0.25f;
        const float far = 100f;
        Matrix4x4 projection = CoordinateConvention.CreateOrthographic(16f, 9f, near, far);

        float zSign = CoordinateConvention.IsLeftHanded ? 1f : -1f;
        AssertDepth(0f, Vector3.UnitZ * (zSign * near), projection);
        AssertDepth(1f, Vector3.UnitZ * (zSign * far), projection);
    }

    private static void AssertDepth(float expected, Vector3 position, Matrix4x4 projection)
    {
        Vector4 clip = Vector4.Transform(new Vector4(position, 1f), projection);
        float depth = clip.Z / clip.W;
        Assert.True(MathF.Abs(expected - depth) < 1e-4f, $"expected {expected}, actual {depth}");
    }
}
