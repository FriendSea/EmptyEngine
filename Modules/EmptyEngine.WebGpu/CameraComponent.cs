using System.Numerics;
using EmptyEngine.ObjectModel;

namespace EmptyEngine.WebGpu;

/// <summary>カメラの投影方式</summary>
public enum CameraProjection
{
    /// <summary>平行投影</summary>
    Orthographic,

    /// <summary>透視投影</summary>
    Perspective,
}

/// <summary>ビュー射影行列を作るカメラ</summary>
public sealed class CameraComponent : ILifecycleAttachable, IRenderCamera
{
    private readonly RenderWorld _renderWorld;
    private IObject? _owner;

    public CameraComponent(RenderWorld renderWorld) => _renderWorld = renderWorld;

    /// <summary>投影方式（平行投影／透視投影）</summary>
    public CameraProjection Projection { get; set; } = CameraProjection.Orthographic;

    /// <summary>平行投影時に縦方向に見える範囲の半分（world 単位）</summary>
    public float Size { get; set; } = 1f;

    /// <summary>透視投影時の縦方向視野角（度）</summary>
    public float FieldOfView { get; set; } = 60f;

    /// <summary>透視投影のニア平面</summary>
    public float NearPlane { get; set; } = 0.1f;

    /// <summary>描画する最遠距離</summary>
    public float FarPlane { get; set; } = 1000f;

    public void OnCreated(IObject owner)
    {
    }

    public void OnDeserialized(IObject owner)
    {
        _owner = owner;
        _renderWorld.RegisterCamera(this);
    }

    public void OnDestroy(IObject owner)
    {
        _renderWorld.UnregisterCamera(this);
        _owner = null;
    }

    /// <summary>世界→クリップ空間のビュー射影行列の生成</summary>
    public Matrix4x4 BuildViewProjection(float aspect)
    {
        Matrix4x4 view = BuildViewMatrix();
        Matrix4x4 projection = BuildProjectionMatrix(aspect);
        return view * projection;
    }

    public Matrix4x4 BuildViewMatrix()
    {
        Matrix4x4 world = SpriteRenderSupport.ReadTransform(_owner);
        return Matrix4x4.Invert(world, out Matrix4x4 view) ? view : Matrix4x4.Identity;
    }

    public Matrix4x4 BuildProjectionMatrix(float aspect)
        => Projection == CameraProjection.Perspective
            ? BuildPerspective(aspect)
            : BuildOrthographic(aspect);

    /// <summary>指定アスペクト比で画面に映るワールド範囲</summary>
    public Vector2 GetVisibleWorldSize(float aspect)
    {
        float a = MathF.Max(aspect, 1e-4f);
        float height = 2f * MathF.Max(Size, 1e-4f);
        return new Vector2(height * a, height);
    }

    private Matrix4x4 BuildOrthographic(float aspect)
    {
        Vector2 size = GetVisibleWorldSize(aspect);
        float near = MathF.Max(NearPlane, 0f);
        float far = MathF.Max(FarPlane, near + 1e-3f);
        return CoordinateConvention.CreateOrthographic(size.X, size.Y, near, far);
    }

    private Matrix4x4 BuildPerspective(float aspect)
    {
        float a = MathF.Max(aspect, 1e-4f);
        float fov = Math.Clamp(FieldOfView, 1f, 179f) * (MathF.PI / 180f);
        float near = MathF.Max(NearPlane, 1e-3f);
        float far = MathF.Max(FarPlane, near + 1e-3f);
        return CoordinateConvention.CreatePerspectiveFieldOfView(fov, a, near, far);
    }
}
