namespace EmptyEngine.Core;

/// <summary>範囲指定して遅延ロードが可能なアセット本体データの抽象</summary>
public interface IAssetBinary
{
    /// <summary>バイト長</summary>
    long Length { get; }

    /// <summary>読み取り専用ストリームの取得</summary>
    /// <remarks>返却されるストリームは呼び出し側が破棄する</remarks>
    Stream OpenRead();
}
