using System.Numerics;

namespace EmptyEngine.ObjectModel;

/// <summary><see cref="Vector2"/> と <see cref="Vector3"/> の相互変換の拡張メソッド群</summary>
public static class VectorExtensions
{
    /// <summary>Vector3 の XY 成分からの Vector2 の生成</summary>
    public static Vector2 ToVector2(this Vector3 value) => new(value.X, value.Y);

    /// <summary>Vector2 への Z 成分の追加による Vector3 の生成</summary>
    public static Vector3 ToVector3(this Vector2 value, float z = 0f) => new(value.X, value.Y, z);
}
