using EmptyEngine.Core;
using EmptyEngine.ObjectModel;

namespace EmptyEngine.Audio;

/// <summary>インポート済みの音声クリップ</summary>
[Asset]
public sealed class AudioClipAsset
{
    /// <summary>音声ファイル本体（エンコード済みバイト列）</summary>
    public IAssetBinary Source = null!;

    /// <summary>層ごとの本体の長さ（バイト）</summary>
    /// <remarks>
    /// 空なら本体全体が 1 本のストリーム。2 つ以上なら、元のチャンネルを 2 つずつ組にした層のストリームを
    /// この並びの順に連結してある（一発再生で鳴るのは先頭の層だけ）
    /// </remarks>
    public int[] LayerLengths = [];

    /// <summary>繰り返しの開始位置（エンコード後のサンプルレートでのフレーム番号）</summary>
    public long LoopStartFrame;

    /// <summary>繰り返しの終端（このフレームは含まない）</summary>
    /// <remarks>0 ならループ指定なし。ループ再生ではクリップ全体を繰り返す</remarks>
    public long LoopEndFrame;
}
