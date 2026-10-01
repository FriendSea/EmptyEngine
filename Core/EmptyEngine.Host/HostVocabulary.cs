namespace EmptyEngine.Host;

/// <summary>何を建て直すか</summary>
internal enum RestartScope : byte
{
    /// <summary>ランタイムとエディタの両方</summary>
    All = 0,
    /// <summary>ランタイムだけ</summary>
    Runtime = 1,
    /// <summary>エディタだけ</summary>
    Editor = 2,
}

/// <summary>Host が見ている片側の状態</summary>
internal enum HostLinkState : byte
{
    Stopped = 0,
    Running = 1,
    /// <summary>watcher は生きているが繋がっていない状態</summary>
    BuildingOrRestarting = 2,
    /// <summary>接続が維持されているが watcher は停止している状態</summary>
    ConnectedWatcherStopped = 3,
}

/// <summary>ログ 1 行の重大度（Host のコンソールでの色）</summary>
internal enum HostLogSeverity : byte
{
    Normal = 0,
    Warning = 1,
    Error = 2,
}
