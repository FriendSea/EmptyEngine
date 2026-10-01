namespace EmptyEngine.Editor;

/// <summary>基準ツリーとのオブジェクト構成の照合と、欠落コンポーネントの補完</summary>
/// <remarks>オブジェクトの増減は不整合として報告する。欠けたコンポーネントは基準の値を複製して補う。</remarks>
internal static class HierarchyReconciliation
{
    private static readonly Func<HierarchyNode, IReadOnlyList<HierarchyNode>> Children = node => node.Children;

    /// <summary>オブジェクトの食い違いの説明</summary>
    /// <returns>増減があればその説明、無ければ <c>null</c></returns>
    public static string? DescribeObjectMismatch(
        IReadOnlyList<HierarchyNode> authoritative, IReadOnlyList<HierarchyNode> candidate)
    {
        Dictionary<string, List<string>> expected = IndexObjectsByScene(authoritative);
        Dictionary<string, List<string>> actual = IndexObjectsByScene(candidate);

        var differences = new List<string>();

        foreach (HierarchyNode root in authoritative)
        {
            if (!actual.ContainsKey(root.SceneId))
            {
                differences.Add($"scene '{root.Name}' is missing");
                continue;
            }

            (List<string> dropped, List<string> added) =
                DiffObjectIds(expected[root.SceneId], actual[root.SceneId]);
            if (dropped.Count > 0) differences.Add($"scene '{root.Name}' dropped {Describe(dropped)}");
            if (added.Count > 0) differences.Add($"scene '{root.Name}' added {Describe(added)}");
        }

        foreach (HierarchyNode root in candidate)
        {
            if (!expected.ContainsKey(root.SceneId))
                differences.Add($"scene '{root.Name}' is unexpected");
        }

        return differences.Count == 0 ? null : string.Join("; ", differences);

        static string Describe(List<string> ids) =>
            ids.Count <= 5
                ? $"{ids.Count} object(s): {string.Join(", ", ids)}"
                : $"{ids.Count} object(s): {string.Join(", ", ids.Take(5))}, ...";
    }

    /// <summary>同じシーン・オブジェクトで欠けたコンポーネントを、基準ツリーの値から補完する</summary>
    /// <param name="candidate">補完先のツリー。このツリーのコンポーネント一覧を更新する。</param>
    /// <param name="restoredComponents">補完したコンポーネントを追加する集合。</param>
    public static void RestoreMissingComponents(
        IReadOnlyList<HierarchyNode> authoritative,
        IReadOnlyList<HierarchyNode> candidate,
        ISet<AuthoringObject> restoredComponents)
    {
        if (authoritative.Count == 0) return;

        var authoritativeBySceneId = new Dictionary<string, HierarchyNode>(StringComparer.Ordinal);
        foreach (HierarchyNode root in authoritative)
            authoritativeBySceneId.TryAdd(root.SceneId, root);

        foreach (HierarchyNode root in candidate)
        {
            if (!authoritativeBySceneId.TryGetValue(root.SceneId, out HierarchyNode? referenceRoot)) continue;

            var authoritativeById = new Dictionary<string, HierarchyNode>(StringComparer.Ordinal);
            foreach (HierarchyNode found in Tree.Flatten(referenceRoot, Children))
                authoritativeById.TryAdd(found.ObjectId, found);

            RestoreInto(root, authoritativeById, restoredComponents);
        }
    }

    private static Dictionary<string, List<string>> IndexObjectsByScene(IEnumerable<HierarchyNode> roots)
    {
        var index = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (HierarchyNode root in roots)
        {
            if (!index.TryGetValue(root.SceneId, out List<string>? ids))
                index[root.SceneId] = ids = new List<string>();
            ids.AddRange(Tree.Flatten(root, Children).Select(node => node.ObjectId));
        }

        return index;
    }

    /// <summary>並び順は問わない、Id の個数どうしの差</summary>
    private static (List<string> Dropped, List<string> Added) DiffObjectIds(
        List<string> authoritative, List<string> candidate)
    {
        var remaining = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string id in authoritative)
            remaining[id] = remaining.GetValueOrDefault(id) + 1;

        var added = new List<string>();
        foreach (string id in candidate)
        {
            int count = remaining.GetValueOrDefault(id);
            if (count == 0) added.Add(id);
            else remaining[id] = count - 1;
        }

        var dropped = new List<string>();
        foreach ((string id, int count) in remaining)
            for (int i = 0; i < count; i++) dropped.Add(id);

        return (dropped, added);
    }

    private static void RestoreInto(
        HierarchyNode node,
        IReadOnlyDictionary<string, HierarchyNode> authoritativeById,
        ISet<AuthoringObject> restoredComponents)
    {
        if (authoritativeById.TryGetValue(node.ObjectId, out HierarchyNode? reference))
            RestoreComponents(node, reference, restoredComponents);

        foreach (HierarchyNode child in node.Children)
            RestoreInto(child, authoritativeById, restoredComponents);
    }

    /// <summary>オブジェクト 1 つ分の欠けの埋め戻し</summary>
    /// <remarks>同じ型名のコンポーネントは複数付くので、型名ごとの個数で突き合わせる</remarks>
    private static void RestoreComponents(
        HierarchyNode node, HierarchyNode reference, ISet<AuthoringObject> restoredComponents)
    {
        if (reference.Components.Count == 0) return;

        var present = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (AuthoringObject component in node.Components)
            present[component.TypeName] = present.GetValueOrDefault(component.TypeName) + 1;

        for (int i = 0; i < reference.Components.Count; i++)
        {
            AuthoringObject component = reference.Components[i];
            if (present.TryGetValue(component.TypeName, out int count) && count > 0)
            {
                present[component.TypeName] = count - 1;
                continue;
            }

            AuthoringObject restored = component.Clone();
            node.Components.Insert(Math.Min(i, node.Components.Count), restored);
            restoredComponents.Add(restored);
        }
    }
}
