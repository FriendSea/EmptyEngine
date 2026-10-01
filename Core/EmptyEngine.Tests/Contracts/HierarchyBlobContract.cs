using EmptyEngine.Editor;
using EmptyEngine.Editor.Authoring;
using Xunit;

namespace EmptyEngine.Tests.Contracts;

/// <summary><see cref="IHierarchyBlobSerializer"/> 実装が満たすべき契約</summary>
public abstract class HierarchyBlobContract
{
    protected abstract IHierarchyBlobSerializer CreateSubject();

    /// <summary>対象が解釈できる有効なシーン blob</summary>
    protected abstract byte[] SampleBlob();

    protected abstract IReadOnlyList<HierarchyNode> ExpectedRoots();

    [Fact]
    public void Known_hierarchy_values_survive_decode_and_reserialize()
    {
        IHierarchyBlobSerializer subject = CreateSubject();
        IReadOnlyList<HierarchyNode> expected = ExpectedRoots();
        IReadOnlyList<HierarchyNode> actual = subject.Deserialize(SampleBlob());
        Assert.Equal(expected.Count, actual.Count);
        foreach ((HierarchyNode a, HierarchyNode b) in expected.Zip(actual))
            AssertNode(a, b);

        byte[] saved = subject.Serialize(actual);
        IReadOnlyList<HierarchyNode> restored = subject.Deserialize(saved);
        Assert.Equal(expected.Count, restored.Count);
        foreach ((HierarchyNode a, HierarchyNode b) in expected.Zip(restored))
            AssertNode(a, b);
        Assert.Equal(saved, subject.Serialize(restored));
    }

    /// <summary>ルート自身が持つコンポーネントの往復での保持</summary>
    [Fact]
    public void Root_level_components_survive_round_trip()
    {
        IHierarchyBlobSerializer subject = CreateSubject();

        HierarchyNode objectAsRoot = subject.Deserialize(SampleBlob())
            .SelectMany(scene => scene.Children)
            .First(node => node.Components.Count > 0);

        HierarchyNode back = subject.Deserialize(subject.Serialize(new[] { objectAsRoot })).Single();

        Assert.Equal(objectAsRoot.Name, back.Name);
        Assert.Equal(objectAsRoot.Components.Count, back.Components.Count);
        Assert.Equal(objectAsRoot.Children.Count, back.Children.Count);
    }
    private static void AssertNode(HierarchyNode expected, HierarchyNode actual)
    {
        Assert.Equal(expected.ObjectId, actual.ObjectId);
        Assert.Equal(expected.SceneId, actual.SceneId);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Active, actual.Active);
        Assert.Equal(expected.Components.Select(c => c.TypeName), actual.Components.Select(c => c.TypeName));
        foreach ((AuthoringObject a, AuthoringObject b) in expected.Components.Zip(actual.Components))
            AssertValue(a.Data, b.Data);
        Assert.Equal(expected.Children.Count, actual.Children.Count);
        foreach ((HierarchyNode a, HierarchyNode b) in expected.Children.Zip(actual.Children))
            AssertNode(a, b);
    }

    private static void AssertValue(FieldValue expected, FieldValue actual)
    {
        Assert.Equal(expected.IsNull, actual.IsNull);
        Assert.Equal(expected.Integer, actual.Integer);
        Assert.Equal(expected.Unsigned, actual.Unsigned);
        Assert.Equal(expected.Real, actual.Real);
        Assert.Equal(expected.Bool, actual.Bool);
        Assert.Equal(expected.Text, actual.Text);
        Assert.Equal(expected.Binary?.Length, actual.Binary?.Length);
        Assert.Equal(expected.Entries.Select(e => e.Key).Order(), actual.Entries.Select(e => e.Key).Order());
        foreach (FieldEntry entry in expected.Entries)
            AssertValue(entry.Value, actual.Get(entry.Key)!);
        Assert.Equal(expected.Items.Count, actual.Items.Count);
        foreach ((FieldValue a, FieldValue b) in expected.Items.Zip(actual.Items))
            AssertValue(a, b);
    }

}
