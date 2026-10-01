using EmptyEngine.Editor.ViewModels;

using Xunit;

namespace EmptyEngine.Tests;

/// <summary>「描いている最中に中身が変わっても壊れない」ことの担保</summary>
public class SnapshotRenderingTests
{
    [Fact]
    public async Task An_in_progress_snapshot_enumeration_survives_a_completed_write()
    {
        var collection = new SnapshotCollection<string> { "a", "b" };
        IReadOnlyList<string> taken = collection.Snapshot;
        using IEnumerator<string> reader = taken.GetEnumerator();
        Assert.True(reader.MoveNext());
        Assert.Equal("a", reader.Current);

        await Task.Run(() =>
        {
            collection.Add("c");
            collection.RemoveAt(0);
        }).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(reader.MoveNext());
        Assert.Equal("b", reader.Current);
        Assert.False(reader.MoveNext());
        Assert.Equal(["a", "b"], taken);
        Assert.Equal(["b", "c"], collection.Snapshot);
    }

}
