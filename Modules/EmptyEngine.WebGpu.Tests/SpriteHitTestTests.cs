using System.Numerics;
using EmptyEngine.ObjectModel;
using EmptyEngine.World;
using Xunit;

namespace EmptyEngine.WebGpu.Tests;

public sealed class SpriteHitTestTests
{
    // design 矩形（x=200..400、y=150..250）を 1920x1080 の画面座標で正規化する。
    private const float Aspect = 1920f / 1080f;

    private static SpriteComponent BuildSprite(out RenderWorld renderWorld)
    {
        renderWorld = new RenderWorld();
        var canvas = new CanvasScalerComponent(renderWorld);
        var sprite = new SpriteComponent(renderWorld);
        var transform = new TransformComponent
        {
            Matrix = Matrix4x4.CreateScale(200f, 100f, 1f) * Matrix4x4.CreateTranslation(300f, 200f, 0f),
        };
        var owner = new GameObject("sprite", "Sprite", [transform, sprite]);
        var canvasOwner = new GameObject("canvas", "Canvas", [canvas], [owner]);

        canvas.OnDeserialized(canvasOwner);
        ((ILifecycleAttachable)sprite).OnDeserialized(owner);
        renderWorld.RecomputeCanvases(Aspect);
        return sprite;
    }

    [Fact]
    public void A_point_on_the_sprite_hits_it()
    {
        SpriteComponent sprite = BuildSprite(out _);

        Assert.True(sprite.HitTest(new Vector2(0.65625f, 0.3148148f)));      // 中心
        Assert.True(sprite.HitTest(new Vector2(0.7078125f, 0.3148148f)));    // 右端のすぐ内側
        Assert.True(sprite.HitTest(new Vector2(0.65625f, 0.2694444f)));      // 上端のすぐ内側
    }

    [Fact]
    public void A_point_off_the_sprite_misses_it()
    {
        SpriteComponent sprite = BuildSprite(out _);

        Assert.False(sprite.HitTest(new Vector2(0.7088542f, 0.3148148f)));   // 右端のすぐ外側
        Assert.False(sprite.HitTest(new Vector2(0.65625f, 0.2675926f)));     // 上端のすぐ外側
        Assert.False(sprite.HitTest(new Vector2(0.5f, 0.5f)));               // 画面中央（矩形の外）
    }

    [Fact]
    public void The_canvas_projection_of_the_frame_decides_the_rectangle()
    {
        SpriteComponent sprite = BuildSprite(out RenderWorld renderWorld);

        // 画面が細くなれば Fit の縮尺も変わる＝当たる範囲は前フレームの描画に追従する
        renderWorld.RecomputeCanvases(Aspect * 0.5f);

        Assert.False(sprite.HitTest(new Vector2(0.7078125f, 0.3148148f)));
    }
}
