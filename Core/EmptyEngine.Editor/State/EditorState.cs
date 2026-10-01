namespace EmptyEngine.Editor.State;

/// <summary>エディタのセッション間で保つ UI 状態</summary>
public sealed class EditorState
{
    /// <summary>ペイン名ごとの幅（px）</summary>
    /// <remarks>伸縮するペインは幅を持たないので入らない。</remarks>
    public Dictionary<string, double> PaneWidths { get; set; } = new();

    /// <summary>ペイン名ごとの畳み状態</summary>
    /// <remarks>記録の無いペインは画面の既定に従う。</remarks>
    public Dictionary<string, bool> CollapsedPanes { get; set; } = new();

    /// <summary>Hierarchy で展開している ObjectId（シーンアセットキー別）</summary>
    public Dictionary<string, List<string>> ExpandedHierarchyObjects { get; set; } = new();

    /// <summary>前回選んでいた配布ビルドの名前</summary>
    public string? BuildTarget { get; set; }

    /// <summary>ビルド名 → 前回の配布先</summary>
    public Dictionary<string, string> BuildOutputs { get; set; } = new();
}
