namespace EmptyEngine.ObjectModel;

/// <summary>色として編集する型の RGBA チャンネルに対応するメンバ名を宣言する。</summary>
/// <remarks>各メンバは 0..1 の浮動小数点値を持ち、宣言する名前は型のメンバ名と一致すること。</remarks>
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class)]
public sealed class ColorChannelsAttribute(string red, string green, string blue, string alpha) : Attribute
{
    /// <summary>赤成分のメンバ名</summary>
    public string Red { get; } = red;

    /// <summary>緑成分のメンバ名</summary>
    public string Green { get; } = green;

    /// <summary>青成分のメンバ名</summary>
    public string Blue { get; } = blue;

    /// <summary>アルファのメンバ名</summary>
    public string Alpha { get; } = alpha;
}
