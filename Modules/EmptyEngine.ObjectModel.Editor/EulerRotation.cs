using System.Numerics;

namespace EmptyEngine.ObjectModel.Editor;

/// <summary>インスペクタ表示用のオイラー角（度）と回転の相互変換</summary>
public static class EulerRotation
{
    private const float RadToDeg = 180f / MathF.PI;
    private const float DegToRad = MathF.PI / 180f;

    /// <summary>オイラー角（度）からの回転行列の合成</summary>
    public static Matrix4x4 FromDegrees(Vector3 degrees)
        => Matrix4x4.CreateFromYawPitchRoll(
            degrees.Y * DegToRad,
            degrees.X * DegToRad,
            degrees.Z * DegToRad);

    /// <summary>回転クォータニオンからのオイラー角（度）の取り出し</summary>
    public static Vector3 ToDegrees(Quaternion rotation)
        => ToDegrees(Matrix4x4.CreateFromQuaternion(rotation));

    /// <summary>回転行列からのオイラー角（度）の取り出し</summary>
    public static Vector3 ToDegrees(Matrix4x4 m)
    {
        float sinX = -m.M32;
        sinX = Math.Clamp(sinX, -1f, 1f);
        float x = MathF.Asin(sinX);

        float y;
        float z;
        if (MathF.Abs(sinX) < 0.999999f)
        {
            y = MathF.Atan2(m.M31, m.M33);
            z = MathF.Atan2(m.M12, m.M22);
        }
        else
        {
            y = MathF.Atan2(-m.M13, m.M11);
            z = 0f;
        }

        return new Vector3(x * RadToDeg, y * RadToDeg, z * RadToDeg);
    }
}
