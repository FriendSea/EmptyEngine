using System.Collections.Concurrent;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Assets;
using EmptyEngine.Editor.Authoring;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EmptyEngine.Tests;

/// <summary>アセットの自動保存の予約と書き込みの検証</summary>
public sealed class AssetSaveQueueTests
{
    [Fact]
    public async Task Switching_targets_writes_the_snapshot_that_was_still_waiting()
    {
        var importer = new Importer();
        await using var saves = new AssetSaveQueue(NullLogger.Instance);

        saves.Schedule(importer, "a", Asset(1), TimeSpan.FromMinutes(1));
        saves.Flush();
        saves.Schedule(importer, "b", Asset(2), TimeSpan.FromMinutes(1));

        await saves.DisposeAsync();
        Assert.Equal([("a", 1), ("b", 2)], importer.Saved);
    }

    [Fact]
    public async Task Only_the_last_reservation_of_a_quiet_period_is_written()
    {
        var importer = new Importer();
        await using var saves = new AssetSaveQueue(NullLogger.Instance);

        saves.Schedule(importer, "a", Asset(1), TimeSpan.FromMinutes(1));
        saves.Schedule(importer, "a", Asset(2), TimeSpan.FromMinutes(1));
        saves.Schedule(importer, "a", Asset(3), TimeSpan.FromMinutes(1));
        saves.Flush();

        await saves.DisposeAsync();
        Assert.Equal([("a", 3)], importer.Saved);
    }

    [Fact]
    public async Task Nothing_is_written_when_no_reservation_is_waiting()
    {
        var importer = new Importer();
        var saves = new AssetSaveQueue(NullLogger.Instance);

        saves.Flush();
        await saves.DisposeAsync();

        Assert.Empty(importer.Saved);
    }

    [Fact]
    public async Task Writes_do_not_overlap_and_a_failure_does_not_stop_later_saves()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = new RecordingLogger<AssetSaveQueue>();
        var importer = new Importer(async value =>
        {
            if (value != 1) return;

            started.SetResult();
            await release.Task;
            throw new IOException("first failed");
        });

        await using var saves = new AssetSaveQueue(log);
        saves.Schedule(importer, "a", Asset(1), TimeSpan.Zero);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        saves.Schedule(importer, "a", Asset(2), TimeSpan.Zero);
        saves.Schedule(importer, "b", Asset(3), TimeSpan.Zero);
        Assert.Equal(1, importer.Started);

        release.SetResult();
        await saves.DisposeAsync();

        Assert.Equal([("a", 2), ("b", 3)], importer.Saved);
        Assert.Contains(log, line => line.Contains("Failed to save asset 'a': first failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Waiting_for_the_flush_means_the_write_already_landed()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var importer = new Importer(async _ => await release.Task);
        await using var saves = new AssetSaveQueue(NullLogger.Instance);

        saves.Schedule(importer, "a", Asset(1), TimeSpan.FromMinutes(1));
        Task flushed = saves.FlushAsync();
        Assert.False(flushed.IsCompleted);
        Assert.Empty(importer.Saved);

        release.SetResult();
        await flushed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([("a", 1)], importer.Saved);
    }

    [Fact]
    public async Task A_reservation_made_after_disposal_is_refused()
    {
        var importer = new Importer();
        var saves = new AssetSaveQueue(NullLogger.Instance);
        await saves.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(
            () => saves.Schedule(importer, "a", Asset(1), TimeSpan.Zero));
    }

    private static AuthoringObject Asset(int value)
    {
        var data = new FieldValue();
        data.Add("Number", new FieldValue { Integer = value });
        return new AuthoringObject(
            TestSchemas.Object("Value", ("Number", TestSchemas.Scalar("System.Int32"))), data);
    }

    /// <summary>書かれた順と重なりを控えるだけのインポータ</summary>
    private sealed class Importer(Func<int, Task>? beforeSave = null) : IAssetImporter
    {
        private int _started;

        public ConcurrentQueue<(string Path, int Value)> Saved { get; } = new();

        public int Started => Volatile.Read(ref _started);

        public IReadOnlyCollection<string> SupportedExtensions => [".test"];

        public Task<AssetImportResult> ImportAsync(
            AssetImportRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task SaveAsync(
            AuthoringObject asset, string sourcePath, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _started);

            int value = (int)asset.Data.Get("Number")!.Integer;
            if (beforeSave is not null) await beforeSave(value);

            Saved.Enqueue((sourcePath, value));
        }
    }
}
