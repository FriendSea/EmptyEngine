using EmptyEngine.Modules.Testing;
using EmptyEngine.Tests;
using Xunit;

namespace EmptyEngine.World.Tests;

public sealed class DuplicateComponentTests
{
    [Fact]
    public void Same_type_components_are_preserved_through_runtime_round_trip()
    {
        var root = new RootInstanceData("inst",
            new ObjectData("obj", "O",
            [
                new ComponentData(typeof(TestHealth).FullName!, new TestHealth { Max = 10, Current = 10 }),
                new ComponentData(typeof(TestHealth).FullName!, new TestHealth { Max = 20, Current = 20 }),
            ],
            []));

        var world = new SceneWorld();
        world.DeserializeSceneNow(SceneBlob.Encode([root]), isPlaying: true);

        RootInstanceData decoded = Assert.Single(SceneBlob.Decode(world.SerializeScene()));

        Assert.Equal(2, decoded.Root.Components.Count);
        Assert.All(decoded.Root.Components, c => Assert.Equal(typeof(TestHealth).FullName, c.TypeName));
    }
}
