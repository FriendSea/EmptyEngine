using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace EmptyEngine.Editor.Hosting;

/// <summary>エディタの状態を触る作業の 1 本のスレッドへの直列化</summary>
public sealed class EditorDispatcher : IDisposable
{
    /// <summary>捨てるときにポンプの終わりを待つ長さ</summary>
    private const int JoinTimeoutMilliseconds = 1000;

    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cts = new();
    private readonly ILogger _logger;
    private bool _disposed;

    public EditorDispatcher(ILogger<EditorDispatcher> logger)
    {
        _logger = logger;
        _thread = new Thread(Pump)
        {
            IsBackground = true,
            Name = "EmptyEngine.Editor dispatcher",
        };
        _thread.Start();
    }

    /// <summary>いま呼び出しているのがディスパッチャのスレッドか</summary>
    public bool IsCurrent => Thread.CurrentThread == _thread;

    /// <summary>作業の投げっぱなしの積み込み</summary>
    public void Post(Action action)
    {
        if (_queue.IsAddingCompleted) return;

        try
        {
            _queue.Add((static state => ((Action)state!)(), action));
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>作業の積み込みと完了待ち</summary>
    public Task InvokeAsync(Action action)
    {
        if (IsCurrent)
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        return completion.Task;
    }

    /// <summary>結果を返す作業の積み込みと完了待ち</summary>
    public async Task<T> InvokeAsync<T>(Func<T> func)
    {
        T result = default!;
        await InvokeAsync(() =>
        {
            result = func();
        });
        return result;
    }

    /// <summary>非同期の作業の積み込みと完了待ち</summary>
    /// <remarks><paramref name="func"/> の <c>await</c> の継続もこのスレッドへ戻る</remarks>
    public Task InvokeAsync(Func<Task> func)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async () =>
        {
            try
            {
                await func();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        return completion.Task;
    }

    private void Pump()
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(this));

        try
        {
            foreach ((SendOrPostCallback callback, object? state) in _queue.GetConsumingEnumerable(_cts.Token))
            {
                try
                {
                    callback(state);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "A queued editor action threw and nobody caught it");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _queue.CompleteAdding();

        // 待機中のポンプが token を使うため、スレッドの終了を確認してから破棄する。
        if (IsCurrent || !_thread.Join(JoinTimeoutMilliseconds)) return;

        _cts.Dispose();
        _queue.Dispose();
    }

    private sealed class DispatcherSynchronizationContext : SynchronizationContext
    {
        private readonly EditorDispatcher _owner;

        public DispatcherSynchronizationContext(EditorDispatcher owner) => _owner = owner;

        public override void Post(SendOrPostCallback callback, object? state)
        {
            if (_owner._queue.IsAddingCompleted) return;
            try { _owner._queue.Add((callback, state)); }
            catch (InvalidOperationException) { }
        }

        public override void Send(SendOrPostCallback callback, object? state)
        {
            if (_owner.IsCurrent)
            {
                callback(state);
                return;
            }

            _owner.InvokeAsync(() => callback(state)).GetAwaiter().GetResult();
        }

        public override SynchronizationContext CreateCopy() => this;
    }
}
