using System.Numerics;
using EmptyEngine.ObjectModel;
using Xunit;

namespace EmptyEngine.WebGpu.Tests;

public sealed class CanvasProjectionTests
{
    [Theory]
    [InlineData(CanvasScaleMode.Fit)]
    [InlineData(CanvasScaleMode.Fill)]
    [InlineData(CanvasScaleMode.MatchWidth)]
    [InlineData(CanvasScaleMode.MatchHeight)]
    [InlineData(CanvasScaleMode.Stretch)]
    public void Canvas_preserves_near_and_far_surfaces_on_both_sides_of_zero(CanvasScaleMode mode)
    {
        var canvas = new CanvasScalerComponent(new RenderWorld()) { Mode = mode };
        canvas.Recompute(16f / 9f);
        float forward = CoordinateConvention.IsLeftHanded ? 1f : -1f;

        Vector3 near = Project(new Vector3(120f, -80f, -100f * forward), canvas.Projection);
        Vector3 far = Project(new Vector3(120f, -80f, 100f * forward), canvas.Projection);

        Assert.Equal(near.X, far.X);
        Assert.Equal(near.Y, far.Y);
        Assert.InRange(near.Z, 0f, 1f);
        Assert.InRange(far.Z, 0f, 1f);
        Assert.True(near.Z < far.Z, "A nearer mesh surface must pass depth testing against a farther surface.");
        Assert.Equal(0.5f, Project(Vector3.Zero, canvas.Projection).Z, precision: 6);
    }

    [Fact]
    public void Canvas_depth_range_maps_to_webgpu_clip_planes()
    {
        var canvas = new CanvasScalerComponent(new RenderWorld()) { NearPlane = -200f, FarPlane = 800f };
        canvas.Recompute(16f / 9f);
        float forward = CoordinateConvention.IsLeftHanded ? 1f : -1f;

        Assert.Equal(0f, Project(new Vector3(0f, 0f, -200f * forward), canvas.Projection).Z, precision: 6);
        Assert.Equal(1f, Project(new Vector3(0f, 0f, 800f * forward), canvas.Projection).Z, precision: 6);
        Assert.True(Project(new Vector3(0f, 0f, -201f * forward), canvas.Projection).Z < 0f);
        Assert.True(Project(new Vector3(0f, 0f, 801f * forward), canvas.Projection).Z > 1f);
    }

    [Theory]
    [InlineData(CanvasScaleMode.Fit, 1f, 1f, 0.5625f)]
    [InlineData(CanvasScaleMode.Fill, 1f, 1.7777778f, 1f)]
    [InlineData(CanvasScaleMode.MatchWidth, 1f, 1f, 0.5625f)]
    [InlineData(CanvasScaleMode.MatchHeight, 1f, 1.7777778f, 1f)]
    [InlineData(CanvasScaleMode.Stretch, 1f, 1f, 1f)]
    [InlineData(CanvasScaleMode.Fit, 1.7777778f, 1f, 1f)]
    public void Depth_projection_preserves_canvas_layout(CanvasScaleMode mode, float aspect, float x, float y)
    {
        var canvas = new CanvasScalerComponent(new RenderWorld()) { Mode = mode };
        canvas.Recompute(aspect);

        Vector3 corner = Project(new Vector3(960f, 540f, 0f), canvas.Projection);

        Assert.Equal(x, corner.X, precision: 5);
        Assert.Equal(y, corner.Y, precision: 5);
    }

    [Fact]
    public void Canvas_depth_is_independent_of_the_active_camera()
    {
        var world = new RenderWorld();
        var canvas = new CanvasScalerComponent(world);
        world.RegisterCanvasScaler(canvas);
        world.RecomputeCanvases(16f / 9f);
        Matrix4x4 projection = canvas.Projection;
        world.RegisterCamera(new CameraComponent(world)
        {
            Projection = CameraProjection.Perspective,
            NearPlane = 50f,
            FarPlane = 100f,
            FieldOfView = 120f,
        });

        world.RecomputeCanvases(16f / 9f);

        Assert.Equal(projection, canvas.Projection);
    }

    private static Vector3 Project(Vector3 point, Matrix4x4 projection)
    {
        Vector4 clip = Vector4.Transform(new Vector4(point, 1f), projection);
        return new Vector3(clip.X, clip.Y, clip.Z) / clip.W;
    }
}
