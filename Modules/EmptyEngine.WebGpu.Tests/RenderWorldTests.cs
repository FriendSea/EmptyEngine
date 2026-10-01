using EmptyEngine.ObjectModel;
using EmptyEngine.Tests;
using EmptyEngine.World;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EmptyEngine.WebGpu.Tests;

public sealed class RenderWorldTests
{
    [Fact]
    public void Render_state_is_isolated_per_world()
    {
        var left = new RenderWorld();
        var right = new RenderWorld();
        var leftSprite = new SpriteComponent(left);
        var rightSprite = new SpriteComponent(right);
        var leftCamera = new CameraComponent(left);
        var rightCamera = new CameraComponent(right);
        var leftCanvas = new CanvasScalerComponent(left);
        var leftOwner = new GameObject("left", "Left", [leftSprite, leftCamera, leftCanvas]);
        var rightOwner = new GameObject("right", "Right", [rightSprite, rightCamera]);

        ((ILifecycleAttachable)leftSprite).OnDeserialized(leftOwner);
        ((ILifecycleAttachable)rightSprite).OnDeserialized(rightOwner);
        leftCamera.OnDeserialized(leftOwner);
        rightCamera.OnDeserialized(rightOwner);
        leftCanvas.OnDeserialized(leftOwner);

        Assert.Equal(new ISpriteRenderer[] { leftSprite }, left.Renderers);
        Assert.Equal(new ISpriteRenderer[] { rightSprite }, right.Renderers);
        Assert.Same(leftCamera, left.ActiveCamera);
        Assert.Same(rightCamera, right.ActiveCamera);

        ((ILifecycleAttachable)leftSprite).OnDestroy(leftOwner);
        leftCamera.OnDestroy(leftOwner);
        leftCanvas.OnDestroy(leftOwner);

        Assert.Empty(left.Renderers);
        Assert.Null(left.ActiveCamera);
        Assert.Equal(new ISpriteRenderer[] { rightSprite }, right.Renderers);
        Assert.Same(rightCamera, right.ActiveCamera);
    }

    [Fact]
    public void SceneWorld_constructs_renderers_with_its_container_resource()
    {
        var renderWorld = new RenderWorld();
        using ServiceProvider services = new ServiceCollection()
            .AddSingleton(renderWorld)
            .BuildServiceProvider();
        using var world = new SceneWorld(assetResolver: null, log: null, services);
        byte[] blob = SceneBlob.Encode([
            new RootInstanceData("scene", new ObjectData(
                "root",
                "Root",
                [new ComponentData(typeof(SpriteComponent).FullName!, new SpriteComponent(new RenderWorld()))],
                []))
        ]);

        world.DeserializeSceneNow(blob, isPlaying: true);

        ISpriteRenderer renderer = Assert.Single(renderWorld.Renderers);
        Assert.IsType<SpriteComponent>(renderer);

        world.Dispose();
        Assert.Empty(renderWorld.Renderers);
    }
}
