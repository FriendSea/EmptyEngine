namespace EmptyEngine.Editor.Components.Panels;

/// <summary>エディタで表示できるペインの一覧。</summary>
internal static class EditorPanes
{
    private static readonly Dictionary<string, Type> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hierarchy"] = typeof(HierarchyPanel),
        ["inspector"] = typeof(InspectorPanel),
        ["build"] = typeof(BuildPanel),
    };

    /// <summary>出せるペインの名前（宣言順）</summary>
    public static IReadOnlyCollection<string> Names => ByName.Keys;

    /// <summary>名前に対応するペイン（無ければ <c>null</c>）</summary>
    public static Type? Find(string? name) =>
        name is not null && ByName.TryGetValue(name, out Type? pane) ? pane : null;
}
