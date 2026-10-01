using EmptyEngine.Editor.Assets;
using Xunit;

namespace EmptyEngine.Tests;

/// <summary>ソース監視の発火規則の検証</summary>
public sealed class SourceAssetWatcherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ee-watch-" + Guid.NewGuid().ToString("N"));

    public SourceAssetWatcherTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Fires_once_for_a_burst_of_changes()
    {
        var fired = new CountdownEvent(1);
        int count = 0;
        using var watcher = new SourceAssetWatcher(_root, () => { Interlocked.Increment(ref count); fired.Signal(); },
            quietPeriod: TimeSpan.FromMilliseconds(150));

        for (int i = 0; i < 20; i++) File.WriteAllText(Path.Combine(_root, $"Thing{i}.scene"), "{}");

        Assert.True(fired.Wait(TimeSpan.FromSeconds(5)), "The watcher did not fire after writing a source.");
        Thread.Sleep(500);
        Assert.Equal(1, Volatile.Read(ref count));
    }

    [Fact]
    public void Ignores_its_own_bookkeeping_writes()
    {
        var fired = new ManualResetEventSlim(false);
        using var watcher = new SourceAssetWatcher(_root, () => fired.Set(),
            quietPeriod: TimeSpan.FromMilliseconds(150));

        File.WriteAllText(Path.Combine(_root, "Thing.scene.meta"), "{\"guid\":\"abc\"}");
        Directory.CreateDirectory(Path.Combine(_root, ".artifacts"));
        File.WriteAllText(Path.Combine(_root, ".artifacts", "some.stamp"), "x");

        Assert.False(fired.Wait(TimeSpan.FromSeconds(1)), "The watcher fired on its own writes (.meta / internal folders).");

        File.WriteAllText(Path.Combine(_root, "Thing.scene"), "{}");
        Assert.True(fired.Wait(TimeSpan.FromSeconds(5)), "The source change was not picked up.");
    }

    [Fact]
    public void Fires_for_a_move_from_outside_the_watched_root()
    {
        var fired = new ManualResetEventSlim(false);
        string outside = Path.Combine(Path.GetTempPath(), "ee-watch-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            string staged = Path.Combine(outside, "Thing.scene");
            File.WriteAllText(staged, "{}");

            using var watcher = new SourceAssetWatcher(_root, () => fired.Set(),
                quietPeriod: TimeSpan.FromMilliseconds(150));

            File.Move(staged, Path.Combine(_root, "Thing.scene"));

            Assert.True(fired.Wait(TimeSpan.FromSeconds(5)), "The watcher did not fire for a file moved in from outside.");
        }
        finally
        {
            try { Directory.Delete(outside, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Missing_root_yields_no_watcher_instead_of_throwing()
    {
        Assert.Null(SourceAssetWatcher.TryStart(Path.Combine(_root, "does-not-exist"), () => { }));
    }
}
