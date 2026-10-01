namespace EmptyEngine.Graphics;

/// <summary>テクスチャ内の矩形領域を正規化 UV（0..1・左上原点）で表すもの</summary>
public struct Rect
{
    /// <summary>左上の U 座標（0..1）</summary>
    public float X;

    /// <summary>左上の V 座標（0..1・上が 0）</summary>
    public float Y;

    /// <summary>幅（U 方向 0..1）</summary>
    public float Width;

    /// <summary>高さ（V 方向 0..1）</summary>
    public float Height;

    public Rect(float x, float y, float width, float height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    /// <summary>テクスチャ全体を指す矩形（0, 0, 1, 1）</summary>
    public static Rect Full => new(0f, 0f, 1f, 1f);
}
