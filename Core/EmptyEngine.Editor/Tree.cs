namespace EmptyEngine.Editor;

/// <summary>親子の形だけを見る木の走査</summary>
/// <remarks>指定されたルート配下を対象とする。子ノードの取得方法と検索条件は呼び出し側が指定する。</remarks>
internal static class Tree
{
    /// <summary>条件に当たる最初のノード（自分自身を含む、前順）</summary>
    public static T? Find<T>(T? root, Func<T, IReadOnlyList<T>> children, Func<T, bool> match)
        where T : class
    {
        if (root is null) return null;
        if (match(root)) return root;

        foreach (T child in children(root))
        {
            if (Find(child, children, match) is { } found) return found;
        }

        return null;
    }

    /// <summary>条件に当たるノードの親</summary>
    /// <returns>ルート自身が当たっても <c>null</c>（ルートはこの木の中に親を持たない）</returns>
    public static T? FindParent<T>(T root, Func<T, IReadOnlyList<T>> children, Func<T, bool> match)
        where T : class
    {
        foreach (T child in children(root))
        {
            if (match(child)) return root;
            if (FindParent(child, children, match) is { } found) return found;
        }

        return null;
    }

    /// <summary>ルートから当たったノードまでの道</summary>
    /// <returns>先頭がルート、末尾が当たったノード。見つからなければ <c>null</c></returns>
    public static List<T>? FindPath<T>(T root, Func<T, IReadOnlyList<T>> children, Func<T, bool> match)
        where T : class
    {
        if (match(root)) return [root];

        foreach (T child in children(root))
        {
            if (FindPath(child, children, match) is { } sub)
            {
                sub.Insert(0, root);
                return sub;
            }
        }

        return null;
    }

    /// <summary>自分自身を含む前順の全ノード</summary>
    public static IEnumerable<T> Flatten<T>(T root, Func<T, IReadOnlyList<T>> children)
    {
        yield return root;

        foreach (T child in children(root))
        {
            foreach (T descendant in Flatten(child, children))
                yield return descendant;
        }
    }
}
