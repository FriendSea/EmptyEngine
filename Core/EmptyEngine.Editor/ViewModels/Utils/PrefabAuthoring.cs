using EmptyEngine.Editor.Authoring;

namespace EmptyEngine.Editor.ViewModels.Utils;

/// <summary>プレハブの切り出しと差し替えに伴うオーサリングツリーの編集</summary>
/// <remarks>選択状態と未保存状態は変更しない。</remarks>
internal static class PrefabAuthoring
{
    /// <summary>切り出す前の木と実体化し直した木の、同じ位置どうしの Id の対応</summary>
    /// <remarks>両方の木が同じ構造であることを前提とする。片方が短い場合は対応する位置がある範囲だけを返す。</remarks>
    public static Dictionary<string, string> MapIds(HierarchyNode before, HierarchyNode after)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        MapIdsInto(before, after, map);
        return map;
    }

    /// <summary>コンポーネントが持つオブジェクト参照の指し先 Id の差し替え</summary>
    /// <remarks>対応表に無い Id はそのまま（＝切り出しの外を指している参照は触らない）</remarks>
    public static void RewriteObjectReferences(
        IEnumerable<AuthoringObject> components, IReadOnlyDictionary<string, string> idMap)
    {
        foreach (AuthoringObject component in components)
            component.Schema.Root.Visit(component.Data, (value, type) =>
            {
                if (type.Kind == FieldKind.ObjectReference
                    && value is { IsNull: false, Text: { } id }
                    && idMap.TryGetValue(id, out string? mapped)) value.SetString(mapped);
            });
    }

    private static void MapIdsInto(HierarchyNode before, HierarchyNode after, IDictionary<string, string> into)
    {
        into[before.ObjectId] = after.ObjectId;

        int count = Math.Min(before.Children.Count, after.Children.Count);
        for (int i = 0; i < count; i++)
            MapIdsInto(before.Children[i], after.Children[i], into);
    }

}
