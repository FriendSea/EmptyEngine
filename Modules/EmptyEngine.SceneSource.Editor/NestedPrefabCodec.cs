using System.Text.Json;
using System.Text.Json.Nodes;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Authoring;

namespace EmptyEngine.SceneSource.Editor;

/// <summary>ネストプレハブの葉の純データ処理</summary>
internal static class NestedPrefabCodec
{
    /// <summary>Id 合成の区切り</summary>
    public const char IdSeparator = ':';

    /// <summary>ネストプレハブの葉</summary>
    public sealed record PrefabLeaf(
        string Id,
        string SourceGuid,
        IReadOnlyList<PrefabVariantCodec.FieldOverride> Overrides,
        IReadOnlyList<PrefabVariantCodec.ComponentAddition> AddedComponents,
        IReadOnlyList<PrefabVariantCodec.ChildAddition> AddedChildren);

    /// <summary>この JSON オブジェクトがネストプレハブの葉か</summary>
    public static bool IsLeaf(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty("Prefab", out _);

    public static PrefabLeaf ReadLeaf(JsonElement element, ISchemaSource schemas)
    {
        if (JsonNode.Parse(element.GetRawText()) is not JsonObject o)
            throw new FormatException("Nested prefab leaf must be a JSON object.");

        return new PrefabLeaf(
            Id: o["Id"]?.GetValue<string>() ?? string.Empty,
            SourceGuid: o["Prefab"]?.GetValue<string>() ?? string.Empty,
            Overrides: PrefabVariantCodec.ReadOverrides(o["Overrides"]),
            AddedComponents: PrefabVariantCodec.ReadAddedComponents(o["AddedComponents"]),
            AddedChildren: PrefabVariantCodec.ReadAddedChildren(o["AddedChildren"], schemas));
    }

    public static JsonObject WriteLeaf(PrefabLeaf leaf) => new()
    {
        ["Id"] = leaf.Id,
        ["Prefab"] = leaf.SourceGuid,
        ["Overrides"] = PrefabVariantCodec.WriteOverrides(leaf.Overrides),
        ["AddedComponents"] = PrefabVariantCodec.WriteAddedComponents(leaf.AddedComponents),
        ["AddedChildren"] = PrefabVariantCodec.WriteAddedChildren(leaf.AddedChildren),
    };

    /// <summary>ツリー全体のオブジェクト Id と参照へのプレフィックス付け</summary>
    /// <remarks>渡された木を直接変更する。元の状態を保持する必要がある場合は複製を渡すこと。</remarks>
    public static HierarchyNode Remap(HierarchyNode root, string leafId)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        CollectIds(root, ids);
        return RemapNode(root, leafId, ids);
    }

    /// <summary>合成 Id の先頭セグメント（葉Id）</summary>
    public static string? LeafIdOf(string objectId)
    {
        int i = objectId.IndexOf(IdSeparator);
        return i > 0 ? objectId[..i] : null;
    }

    /// <summary><paramref name="objectId"/> のノードがネスト配置インスタンスのルートか</summary>
    public static bool IsInstanceRoot(string objectId, string? parentObjectId) =>
        PrefixDepth(objectId) > PrefixDepth(parentObjectId);

    private static int PrefixDepth(string? objectId) =>
        objectId is null ? 0 : objectId.AsSpan().Count(IdSeparator);

    private static void CollectIds(HierarchyNode node, HashSet<string> ids)
    {
        ids.Add(node.ObjectId);
        foreach (HierarchyNode child in node.Children)
            CollectIds(child, ids);
    }

    private static HierarchyNode RemapNode(HierarchyNode node, string leafId, HashSet<string> ids)
    {
        foreach (AuthoringObject component in node.Components)
            component.Schema.Root.Visit(component.Data, (value, type) =>
            {
                if (type.Kind == FieldKind.ObjectReference
                    && value is { IsNull: false, Text: { } id }
                    && ids.Contains(id)) value.SetString(leafId + IdSeparator + id);
            });

        var children = new List<HierarchyNode>(node.Children.Count);
        foreach (HierarchyNode child in node.Children)
            children.Add(RemapNode(child, leafId, ids));

        return new HierarchyNode(
            leafId + IdSeparator + node.ObjectId, node.Name, children, node.Components, active: node.Active);
    }

}
