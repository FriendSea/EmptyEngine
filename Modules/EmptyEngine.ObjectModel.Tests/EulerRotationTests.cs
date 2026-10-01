using System.Numerics;
using EmptyEngine.ObjectModel.Editor;
using Xunit;

namespace EmptyEngine.ObjectModel.Tests;

/// <summary><see cref="EulerRotation"/> の往復検証</summary>
public sealed class EulerRotationTests
{
    private static void AssertSameRotation(Matrix4x4 expected, Matrix4x4 actual)
    {
        const float tolerance = 1e-3f;
        Assert.True(
            MathF.Abs(expected.M11 - actual.M11) < tolerance
            && MathF.Abs(expected.M12 - actual.M12) < tolerance
            && MathF.Abs(expected.M13 - actual.M13) < tolerance
            && MathF.Abs(expected.M21 - actual.M21) < tolerance
            && MathF.Abs(expected.M22 - actual.M22) < tolerance
            && MathF.Abs(expected.M23 - actual.M23) < tolerance
            && MathF.Abs(expected.M31 - actual.M31) < tolerance
            && MathF.Abs(expected.M32 - actual.M32) < tolerance
            && MathF.Abs(expected.M33 - actual.M33) < tolerance,
            $"rotation mismatch:\nexpected {expected}\nactual   {actual}");
    }

    [Fact]
    public void FromDegrees_matches_rotation_composition_order()
    {
        var degrees = new Vector3(30f, 50f, 70f);
        const float d2r = MathF.PI / 180f;

        Matrix4x4 composed =
            Matrix4x4.CreateRotationZ(degrees.Z * d2r)
            * Matrix4x4.CreateRotationX(degrees.X * d2r)
            * Matrix4x4.CreateRotationY(degrees.Y * d2r);

        AssertSameRotation(composed, EulerRotation.FromDegrees(degrees));
    }

    [Theory]
    [InlineData(0f, 0f, 0f)]
    [InlineData(30f, 0f, 0f)]
    [InlineData(0f, 45f, 0f)]
    [InlineData(0f, 0f, 60f)]
    [InlineData(30f, 45f, 60f)]
    [InlineData(-30f, 120f, -170f)]
    [InlineData(89f, -100f, 15f)]
    [InlineData(-89.9f, 10f, 20f)]
    [InlineData(179f, 179f, 179f)]
    public void Degrees_roundtrip_preserves_rotation(float x, float y, float z)
    {
        Matrix4x4 original = EulerRotation.FromDegrees(new Vector3(x, y, z));

        Vector3 extracted = EulerRotation.ToDegrees(original);
        Matrix4x4 recomposed = EulerRotation.FromDegrees(extracted);

        AssertSameRotation(original, recomposed);
    }

    [Theory]
    [InlineData(90f, 30f, 0f)]
    [InlineData(-90f, 0f, 45f)]
    [InlineData(90f, 30f, 45f)]
    public void Gimbal_lock_roundtrip_preserves_rotation(float x, float y, float z)
    {
        Matrix4x4 original = EulerRotation.FromDegrees(new Vector3(x, y, z));

        Vector3 extracted = EulerRotation.ToDegrees(original);
        Matrix4x4 recomposed = EulerRotation.FromDegrees(extracted);

        AssertSameRotation(original, recomposed);
    }

    [Fact]
    public void Quaternion_roundtrip_preserves_rotation()
    {
        var rotation = Quaternion.CreateFromYawPitchRoll(0.7f, -1.1f, 2.3f);

        Vector3 extracted = EulerRotation.ToDegrees(rotation);
        Matrix4x4 recomposed = EulerRotation.FromDegrees(extracted);

        AssertSameRotation(Matrix4x4.CreateFromQuaternion(rotation), recomposed);
    }
}
