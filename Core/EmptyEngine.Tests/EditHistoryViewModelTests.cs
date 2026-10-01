using EmptyEngine.Editor.State;
using EmptyEngine.Editor.ViewModels;
using Xunit;

namespace EmptyEngine.Tests;

/// <summary>undo/redo の進行そのものの検証</summary>
/// <remarks>履歴の記録、連続編集の統合、取り消し・やり直しを検証する。</remarks>
public sealed class EditHistoryViewModelTests
{
    /// <summary>番号を blob に焼いただけの 1 段</summary>
    private static SceneSnapshot Snapshot(int id) => new(
        BitConverter.GetBytes(id),
        new Dictionary<string, string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal));

    /// <summary>世界へ当てるよう求められた段の番号を順に控える履歴</summary>
    private static (EditHistoryViewModel History, List<int> Restored) New(int capacity = 50)
    {
        var history = new EditHistoryViewModel(capacity);
        var restored = new List<int>();
        history.Restoring += snapshot => restored.Add(BitConverter.ToInt32(snapshot.Blob));
        return (history, restored);
    }

    [Fact]
    public void Undo_and_redo_visit_the_committed_values_in_order()
    {
        (EditHistoryViewModel history, List<int> restored) = New();
        history.Reset(Snapshot(0));
        history.Commit(null, Snapshot(1));
        history.Commit(null, Snapshot(2));

        Assert.True(history.CanUndo);
        history.Undo();
        history.Undo();

        Assert.Equal([1, 0], restored);
        Assert.False(history.CanUndo);
        Assert.True(history.CanRedo);
        restored.Clear();
        history.Redo();
        history.Redo();
        Assert.Equal([1, 2], restored);
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void Closing_coalescing_separates_consecutive_edits_with_the_same_key()
    {
        (EditHistoryViewModel history, _) = New();
        history.Reset(Snapshot(0));

        history.Commit("node/Position", Snapshot(1));
        history.Commit("node/Position", Snapshot(2));
        history.Commit("node/Position", Snapshot(3));

        history.CloseCoalescing();
        history.Commit("node/Position", Snapshot(4));
        history.Undo();
        Assert.True(history.CanUndo);
        history.Undo();
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void Suspensions_nest()
    {
        (EditHistoryViewModel history, _) = New();
        history.Reset(Snapshot(0));

        using (history.Suspend())
        {
            using (history.Suspend())
            {
            }

            // 内側を抜けても外側はまだ生きている
            Assert.True(history.IsSuspended);
            history.Commit(null, Snapshot(1));
        }

        Assert.False(history.IsSuspended);
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void Restoring_runs_suspended()
    {
        (EditHistoryViewModel history, _) = New();
        history.Reset(Snapshot(0));
        history.Commit(null, Snapshot(1));

        // 世界を戻した結果として届く積み込みは段にならない
        history.Restoring += _ =>
        {
            Assert.True(history.IsSuspended);
            history.Commit(null, Snapshot(99));
        };
        history.Undo();

        Assert.False(history.IsSuspended);
        Assert.False(history.CanUndo);
        Assert.True(history.CanRedo);
    }

    [Fact]
    public void A_disabled_history_neither_records_nor_moves()
    {
        (EditHistoryViewModel history, List<int> restored) = New();
        history.Reset(Snapshot(0));
        history.Commit(null, Snapshot(1));

        var published = new List<EditHistory>();
        history.HistoryChanged += published.Add;
        history.Enabled = false;

        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);
        history.Commit(null, Snapshot(2));
        history.Undo();
        Assert.Empty(restored);
        history.PublishHistory();
        Assert.Empty(published);

        history.Enabled = true;
        Assert.True(history.CanUndo);
    }

    [Fact]
    public void Availability_changes_are_notified_to_bound_views()
    {
        (EditHistoryViewModel history, _) = New();
        var changed = new List<string?>();
        history.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        history.Reset(Snapshot(0));
        history.Commit(null, Snapshot(1));
        Assert.Contains(nameof(EditHistoryViewModel.CanUndo), changed);

        changed.Clear();
        history.Enabled = false;
        Assert.Equal([nameof(EditHistoryViewModel.CanUndo), nameof(EditHistoryViewModel.CanRedo)], changed);

        changed.Clear();
        history.Enabled = true;
        history.Undo();
        Assert.Contains(nameof(EditHistoryViewModel.CanRedo), changed);
    }

    [Fact]
    public void The_capacity_drops_the_oldest_step()
    {
        (EditHistoryViewModel history, List<int> restored) = New(capacity: 2);
        history.Reset(Snapshot(0));
        for (int i = 1; i <= 5; i++) history.Commit(null, Snapshot(i));

        int steps = 0;
        while (history.CanUndo)
        {
            history.Undo();
            steps++;
        }

        Assert.Equal(2, steps);
        Assert.Equal([4, 3], restored);
    }

    [Fact]
    public void Adopting_a_session_takes_over_its_cursor()
    {
        (EditHistoryViewModel history, List<int> restored) = New();
        var session = new EditHistory(
            [
                new EditHistoryStep(1, Snapshot(10)),
                new EditHistoryStep(2, Snapshot(11)),
                new EditHistoryStep(3, Snapshot(12)),
            ],
            Cursor: 2);

        history.Adopt(session);

        Assert.True(history.CanUndo);
        Assert.False(history.CanRedo);
        history.Undo();
        Assert.Equal([11], restored);
    }

    [Fact]
    public void Every_committed_step_is_published_for_handover()
    {
        (EditHistoryViewModel history, _) = New();
        EditHistory? latest = null;
        history.HistoryChanged += changed => latest = changed;

        history.Reset(Snapshot(0));
        history.Commit(null, Snapshot(1));
        history.Commit(null, Snapshot(2));

        Assert.NotNull(latest);
        Assert.Equal(3, latest.Steps.Count);
        Assert.Equal(2, latest.Cursor);
        Assert.Equal(latest.Steps.Select(s => s.Key).Distinct().Count(), latest.Steps.Count);
    }
}
