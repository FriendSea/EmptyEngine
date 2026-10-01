namespace EmptyEngine.Audio;

/// <summary>一発再生の手</summary>
/// <remarks><see cref="IDisposable.Dispose"/> で止まる。鳴り終わったあとの Dispose は何もしない</remarks>
public interface IAudioPlayback : IDisposable
{
    /// <summary>鳴っているか</summary>
    bool IsPlaying { get; }

    /// <summary>鳴り始めてからの再生位置（秒）</summary>
    double Time { get; }
}
