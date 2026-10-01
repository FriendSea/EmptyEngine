using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EmptyEngine.Editor.Assets;

/// <summary>ソースアセットの変更が静まったところで 1 回だけ知らせる監視</summary>
internal sealed class SourceAssetWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _debounce;
    private readonly Action _onChanged;
    private readonly ILogger _logger;
    private readonly TimeSpan _quietPeriod;
    private readonly string _rootPath;
    private int _disposed;

    /// <summary>指定フォルダ以下の再帰監視</summary>
    public SourceAssetWatcher(string rootPath, Action onChanged, TimeSpan? quietPeriod = null, ILogger? logger = null)
    {
        _rootPath = Path.GetFullPath(rootPath);
        _onChanged = onChanged;
        _logger = logger ?? NullLogger.Instance;
        _quietPeriod = quietPeriod ?? TimeSpan.FromMilliseconds(400);
        _debounce = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);

        _watcher = new FileSystemWatcher(rootPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024,
        };

        _watcher.Created += OnFileEvent;
        _watcher.Changed += OnFileEvent;
        _watcher.Deleted += OnFileEvent;
        _watcher.Renamed += OnFileEvent;
        _watcher.Error += OnError;
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>監視の開始</summary>
    public static SourceAssetWatcher? TryStart(string rootPath, Action onChanged, ILogger? logger = null)
    {
        try
        {
            if (!Directory.Exists(rootPath)) return null;
            return new SourceAssetWatcher(rootPath, onChanged, logger: logger);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(
                "Asset watcher could not start for {Root}: {Error}. Imports will only be checked when the editor regains focus.",
                rootPath, ex.Message);
            return null;
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        if (IsIgnored(e.FullPath)
            && (e is not RenamedEventArgs renamed || IsIgnored(renamed.OldFullPath))) return;

        Schedule();
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        _logger.LogWarning("Asset watcher overflowed ({Error}); scheduling a full check.", e.GetException().Message);
        Schedule();
    }

    private void Schedule()
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        try { _debounce.Change(_quietPeriod, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    private void Fire()
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        try
        {
            _onChanged();
        }
        catch (Exception ex)
        {
            _logger.LogError("Asset watcher callback failed: {Error}", ex.Message);
        }
    }

    private bool IsIgnored(string fullPath)
    {
        if (fullPath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) return true;

        string relative;
        try { relative = Path.GetRelativePath(_rootPath, fullPath); }
        catch { return false; }

        foreach (string segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment.Length > 1 && segment[0] == '.') return true;
        }

        return false;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _debounce.Dispose();
    }
}
