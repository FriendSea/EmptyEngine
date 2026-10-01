using EmptyEngine.Modules.Testing;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Authoring;
using EmptyEngine.Tests;
using EmptyEngine.World.Editor;
using Xunit;

namespace EmptyEngine.World.Tests;

/// <summary>値ツリーが空のコンポーネントのエディタ→ランタイム往復の検証</summary>
/// <remarks>空の map はコンポーネントとして復元でき、<c>nil</c> は復元されない。</remarks>
[Collection(TypeSerializerCollection.Name)]
public sealed class EmptyDataComponentTests
{
    private static byte[] SceneWith(FieldValue data)
    {
        var component = new AuthoringObject(CatalogStub.Schema(typeof(TestTransform)), data);
        var scene = new HierarchyNode(
            "root", "Root", [new HierarchyNode("obj", "Object", null, [component])], sceneId: "scene-1");

        return AuthoringBlobCodec.Encode([scene]);
    }

    /// <remarks>ワイヤ上の nil は「実体なし」＝ランタイムは型名も見ずに捨てる。エディタは nil を持たない</remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Empty_or_nil_component_data_survives_the_editor_and_runtime(bool nil)
    {
        using var world = new SceneWorld();
        world.DeserializeSceneNow(SceneWith(nil ? FieldValue.Nil() : new FieldValue()), isPlaying: false);
        RootInstanceData instance = Assert.Single(SceneBlob.Decode(world.SerializeScene()));
        ComponentData survivor = Assert.Single(Assert.Single(instance.Root.Children).Components);
        Assert.Equal(typeof(TestTransform).FullName, survivor.TypeName);
        Assert.IsType<TestTransform>(survivor.Component);
    }
}
