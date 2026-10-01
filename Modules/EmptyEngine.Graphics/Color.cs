using System.Numerics;
using EmptyEngine.ObjectModel;

namespace EmptyEngine.Graphics;

/// <summary>各成分 0..1 の線形 RGBA カラー</summary>
[ColorChannels(nameof(Color.R), nameof(Color.G), nameof(Color.B), nameof(Color.A))]
public struct Color : IEquatable<Color>
{
    /// <summary>赤成分（0..1）</summary>
    public float R;

    /// <summary>緑成分（0..1）</summary>
    public float G;

    /// <summary>青成分（0..1）</summary>
    public float B;

    /// <summary>アルファ（不透明度 0..1）</summary>
    public float A;

    public Color(float r, float g, float b, float a)
    {
        R = r;
        G = g;
        B = b;
        A = a;
    }

    /// <summary>アルファを 1（不透明）とみなした生成</summary>
    public Color(float r, float g, float b) : this(r, g, b, 1f)
    {
    }

    /// <summary>不透明な白（1,1,1,1）</summary>
    public static Color White => new(1f, 1f, 1f, 1f);

    /// <summary>不透明な黒（0,0,0,1）</summary>
    public static Color Black => new(0f, 0f, 0f, 1f);

    /// <summary>完全な透明（0,0,0,0）</summary>
    public static Color Transparent => new(0f, 0f, 0f, 0f);

    /// <summary>0..255 の各成分からの生成</summary>
    public static Color FromBytes(byte r, byte g, byte b, byte a = 255)
        => new(r / 255f, g / 255f, b / 255f, a / 255f);

    /// <summary>RGB はそのままにアルファだけ差し替えた色</summary>
    public readonly Color WithAlpha(float a) => new(R, G, B, a);

    /// <summary><see cref="Vector4"/> への変換</summary>
    public static implicit operator Vector4(Color color) => new(color.R, color.G, color.B, color.A);

    /// <summary><see cref="Color"/> への変換</summary>
    public static implicit operator Color(Vector4 vector) => new(vector.X, vector.Y, vector.Z, vector.W);

    public readonly bool Equals(Color other) => R == other.R && G == other.G && B == other.B && A == other.A;

    public override readonly bool Equals(object? obj) => obj is Color other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(R, G, B, A);

    public static bool operator ==(Color left, Color right) => left.Equals(right);

    public static bool operator !=(Color left, Color right) => !left.Equals(right);

    public override readonly string ToString() => $"Color(R={R}, G={G}, B={B}, A={A})";
}
