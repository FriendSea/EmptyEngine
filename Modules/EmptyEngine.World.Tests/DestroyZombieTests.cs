using EmptyEngine.ObjectModel;
using EmptyEngine.Tests;
using Xunit;

namespace EmptyEngine.World.Tests;

/// <summary>破棄したコンポーネントが更新レジストリに残る経路の検証</summary>
public sealed class DestroyZombieTests
{
    private static ObjectData Probe(string id, string name)
        => new(id, name, [new ComponentData(typeof(ZombieProbe).FullName!, new ZombieProbe())], []);

    /// <summary>ルート直下に PauseUI 相当の子を持つ GameCommon 風ツリー</summary>
    private static ObjectData GameCommonLike()
        => new("gc", "GameCommon",
            [new ComponentData(typeof(ZombieProbe).FullName!, new ZombieProbe())],
            [Probe("pause-ui", "PauseUI")]);

    private static SceneWorld BuildWorld()
    {
        var world = new SceneWorld();
        world.DeserializeSceneNow(SceneBlob.Encode(
        [
            new RootInstanceData("inst-common", Probe("common", "Common")),
            new RootInstanceData("inst-proto", GameCommonLike()),
        ]), isPlaying: true);
        return world;
    }

    [Fact]
    public void SetActive_true_on_destroyed_subtree_is_ignored()
    {
        ZombieProbe.Registered.Clear();
        var logs = new List<string>();
        var world = new SceneWorld(null, logs.Add);
        world.DeserializeSceneNow(SceneBlob.Encode(
        [
            new RootInstanceData("inst-common", Probe("common", "Common")),
            new RootInstanceData("inst-proto", GameCommonLike()),
        ]), isPlaying: true);
        int baseline = ZombieProbe.Registered.Count;

        IObject spawned = world.Instantiate(world.Roots[1]);
        Assert.Equal(baseline + 2, ZombieProbe.Registered.Count);
        IObject pauseUi = spawned.GetChildren().Single();
        pauseUi.SetActive(false);
        Assert.Equal(baseline + 1, ZombieProbe.Registered.Count);
        pauseUi.SetActive(true);
        Assert.Equal(baseline + 2, ZombieProbe.Registered.Count);
        pauseUi.SetActive(false);
        Assert.Equal(baseline + 1, ZombieProbe.Registered.Count);

        spawned.Destroy();
        world.FlushPendingDestructions();
        Assert.Equal(baseline, ZombieProbe.Registered.Count);

        pauseUi.SetActive(true);
        Assert.Equal(baseline, ZombieProbe.Registered.Count);
        Assert.Contains(logs, m => m.Contains("SetActive(True) ignored"));
    }

    [Fact]
    public void Exception_during_destroy_still_unregisters_later_components()
    {
        ZombieProbe.Registered.Clear();
        var logs = new List<string>();
        var world = new SceneWorld(null, logs.Add);
        world.DeserializeSceneNow(SceneBlob.Encode(
        [
            new RootInstanceData("inst-x", new ObjectData("x", "X",
                [new ComponentData(typeof(ThrowingOnDestroy).FullName!, new ThrowingOnDestroy())],
                [Probe("x-child", "Child")])),
        ]), isPlaying: true);
        Assert.Single(ZombieProbe.Registered);

        IObject root = world.Roots.Single();
        root.Destroy();
        world.FlushPendingDestructions();

        Assert.Empty(world.Roots);
        Assert.Empty(ZombieProbe.Registered);

        Assert.Contains(logs, m => m.Contains("OnDestroy threw") && m.Contains(nameof(ThrowingOnDestroy)));
    }

