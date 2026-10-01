namespace EmptyEngine.Audio;

/// <summary>層ごとの音量を調整できる、クリップの再生口</summary>
/// <remarks>
/// クリップの層（元のチャンネルを 2 つずつ組にしたもの）を層ごとの音量で混ぜて鳴らす。
/// 層の音量の既定は先頭の層だけ 1 で残りは 0。<see cref="IDisposable.Dispose"/> で止まり、再開はできない
/// </remarks>
public interface IAudioVoice : IDisposable
{
    /// <summary>層の数</summary>
    int LayerCount { get; }

    /// <summary>全体の音量</summary>
    float Volume { get; set; }

    /// <summary>クリップの先頭からの再生位置（秒）</summary>
    double Time { get; }

    /// <summary>鳴っているか</summary>
    /// <remarks>ループしない再生が末尾まで鳴り終わると <c>false</c> になる。鳴り終わっても Dispose は要る</remarks>
    bool IsPlaying { get; }

    /// <summary>層の音量</summary>
    float GetLayerVolume(int layer);

    /// <summary>層の音量の設定</summary>
    /// <remarks>鳴っている間の変更は、先読み分を挟んでなめらかに移る</remarks>
    void SetLayerVolume(int layer, float volume);

    /// <summary>指定位置からの再生開始</summary>
    /// <param name="startSeconds">鳴らし始める位置（秒）</param>
    /// <param name="loop">
    /// 繰り返すか。繰り返すときはループ区間（クリップに指定が無ければ全体）を回し、繰り返さないときはクリップの末尾で止まる
    /// </param>
    /// <param name="volume">全体の音量（<see cref="Volume"/> の初期値）</param>
    void Play(double startSeconds = 0d, bool loop = false, float volume = 1f);
}
