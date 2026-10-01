using EmptyEngine.Modules.Testing;
using EmptyEngine.Tests;
using Xunit;

namespace EmptyEngine.World.Tests;

/// <summary>消えた型名を含む blob の復号が止まらないことの検証</summary>
[Collection(TypeSerializerCollection.Name)]
public sealed class UnknownComponentTypeTests
{
    private const string GoneTypeName = "Some.Deleted.Component";

    private static byte[] SceneWithGoneType()
        => SceneBlob.Encode([new RootInstanceData("inst",
            new ObjectData("obj", "O",
                [
                    new ComponentData(GoneTypeName, new TestTransform { X = 9f }),
                    new ComponentData(typeof(TestTransform).FullName!, new TestTransform { X = 1f }),
                ],
                []))]);

    [Fact]
    public void Unknown_type_is_skipped_and_reported_while_the_rest_is_restored()
    {
        var messages = new List<string>();

        RootInstanceData decoded = Assert.Single(SceneBlob.Decode(SceneWithGoneType(), messages.Add));

        Assert.Equal(2, decoded.Root.Components.Count);
        Assert.Equal(GoneTypeName, decoded.Root.Components[0].TypeName);
        Assert.Null(decoded.Root.Components[0].Component);
        Assert.Equal(1f, Assert.IsType<TestTransform>(decoded.Root.Components[1].Component).X);

        Assert.Contains(GoneTypeName, Assert.Single(messages));
    }

    [Fact]
    public void World_applies_the_blob_without_the_unknown_component()
    {
        var world = new SceneWorld();

        world.DeserializeSceneNow(SceneWithGoneType(), isPlaying: true);

        RootInstanceData instance = Assert.Single(SceneBlob.Decode(world.SerializeScene()));
        ComponentData survivor = Assert.Single(instance.Root.Components);
        Assert.Equal(typeof(TestTransform).FullName, survivor.TypeName);
    }
}