    [Fact]
    public void Instantiate_does_not_create_components_deactivated_during_creation()
    {
        ZombieProbe.Registered.Clear();
        var world = new SceneWorld();
        world.DeserializeSceneNow(SceneBlob.Encode(
        [
            new RootInstanceData("inst-proto", new ObjectData("gc", "GameCommon",
                [new ComponentData(typeof(DeactivatorProbe).FullName!, new DeactivatorProbe { TargetId = "clear-ui" })],
                [Probe("clear-ui", "ClearUI")])),
        ]), isPlaying: true);
        int baseline = ZombieProbe.Registered.Count;

        IObject spawned = world.Instantiate(world.Roots[0]);
        Assert.Equal(baseline, ZombieProbe.Registered.Count);

        spawned.Reload();
        Assert.Equal(baseline, ZombieProbe.Registered.Count);

        IObject reloaded = world.Roots[^1];

        IObject clearUi = reloaded.GetChildren().Single();
        clearUi.SetActive(true);
        Assert.Equal(baseline + 1, ZombieProbe.Registered.Count);

        reloaded.Destroy();
        world.FlushPendingDestructions();
        Assert.Equal(baseline, ZombieProbe.Registered.Count);
    }

    /// <summary>プレイ中の編集で、同じ Id の兄弟を並び順で再利用する。</summary>
    /// <remarks>再利用したコンポーネントでは <c>OnCreated</c> を再実行しない。</remarks>
    [Fact]
    public void Editor_sync_during_play_reuses_same_id_siblings_in_order()
    {
        ZombieProbe.Registered.Clear();
        var world = new SceneWorld();
        world.DeserializeSceneNow(SceneBlob.Encode(
        [
            new RootInstanceData("inst-proto", new ObjectData("gc", "GameCommon", [], [Probe("bullet", "Bullet")])),
        ]), isPlaying: true);

        IObject parent = world.Roots[0];
        world.Instantiate(parent.GetChildren()[0], parent);
        world.Instantiate(parent.GetChildren()[0], parent);
        IObject[] before = parent.GetChildren().ToArray();

        world.DeserializeSceneNow(SceneBlob.Encode(
        [
            new RootInstanceData("inst-proto", new ObjectData("gc", "GameCommon", [],
                [Probe("bullet", "Bullet"), Probe("bullet", "Bullet"), Probe("bullet", "Bullet")])),
        ]), isPlaying: true);

        Assert.Equal(before, world.Roots[0].GetChildren());
        Assert.All(before, o => Assert.False(o.Lifetime().IsCancellationRequested));
        Assert.Equal(3, ZombieProbe.Registered.Count);
    }

    [Fact]
    public void Editor_sync_during_play_destroys_runtime_roots_cleanly()
    {
        ZombieProbe.Registered.Clear();
        var world = BuildWorld();
        int baseline = ZombieProbe.Registered.Count;

        world.Instantiate(world.Roots[1]);
        Assert.Equal(baseline + 2, ZombieProbe.Registered.Count);

        world.DeserializeSceneNow(SceneBlob.Encode(
        [
            new RootInstanceData("inst-common", Probe("common", "Common")),
            new RootInstanceData("inst-proto", GameCommonLike()),
        ]), isPlaying: true);

        Assert.Equal(baseline, ZombieProbe.Registered.Count);
    }
}

/// <summary>登録の残留を観測するプローブ</summary>
public sealed class ZombieProbe : ILifecycleAttachable
{
    public static readonly List<ZombieProbe> Registered = new();

    public void OnCreated(IObject owner)
    {
        if (!Registered.Contains(this))
            Registered.Add(this);
    }

    public void OnDeserialized(IObject owner) { }

    public void OnDestroy(IObject owner) => Registered.Remove(this);
}

/// <summary>OnCreated で同一インスタンス内の対象ノードを隠すコンポーネント</summary>
public sealed class DeactivatorProbe : ILifecycleAttachable
{
    public string TargetId { get; set; } = "";

    public void OnCreated(IObject owner)
    {
        if (owner.FindObject(TargetId) is { } target)
            target.SetActive(false);
    }

    public void OnDeserialized(IObject owner) { }

    public void OnDestroy(IObject owner) { }
}

/// <summary>破棄時に例外を投げるコンポーネント（破棄ループ中断の再現用）</summary>
public sealed class ThrowingOnDestroy : ILifecycleAttachable
{
    public void OnCreated(IObject owner) { }

    public void OnDeserialized(IObject owner) { }

    public void OnDestroy(IObject owner) => throw new InvalidOperationException("boom");
}
