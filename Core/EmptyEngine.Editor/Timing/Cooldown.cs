namespace EmptyEngine.Editor.Timing;

/// <summary>決めた間隔より短い再実行を落とす門</summary>
/// <remarks>時刻は単調増加の <see cref="Environment.TickCount64"/> で測る。操作は複数のスレッドから呼び出せる。</remarks>
internal sealed class Cooldown
{
    private const long Never = long.MinValue / 2;

    private readonly long _intervalMilliseconds;
    private long _lastTick = Never;

    /// <param name="interval">一度通してから次に通すまでの間隔</param>
    public Cooldown(TimeSpan interval) => _intervalMilliseconds = (long)interval.TotalMilliseconds;

    /// <summary>次に通せるまでの残り（<see cref="TimeSpan.Zero"/> なら今すぐ通せる）</summary>
    public TimeSpan Remaining
    {
        get
        {
            long elapsed = Environment.TickCount64 - Volatile.Read(ref _lastTick);
            return elapsed >= _intervalMilliseconds
                ? TimeSpan.Zero
                : TimeSpan.FromMilliseconds(_intervalMilliseconds - elapsed);
        }
    }

    /// <summary>通ったことの記録</summary>
    public void Mark() => Volatile.Write(ref _lastTick, Environment.TickCount64);

    /// <summary>通せるなら記録して <c>true</c></summary>
    public bool TryEnter()
    {
        if (Remaining > TimeSpan.Zero) return false;

        Mark();
        return true;
    }
}
