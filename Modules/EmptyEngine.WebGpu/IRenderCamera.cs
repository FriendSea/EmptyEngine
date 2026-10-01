using System.Numerics;

namespace EmptyEngine.WebGpu;

/// <summary>描画に使う視点。シーン内のコンポーネントである必要はない。</summary>
public interface IRenderCamera
{
    Matrix4x4 BuildViewMatrix();

    Matrix4x4 BuildProjectionMatrix(float aspect);

    Matrix4x4 BuildViewProjection(float aspect) => BuildViewMatrix() * BuildProjectionMatrix(aspect);
}
