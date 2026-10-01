using EmptyEngine.ObjectModel;
using EmptyEngine.Tests;
using Xunit;

namespace EmptyEngine.World.Tests;

/// <summary><see cref="GameObject.Reload"/> / <see cref="SceneWorld.Roots"/> の検証</summary>
public sealed class SceneReloadTests
{
    private static ObjectData Obj(string id, string name, int value)
        => new(id, name, [new ComponentData(typeof(SceneReloadProbe).FullName!, new SceneReloadProbe { Value = value })], []);

    private static byte[] TwoScenes(int aValue, int bValue) => SceneBlob.Encode(
    [
        new RootInstanceData("inst-a", Obj("a-obj", "A-Object", aValue)),
        new RootInstanceData("inst-b", Obj("b-obj", "B-Object", bValue)),
    ]);

    [Fact]
    public void Reload_resets_only_the_selected_scene_and_preserves_instance_order()
    {
        var destroyed = new List<int>();
        SceneReloadProbe.OnDestroyed = destroyed.Add;
        try
        {
            using var world = new SceneWorld();
            world.DeserializeSceneNow(TwoScenes(1, 2), isPlaying: true);
            Assert.Equal(new[] { 1, 2 }, world.Roots.Select(r => r.GetAttachable<SceneReloadProbe>()!.Value));
            IObject first = world.Roots[0];
            first.GetAttachable<SceneReloadProbe>()!.Value = 111;
            world.Roots[1].GetAttachable<SceneReloadProbe>()!.Value = 222;

            world.Roots[1].Reload();
            Assert.Same(first, world.Roots[0]);
            Assert.Equal(new[] { 222 }, destroyed);
            Assert.Equal(111, first.GetAttachable<SceneReloadProbe>()!.Value);
            Assert.Equal(2, world.Roots[1].GetAttachable<SceneReloadProbe>()!.Value);
            Assert.Equal(new[] { "inst-a", "inst-b" },
                SceneBlob.Decode(world.SerializeScene()).Select(s => s.InstanceId));
        }
        finally
        {
            SceneReloadProbe.OnDestroyed = null;
        }
    }

}

/// <summary>このテスト専用のライフサイクルコンポーネント</summary>
public sealed class SceneReloadProbe : ILifecycleAttachable
{
    /// <summary>破棄時のコールバック</summary>
    public static Action<int>? OnDestroyed;

    public int Value { get; set; }

    public void OnCreated(IObject owner) { }

    public void OnDeserialized(IObject owner) { }

    public void OnDestroy(IObject owner) => OnDestroyed?.Invoke(Value);
}
