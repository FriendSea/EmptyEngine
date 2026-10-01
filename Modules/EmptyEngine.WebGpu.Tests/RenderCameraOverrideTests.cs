using System.Numerics;
using EmptyEngine.ObjectModel;
using Xunit;

namespace EmptyEngine.WebGpu.Tests;

public sealed class RenderCameraOverrideTests
{
    [Fact]
    public void Module_does_not_own_scene_view_or_windowing()
    {
        var assembly = typeof(IRenderCamera).Assembly;
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name == "EmptyEngine.Windowing");
        Assert.Null(assembly.GetType("EmptyEngine.WebGpu.SceneViewCamera"));
        Assert.Null(assembly.GetType("EmptyEngine.WebGpu.SceneViewInput"));
    }

    [Fact]
    public void External_override_keeps_scene_camera_registration_independent()
    {
        var world = new RenderWorld();
        var first = new CameraComponent(world);
        var second = new CameraComponent(world);
        world.RegisterCamera(first);
        world.RegisterCamera(second);
        var external = new ExternalCamera(Matrix4x4.CreateTranslation(3, 0, 0), Matrix4x4.Identity);

        world.CameraOverride = external;
        Assert.Same(first, world.ActiveCamera);
        Assert.Same(external, world.ViewCamera);
        world.UnregisterCamera(first);
        Assert.Same(second, world.ActiveCamera);
        Assert.Same(external, world.ViewCamera);

        world.CameraOverride = null;
        Assert.Same(second, world.ViewCamera);
        world.UnregisterCamera(second);
        Assert.Null(world.ViewCamera);
        world.CameraOverride = external;
        Assert.Same(external, world.ViewCamera);
    }

    [Fact]
    public void Hit_testing_uses_an_external_camera_without_a_scene_component()
    {
        var world = new RenderWorld();
        var sprite = new SpriteComponent(world);
        ((ILifecycleAttachable)sprite).OnDeserialized(new Owner());
        Matrix4x4 projection = Matrix4x4.CreateScale(0.2f, 0.2f, 1f);
        world.CameraOverride = new ExternalCamera(Matrix4x4.Identity, projection);
        Assert.True(sprite.HitTest(new Vector2(0.5f, 0.5f)));

        world.CameraOverride = new ExternalCamera(Matrix4x4.CreateTranslation(3, 0, 0), projection);
        Assert.False(sprite.HitTest(new Vector2(0.5f, 0.5f)));
        Assert.True(sprite.HitTest(new Vector2(0.8f, 0.5f)));
        IRenderCamera camera = Assert.IsAssignableFrom<IRenderCamera>(world.ViewCamera);
        Assert.Equal(new Vector3(0.6f, 0, 0), Vector3.Transform(Vector3.Zero, camera.BuildViewProjection(1f)));
    }

    private sealed record ExternalCamera(Matrix4x4 View, Matrix4x4 Projection) : IRenderCamera
    {
        public Matrix4x4 BuildViewMatrix() => View;
        public Matrix4x4 BuildProjectionMatrix(float aspect) => Projection;
    }

    private sealed class Owner : IObject
    {
        public IObject? Parent => null;
        public T? GetAttachable<T>() where T : class, IAttachable => null;
        public IEnumerable<IAttachable> GetAllAttachables() => [];
    }
}
