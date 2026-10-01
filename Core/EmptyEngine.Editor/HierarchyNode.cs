namespace EmptyEngine.Editor;

/// <summary>保存・通信・復元で受け渡すエディタの状態。</summary>
public sealed class HierarchyNode
{
    public HierarchyNode(
        string objectId,
        string name,
        IReadOnlyList<HierarchyNode>? children = null,
        IReadOnlyList<AuthoringObject>? components = null,
        string sceneId = "",
        bool active = true)
    {
        ObjectId = objectId;
        Name = name;
        SceneId = sceneId;
        Active = active;
        Children = children is null ? new() : new(children);
        Components = components is null ? new() : new(components);
    }

    /// <summary>オブジェクトの安定 Id（参照先・ソース由来）</summary>
    public string ObjectId { get; }

    /// <summary>このノードが属するロード単位の識別子</summary>
    public string SceneId { get; set; }

    /// <summary>表示・シリアライズに使う名前</summary>
    public string Name { get; set; }

    public bool Active { get; set; }

    /// <summary>子ノード</summary>
    public List<HierarchyNode> Children { get; }

    /// <summary>コンポーネント</summary>
    public List<AuthoringObject> Components { get; }
}
