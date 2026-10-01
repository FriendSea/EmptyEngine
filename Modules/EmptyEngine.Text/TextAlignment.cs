namespace EmptyEngine.Text;

/// <summary>文字列の横方向の揃え方</summary>
/// <remarks>決めるのはテクスチャの中で行がどう並ぶかだけ。テクスチャを原点に対してどう置くかは描画側の責任</remarks>
public enum TextAlignment
{
    /// <summary>センタリング</summary>
    Center,

    /// <summary>左詰め</summary>
    Left,

    /// <summary>右詰め</summary>
    Right,
}
