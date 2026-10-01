using EmptyEngine.Core;
using EmptyEngine.Editor.Distribution;
using EmptyEngine.Editor.ViewModels.Build;
using Xunit;

namespace EmptyEngine.Tests;

/// <summary>Build Settings リストの検証</summary>
/// <remarks>シーンの順序と、変更のたびに <c>BuildScenes.json</c> へ保存されることを検証する。</remarks>
public sealed class BuildSceneListViewModelTests : IDisposable
{
    /// <summary>このテスト専用のプロジェクト相当のフォルダ</summary>
    private readonly string _projectRoot =
        Path.Combine(Path.GetTempPath(), "ee-buildscenes-" + Guid.NewGuid().ToString("N"));

    private readonly string _assetsRoot;

    public BuildSceneListViewModelTests()
    {
        _assetsRoot = Path.Combine(_projectRoot, "Assets");
        Directory.CreateDirectory(_assetsRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_projectRoot, recursive: true); } catch { }
    }

    /// <summary>すべてシーン扱いで、名前はキーの頭を大文字にしただけの出所</summary>
    private sealed class FakeSource(string? root) : IBuildSceneSource
    {
        /// <summary>シーンではないと答えるキー</summary>
        public HashSet<string> NotScenes { get; } = new(StringComparer.Ordinal);

        public string? AssetsRootPath => root;

        public bool IsSceneAsset(string key) => !NotScenes.Contains(key);

        public string? ResolveDisplayName(string key) => key + "!";
    }

    private BuildSceneListViewModel NewList(out FakeSource source)
    {
        source = new FakeSource(_assetsRoot);
        return new BuildSceneListViewModel(source);
    }

    private IReadOnlyList<string> Persisted() => BuildSceneList.Read(_assetsRoot);

    [Fact]
    public void Adding_scenes_persists_order_and_updates_startup_and_move_controls()
    {
        BuildSceneListViewModel list = NewList(out _);
        list.Add(new AssetKey("a.scene"));
        list.Selected = list.Items[0];
        Assert.False(list.CanMove);
        Assert.True(list.Items[0].IsStartup);

        list.Add(new AssetKey("b.scene"));
        Assert.True(list.CanMove);
        Assert.False(list.Items[1].IsStartup);
        Assert.Equal(["a.scene", "b.scene"], list.Keys);
        Assert.Equal(["a.scene", "b.scene"], Persisted());
    }

    [Fact]
    public void Moving_reorders_and_moves_the_startup_marker()
    {
        BuildSceneListViewModel list = NewList(out _);
        list.Add(new AssetKey("a.scene"));
        list.Add(new AssetKey("b.scene"));

        list.Selected = list.Items[1];
        list.Move(-1);

        Assert.Equal(["b.scene", "a.scene"], list.Keys);
        Assert.Equal(["b.scene", "a.scene"], Persisted());
        Assert.True(list.Items[0].IsStartup);
        Assert.False(list.Items[1].IsStartup);
    }

    /// <summary>端を越える移動は何もしない（書き戻しも起きない）</summary>
    [Fact]
    public void Moving_past_the_end_does_nothing()
    {
        BuildSceneListViewModel list = NewList(out _);
        list.Add(new AssetKey("a.scene"));
        list.Selected = list.Items[0];

        list.Move(-1);
        list.Move(1);

        Assert.Equal(["a.scene"], list.Keys);
    }

    [Fact]
    public void Removing_drops_the_selection_with_the_item()
    {
        BuildSceneListViewModel list = NewList(out _);
        list.Add(new AssetKey("a.scene"));
        list.Add(new AssetKey("b.scene"));
        list.Selected = list.Items[0];

        list.RemoveSelected();

        Assert.Equal(["b.scene"], list.Keys);
        Assert.Equal(["b.scene"], Persisted());
        Assert.Null(list.Selected);
        Assert.False(list.CanRemove);
    }

    [Fact]
    public void The_same_scene_is_not_added_twice()
    {
        BuildSceneListViewModel list = NewList(out _);

        list.Add(new AssetKey("a.scene"));
        list.Add(new AssetKey("a.scene"));

        Assert.Equal(["a.scene"], list.Keys);
    }

    [Fact]
    public void A_key_that_is_not_a_scene_is_refused()
    {
        BuildSceneListViewModel list = NewList(out FakeSource source);
        source.NotScenes.Add("texture.png");

        list.Add(new AssetKey("texture.png"));

        Assert.Empty(list.Keys);
    }

    [Fact]
    public void Reloading_takes_the_order_from_the_file()
    {
        BuildSceneList.Write(_assetsRoot, ["b.scene", "a.scene"]);
        BuildSceneListViewModel list = NewList(out _);

        list.Reload();

        Assert.Equal(["b.scene", "a.scene"], list.Keys);
        Assert.True(list.Items[0].IsStartup);
        Assert.Equal("b.scene!", list.Items[0].DisplayName);
    }

    /// <summary>取り込みがまだ（Assets の根が無い）なら、読みも書きもしない</summary>
    [Fact]
    public void Without_an_assets_root_nothing_is_persisted()
    {
        var list = new BuildSceneListViewModel(new FakeSource(null));

        list.Reload();
        list.Add(new AssetKey("a.scene"));

        Assert.Equal(["a.scene"], list.Keys);
        Assert.Empty(Persisted());
    }

}
