using System.Collections;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace EmptyEngine.Tests;

/// <summary>書かれたログを整形済みの 1 件ずつ控えるロガー</summary>
/// <remarks>例外付きの 1 件はメッセージの後に改行を挟んで例外の文字列が続く。どのスレッドから書いてもよい</remarks>
public sealed class RecordingLogger<T> : ILogger<T>, IReadOnlyCollection<string>
{
    private readonly ConcurrentQueue<string> _entries = new();

    public int Count => _entries.Count;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        string message = formatter(state, exception);
        _entries.Enqueue(exception is null ? message : message + Environment.NewLine + exception);
    }

    public IEnumerator<string> GetEnumerator() => _entries.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
