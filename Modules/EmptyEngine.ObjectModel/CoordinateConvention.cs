using System.Numerics;

namespace EmptyEngine.ObjectModel;

/// <summary>エンジン全体で使用するワールド座標の手系</summary>
public enum CoordinateHandedness
{
    Left,
    Right,
}

/// <summary>ビルド時に選択された座標系の基底とビュー・射影変換を提供する</summary>
public static class CoordinateConvention
{
#if EMPTYENGINE_LEFT_HANDED && EMPTYENGINE_RIGHT_HANDED
#error EMPTYENGINE_LEFT_HANDED and EMPTYENGINE_RIGHT_HANDED cannot both be defined.
#elif EMPTYENGINE_LEFT_HANDED
    public const CoordinateHandedness Handedness = CoordinateHandedness.Left;
#elif EMPTYENGINE_RIGHT_HANDED
    public const CoordinateHandedness Handedness = CoordinateHandedness.Right;
#else
#error Set EmptyEngineHandedness to LeftHanded or RightHanded.
#endif

    public const bool IsLeftHanded = Handedness == CoordinateHandedness.Left;

    /// <summary>選択中の手系から System.Numerics の右手系透視射影を生成する</summary>
    public static Matrix4x4 CreatePerspectiveFieldOfView(
        float fieldOfView,
        float aspectRatio,
        float nearPlaneDistance,
        float farPlaneDistance)
    {
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
            fieldOfView,
            aspectRatio,
            nearPlaneDistance,
            farPlaneDistance);
        return ApplyViewHandedness(projection);
    }

    /// <summary>選択中の手系から System.Numerics の右手系平行投影を生成する</summary>
    public static Matrix4x4 CreateOrthographic(
        float width,
        float height,
        float nearPlaneDistance,
        float farPlaneDistance)
    {
        Matrix4x4 projection = Matrix4x4.CreateOrthographic(
            width,
            height,
            nearPlaneDistance,
            farPlaneDistance);
        return ApplyViewHandedness(projection);
    }

    private static Matrix4x4 ApplyViewHandedness(Matrix4x4 rightHandedProjection)
    {
#if EMPTYENGINE_LEFT_HANDED
        return Matrix4x4.CreateScale(1f, 1f, -1f) * rightHandedProjection;
#else
        return rightHandedProjection;
#endif
    }
}
