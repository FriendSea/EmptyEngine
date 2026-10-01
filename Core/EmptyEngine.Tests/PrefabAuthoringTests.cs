using EmptyEngine.Editor;
using EmptyEngine.Editor.Authoring;
using EmptyEngine.Editor.ViewModels.Utils;
using Xunit;

namespace EmptyEngine.Tests;

/// <summary>プレハブの切り出しに伴うツリー編集を検証する</summary>
/// <remarks>ID の対応付けと参照の書き換えを対象とする。</remarks>
public sealed class PrefabAuthoringTests
{
    private static AuthoringObject Reference(string targetId)
    {
        var data = new FieldValue();
        data.Add("Target", new FieldValue { Text = targetId });
        return new AuthoringObject(TestSchemas.Object("Sample.Pointer", ("Target", new FieldTypeInfo("", FieldKind.ObjectReference))), data);
    }

    private static string? TargetIdOf(AuthoringObject component) =>
        component.Data.Get("Target") is { IsNull: false } reference ? reference.Text : null;

    /// <summary>孫までの木（root → mid → leaf）</summary>
    private static HierarchyNode Tree(string suffix, params AuthoringObject[] leafComponents)
    {
        var leaf = new HierarchyNode("leaf" + suffix, "Leaf", null, leafComponents);
        var mid = new HierarchyNode("mid" + suffix, "Mid", [leaf]);
        return new HierarchyNode("root" + suffix, "Root", [mid]);
    }

    [Fact]
    public void Ids_are_paired_by_position_down_the_whole_tree()
    {
        Dictionary<string, string> map = PrefabAuthoring.MapIds(Tree("-old"), Tree("-new"));

        Assert.Equal("root-new", map["root-old"]);
        Assert.Equal("mid-new", map["mid-old"]);
        Assert.Equal("leaf-new", map["leaf-old"]);
    }

    /// <summary>実体化が同じ形を返さなくても、短い方まででは対応が取れていること</summary>
    [Fact]
    public void Pairing_stops_where_the_two_trees_stop_matching()
    {
        var before = new HierarchyNode("root-old", "Root", [new HierarchyNode("mid-old", "Mid")]);
        var after = new HierarchyNode("root-new", "Root");

        Dictionary<string, string> map = PrefabAuthoring.MapIds(before, after);

        Assert.Equal("root-new", map["root-old"]);
        Assert.False(map.ContainsKey("mid-old"));
    }

    [Fact]
    public void References_into_the_extracted_subtree_are_repointed()
    {
        AuthoringObject pointer = Reference("leaf-old");
        AuthoringObject outside = Reference("somewhere-else");

        PrefabAuthoring.RewriteObjectReferences([pointer, outside], new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["leaf-old"] = "leaf-new",
        });

        Assert.Equal("leaf-new", TargetIdOf(pointer));
        Assert.Equal("somewhere-else", TargetIdOf(outside));
    }
}
