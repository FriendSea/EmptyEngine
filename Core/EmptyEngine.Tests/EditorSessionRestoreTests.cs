using System.Globalization;
using System.Text;
using EmptyEngine.Editor.State;
using Xunit;

namespace EmptyEngine.Tests;

/// <summary>編集セッション（undo 履歴）をディスクへ置いて建て直しを跨がせる契約の回帰</summary>
public sealed class EditorSessionRestoreTests
{
    private sealed class Workspace : IDisposable
    {
        public Workspace() => Directory.CreateDirectory(Root);

        public string Root { get; } =
            Path.Combine(Path.GetTempPath(), "ee-session-" + Guid.NewGuid().ToString("n"));

        public string StepsDirectory => Path.Combine(Root, "steps");

        public string IndexPath => Path.Combine(Root, "index");

        public string StepPath(ulong key) =>
            Path.Combine(StepsDirectory, key.ToString("x16", CultureInfo.InvariantCulture) + ".step");

        public EditHistoryStore Open() => new(Root);

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }

    private static SceneSnapshot Step(string marker) => new(
        Blob: Encoding.UTF8.GetBytes(marker),
        LoadedSceneKeys: new Dictionary<string, string>(StringComparer.Ordinal) { ["scene-1"] = "guid-aaa" },
        DirtySceneIds: new HashSet<string>(StringComparer.Ordinal) { "scene-1" });

    private static EditHistory Session(params string[] markers) => new(
        markers.Select((m, i) => new EditHistoryStep((ulong)(i + 1), Step(m))).ToList(),
        Cursor: markers.Length - 1);

    private static string[] Markers(EditHistory session) =>
        session.Steps.Select(s => Encoding.UTF8.GetString(s.Snapshot.Blob)).ToArray();

    [Fact]
    public async Task Restart_restores_session_values_without_rewriting_existing_steps()
    {
        using var workspace = new Workspace();
        EditHistoryStore store = workspace.Open();
        Assert.Null(store.TryLoad());
        var original = new SceneSnapshot(
            Blob: new byte[] { 0x00, 0xFF, 0x10, 0x00, 0x7F },
            LoadedSceneKeys: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["scene-instance-1"] = "guid-aaa",
                ["scene-instance-2"] = "guid-bbb",
            },
            DirtySceneIds: new HashSet<string>(StringComparer.Ordinal) { "scene-instance-2" });
        var first = new EditHistoryStep(1, original);
        await store.SaveAsync(new EditHistory([first], Cursor: 0));

        string existing = workspace.StepPath(1);
        File.SetLastWriteTimeUtc(existing, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        DateTime timestamp = File.GetLastWriteTimeUtc(existing);
        await store.SaveAsync(new EditHistory(
            [first, new EditHistoryStep(2, Step("unsaved"))], Cursor: 1));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(existing));

        EditHistory restored = Assert.IsType<EditHistory>(workspace.Open().TryLoad());
        Assert.Equal(1, restored.Cursor);
        Assert.Equal(new ulong[] { 1, 2 }, restored.Steps.Select(s => s.Key));
        SceneSnapshot decoded = restored.Steps[0].Snapshot;
        Assert.Equal(original.Blob, decoded.Blob);
        Assert.Equal(original.LoadedSceneKeys, decoded.LoadedSceneKeys);
        Assert.Equal(original.DirtySceneIds, decoded.DirtySceneIds);
        Assert.Equal("unsaved", Encoding.UTF8.GetString(restored.Steps[1].Snapshot.Blob));
    }

    [Fact]
    public async Task Steps_that_fell_out_of_the_history_are_deleted()
    {
        using var workspace = new Workspace();
        EditHistoryStore store = workspace.Open();

        await store.SaveAsync(Session("baseline", "edit-1", "edit-2"));

        var branched = new EditHistory(
            new[]
            {
                new EditHistoryStep(1, Step("baseline")),
                new EditHistoryStep(4, Step("edit-1-again")),
            },
            Cursor: 1);
        await store.SaveAsync(branched);

        EditHistory? restored = workspace.Open().TryLoad();
        Assert.NotNull(restored);
        Assert.Equal(new[] { "baseline", "edit-1-again" }, Markers(restored!));
        Assert.Equal(2, Directory.GetFiles(workspace.StepsDirectory).Length);
    }

    [Fact]
    public async Task A_session_whose_step_is_missing_is_dropped_whole()
    {
        using var workspace = new Workspace();
        await workspace.Open().SaveAsync(Session("baseline", "edit-1"));

        File.Delete(workspace.StepPath(2));

        Assert.Null(workspace.Open().TryLoad());
    }

    [Fact]
    public async Task Unknown_index_version_is_rejected_instead_of_misread()
    {
        using var workspace = new Workspace();
        await workspace.Open().SaveAsync(Session("baseline", "edit-1"));

        byte[] index = await File.ReadAllBytesAsync(workspace.IndexPath);
        index[0] = 99;
        await File.WriteAllBytesAsync(workspace.IndexPath, index);

        Assert.Null(workspace.Open().TryLoad());
    }

    [Fact]
    public async Task A_truncated_index_is_rejected_instead_of_misread()
    {
        using var workspace = new Workspace();
        await workspace.Open().SaveAsync(Session("baseline", "edit-1"));

        await File.WriteAllBytesAsync(workspace.IndexPath, new byte[] { 1, 0xFF, 0xFF, 0xFF, 0x7F });

        Assert.Null(workspace.Open().TryLoad());
    }

    [Fact]
    public async Task The_last_save_of_a_burst_is_the_one_that_survives()
    {
        using var workspace = new Workspace();
        EditHistoryStore store = workspace.Open();

        Task[] burst =
        [
            store.SaveAsync(Session("baseline", "edit-1")),
            store.SaveAsync(Session("baseline", "edit-1", "edit-2")),
            store.SaveAsync(Session("baseline", "edit-1", "edit-2", "edit-3")),
        ];
        await Task.WhenAll(burst);

        EditHistory? restored = workspace.Open().TryLoad();
        Assert.NotNull(restored);
        Assert.Equal(new[] { "baseline", "edit-1", "edit-2", "edit-3" }, Markers(restored!));
    }

    [Fact]
    public void Empty_world_round_trips_as_an_empty_world()
    {
        var original = new SceneSnapshot(
            Blob: Array.Empty<byte>(),
            LoadedSceneKeys: new Dictionary<string, string>(StringComparer.Ordinal),
            DirtySceneIds: new HashSet<string>(StringComparer.Ordinal));

        SceneSnapshot? decoded = SceneSnapshot.TryDecode(original.Encode());

        Assert.NotNull(decoded);
        Assert.Empty(decoded!.Blob);
        Assert.Empty(decoded.LoadedSceneKeys);
        Assert.Empty(decoded.DirtySceneIds);
    }

    [Fact]
    public void Unknown_payload_is_rejected_instead_of_misread()
    {
        Assert.Null(SceneSnapshot.TryDecode(new byte[] { 99, 1, 2, 3 }));
        Assert.Null(SceneSnapshot.TryDecode(Array.Empty<byte>()));
        Assert.Null(SceneSnapshot.TryDecode(new byte[] { 1, 0xFF, 0xFF, 0xFF, 0x7F }));
    }
}
