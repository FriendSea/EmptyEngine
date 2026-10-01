using EmptyEngine.Editor.State;
using Xunit;

namespace EmptyEngine.Tests;

/// <summary>ペインの幅と畳み状態の控えの検証</summary>
public sealed class EditorPaneStateTests
{
    private static string StatePath() =>
        Path.Combine(Path.GetTempPath(), "ee-pane-state-" + Guid.NewGuid().ToString("N"), "editor-state.json");

    [Fact]
    public void KeepsWidthsAndCollapseAcrossSessions()
    {
        string path = StatePath();

        var store = new EditorStateStore(path);
        store.SetPaneWidths(new Dictionary<string, double> { ["hierarchy"] = 320 });
        store.SetPaneCollapsed("build", false);

        var reopened = new EditorStateStore(path);

        Assert.Equal(320, reopened.PaneWidths["hierarchy"]);
        Assert.False(reopened.PaneCollapsed("build"));
    }

    [Fact]
    public void LeavesUntouchedPanesToTheScreenDefault()
    {
        var store = new EditorStateStore(StatePath());
        store.SetPaneCollapsed("build", true);

        Assert.Null(store.PaneCollapsed("inspector"));
        Assert.False(store.PaneWidths.ContainsKey("inspector"));
    }

    [Fact]
    public void ReplacesOnlyTheWidthsItIsGiven()
    {
        var store = new EditorStateStore(StatePath());
        store.SetPaneWidths(new Dictionary<string, double> { ["hierarchy"] = 320, ["build"] = 200 });

        store.SetPaneWidths(new Dictionary<string, double> { ["hierarchy"] = 240 });

        Assert.Equal(240, store.PaneWidths["hierarchy"]);
        Assert.Equal(200, store.PaneWidths["build"]);
    }
}
