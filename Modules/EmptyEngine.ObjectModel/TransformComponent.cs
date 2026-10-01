using System.Numerics;

namespace EmptyEngine.ObjectModel;

/// <summary>オブジェクトの位置・回転・スケールを表すシーン基礎コンポーネント</summary>
public sealed class TransformComponent : ILifecycleAttachable
{
    private IObject? _owner;

    /// <summary>ローカル→ワールドの変換行列</summary>
    public Matrix4x4 Matrix { get; set; } = Matrix4x4.Identity;

    /// <summary>平行移動成分</summary>
    public Vector3 Position
    {
        get => Matrix.Translation;
        set
        {
            Matrix4x4 m = Matrix;
            m.Translation = value;
            Matrix = m;
        }
    }

    public Vector3 Scale
    {
        get
        {
            Matrix4x4 m = Matrix;
            float sx = new Vector3(m.M11, m.M12, m.M13).Length();
            float sy = new Vector3(m.M21, m.M22, m.M23).Length();
            float sz = new Vector3(m.M31, m.M32, m.M33).Length();
            if (m.GetDeterminant() < 0f) sx = -sx;
            return new Vector3(sx, sy, sz);
        }
        set
        {
            Matrix4x4 m = Matrix;
            Vector3 currentScale = Scale;
            if (currentScale.X != 0f) { m.M11 *= value.X / currentScale.X; m.M12 *= value.X / currentScale.X; m.M13 *= value.X / currentScale.X; }
            if (currentScale.Y != 0f) { m.M21 *= value.Y / currentScale.Y; m.M22 *= value.Y / currentScale.Y; m.M23 *= value.Y / currentScale.Y; }
            if (currentScale.Z != 0f) { m.M31 *= value.Z / currentScale.Z; m.M32 *= value.Z / currentScale.Z; m.M33 *= value.Z / currentScale.Z; }
            Matrix = m;
        }
    }

    /// <summary>位置とスケールの影響を受けずに、ローカル方向をワールド方向へ変換する</summary>
    public Vector3 TransformDirection(Vector3 localDirection)
    {
        if (localDirection == Vector3.Zero)
            return Vector3.Zero;

        if (Matrix4x4.Decompose(Matrix, out _, out Quaternion rotation, out _))
            return Vector3.Transform(localDirection, rotation);

        Vector3 transformed = Vector3.TransformNormal(localDirection, Matrix);
        float lengthSquared = transformed.LengthSquared();
        return lengthSquared > 0f
            ? transformed * (localDirection.Length() / MathF.Sqrt(lengthSquared))
            : Vector3.Zero;
    }

    /// <summary>The transform from this object's local space to world space.</summary>
    public Matrix4x4 WorldMatrix => _owner is null ? Matrix : GetWorldMatrix(_owner);

    /// <summary>The object's position in world space.</summary>
    public Vector3 WorldPosition
    {
        get => WorldMatrix.Translation;
        set
        {
            if (_owner?.Parent is not { } parent)
            {
                Position = value;
                return;
            }

            Matrix4x4 parentWorld = GetWorldMatrix(parent);
            if (!Matrix4x4.Invert(parentWorld, out Matrix4x4 inverse))
                throw new InvalidOperationException("Cannot set world position because the parent transform is not invertible.");

            Position = Vector3.Transform(value, inverse);
        }
    }

    /// <summary>Builds the local-to-world matrix for an object and all of its ancestors.</summary>
    public static Matrix4x4 GetWorldMatrix(IObject? owner)
    {
        Matrix4x4 world = Matrix4x4.Identity;
        for (IObject? node = owner; node is not null; node = node.Parent)
            world *= node.GetAttachable<TransformComponent>()?.Matrix ?? Matrix4x4.Identity;
        return world;
    }

    /// <summary>平行移動・Z 軸回転・スケールから合成</summary>
    public static TransformComponent FromTrs(Vector3 position, float rotationZRadians, Vector3 scale)
        => new()
        {
            Matrix = Matrix4x4.CreateScale(scale)
                * Matrix4x4.CreateRotationZ(rotationZRadians)
                * Matrix4x4.CreateTranslation(position),
        };

    public void Rotate(float x, float y, float z)
    {
        Matrix4x4 m = Matrix;
        m = Matrix4x4.CreateRotationX(x / 180 * MathF.PI) * m;
        m = Matrix4x4.CreateRotationY(y / 180 * MathF.PI) * m;
        m = Matrix4x4.CreateRotationZ(z / 180 * MathF.PI) * m;
        Matrix = m;
    }

    public void OnCreated(IObject owner) => _owner = owner;

    public void OnDeserialized(IObject owner) => _owner = owner;

    public void OnDestroy(IObject owner) => _owner = null;
}
