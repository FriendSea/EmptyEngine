using EmptyEngine.Editor.ViewModels;
using EmptyEngine.Editor.ViewModels.Hierarchy;

namespace EmptyEngine.Editor.Api;

/// <summary><c>GET /api/editor/hierarchy</c> が返す木の組み立て</summary>
/// <remarks>返るのは呼んだ時点の写しで、取得後の編集は反映しない。</remarks>
public sealed class HierarchyApi
{
    private readonly EditorViewModel _editor;

    internal HierarchyApi(EditorViewModel editor) => _editor = editor;

    public HierarchyResponse GetHierarchy()
    {
        Dictionary<string, LoadedSceneInfo> scenes = _editor.GetLoadedScenes()
            .ToDictionary(scene => scene.SceneId, StringComparer.Ordinal);

        HierarchyNodeIdentity? selected = _editor.SelectedNode is { } node
            ? new HierarchyNodeIdentity(node.SceneId, node.ObjectId)
            : null;

        HierarchyItemResponse[] roots = _editor.RootNodes.Snapshot
            .Select(root => new HierarchyItemResponse(root, scenes.GetValueOrDefault(root.SceneId)))
            .ToArray();

        return new HierarchyResponse(_editor.IsPlaying, selected, roots);
    }
}

/// <summary><c>GET /api/editor/hierarchy</c> の応答</summary>
public sealed record HierarchyResponse(
    bool IsPlaying,
    HierarchyNodeIdentity? Selected,
    IReadOnlyList<HierarchyItemResponse> Roots);

/// <summary>ロード済みシーンインスタンスの中でのノードの身元</summary>
public sealed record HierarchyNodeIdentity(string SceneId, string ObjectId);

/// <summary>応答に載るヒエラルキーノード 1 つ分</summary>
public sealed class HierarchyItemResponse
{
    internal HierarchyItemResponse(HierarchyNodeViewModel model, LoadedSceneInfo? scene)
    {
        SceneId = model.SceneId;
        ObjectId = model.ObjectId;
        Name = model.Name;
        DisplayName = model.DisplayName;
        Active = model.Active;
        IsSceneRoot = model.IsSceneRoot;
        IsPrefabRoot = model.IsPrefabRoot;
        Scene = scene;
        Children = model.Children.Snapshot
            .Select(child => new HierarchyItemResponse(child, null))
            .ToArray();
    }

    public string SceneId { get; }
    public string ObjectId { get; }
    public string Name { get; }

    /// <summary>未保存のシーンなら <c>*</c> が付いた名前</summary>
    public string DisplayName { get; }
    public bool Active { get; }
    public bool IsSceneRoot { get; }
    public bool IsPrefabRoot { get; }

    /// <summary>シーンインスタンスのルートのときだけ、その元アセット</summary>
    /// <remarks><c>/api/editor/scenes</c> が返すものと同じ形</remarks>
    public LoadedSceneInfo? Scene { get; }
    public IReadOnlyList<HierarchyItemResponse> Children { get; }
}
