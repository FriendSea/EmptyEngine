namespace EmptyEngine.PlayerLoop;

/// <summary>各フレームの更新に必要なコンテキスト情報</summary>
public readonly record struct UpdateContext(
    TimeSpan DeltaTime,
    TimeSpan ElapsedTime,
    long FrameIndex);
