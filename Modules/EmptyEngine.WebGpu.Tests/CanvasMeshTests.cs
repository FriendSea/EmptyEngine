using EmptyEngine.Serialization;
using EmptyEngine.ObjectModel;
using EmptyEngine.World;
using Xunit;

namespace EmptyEngine.WebGpu.Tests;

public sealed class CanvasMeshTests
{
    [Fact]
    public void Explicit_canvas_depth_range_survives_serialization()
    {
        var canvas = new CanvasScalerComponent(new RenderWorld()) { NearPlane = -200f, FarPlane = 800f };
        byte[] encoded = TypeSerializers.Serialize(typeof(CanvasScalerComponent), canvas);

        var restored = (CanvasScalerComponent)TypeSerializers.Deserialize(typeof(CanvasScalerComponent), encoded);

        Assert.Equal(-200f, restored.NearPlane);
        Assert.Equal(800f, restored.FarPlane);
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(0)]
    [InlineData(5)]
    public void Mesh_and_sprite_in_the_same_canvas_share_a_layer(int sortOrder)
    {
        var world = new RenderWorld();
        var canvas = new CanvasScalerComponent(world) { SortOrder = sortOrder };
        var sprite = new SpriteComponent(world);
        var mesh = new MeshComponent(world);
        var panel = new GameObject("panel", "Panel", [sprite]);
        var model = new GameObject("model", "Model", [mesh]);
        _ = new GameObject("canvas", "Canvas", [canvas], [panel, model]);
        ((ILifecycleAttachable)sprite).OnDeserialized(panel);
        ((ILifecycleAttachable)mesh).OnDeserialized(model);

        // Canvas sorting can preserve registration order only when the layers match.
        Assert.Equal(new ISpriteRenderer[] { sprite, mesh }, world.Renderers);
        Assert.Equal(((ISpriteRenderer)sprite).RenderLayer, mesh.RenderLayer);
        Assert.Equal(SpriteRenderSupport.CanvasRenderLayer + sortOrder, mesh.RenderLayer);
    }

    [Fact]
    public void Mesh_follows_the_nearest_canvas_and_live_sort_order_changes()
    {
        var world = new RenderWorld();
        var outerCanvas = new CanvasScalerComponent(world) { SortOrder = 10 };
        var innerCanvas = new CanvasScalerComponent(world) { SortOrder = 20 };
        var mesh = new MeshComponent(world);
        var owner = new GameObject("mesh", "Mesh", [mesh]);
        var group = new GameObject("group", "Group", [], [owner]);
        var inner = new GameObject("inner", "Inner", [innerCanvas], [group]);
        _ = new GameObject("outer", "Outer", [outerCanvas], [inner]);
        ((ILifecycleAttachable)mesh).OnDeserialized(owner);

        Assert.Equal(SpriteRenderSupport.CanvasRenderLayer + 20, mesh.RenderLayer);
        innerCanvas.SortOrder = -3;
        Assert.Equal(SpriteRenderSupport.CanvasRenderLayer - 3, mesh.RenderLayer);
    }

    [Fact]
    public void Mesh_outside_a_canvas_keeps_its_world_layer()
    {
        var world = new RenderWorld();
        var mesh = new MeshComponent(world);
        var owner = new GameObject("mesh", "Mesh", [mesh]);
        ((ILifecycleAttachable)mesh).OnDeserialized(owner);

        Assert.Null(SpriteRenderSupport.FindCanvas(owner));
        Assert.Equal(-100, mesh.RenderLayer);
    }
}
