using EmptyEngine.ObjectModel;

namespace EmptyEngine.World;

/// <summary>シーングラフのノード</summary>
public sealed class GameObject : IObject
{
    private readonly List<IAttachable> _attachables;
    private readonly SceneWorld? _world;
    private List<GameObject> _children;
    private Dictionary<string, GameObject>? _sceneIndex;
    private GameObject? _parent;
    private bool _inWorld;
    private CancellationTokenSource? _lifetime;
    private bool _lifetimeEnded;

    public GameObject(
        string id,
        string name,
        IReadOnlyList<IAttachable> attachables,
        IReadOnlyList<GameObject>? children = null,
        bool active = true)
    {
        Id = id;
        Name = name;
        Active = active;
        _children = children is null ? [] : new List<GameObject>(children);
        foreach (GameObject child in _children)
            child._parent = this;
        _attachables = new List<IAttachable>(attachables);
    }

    /// <summary>世界が組み上げたコンポーネント列・子列の引き取り</summary>
    internal GameObject(
        SceneWorld world,
        string id,
        string name,
        List<IAttachable> attachables,
        List<GameObject> children,
        bool active = true)
    {
        Id = id;
        Name = name;
        Active = active;
        _world = world;
        _inWorld = true;
        _attachables = attachables;
        _children = children;
        foreach (GameObject child in _children)
            child._parent = this;
    }

    /// <summary>子オブジェクト</summary>
    public IReadOnlyList<GameObject> Children => _children;

    public string Id { get; }

    public string Name { get; private set; }

    /// <summary>親オブジェクト</summary>
    public IObject? Parent => _parent;

    /// <summary>オブジェクトとその子孫を有効化・無効化する</summary>
    /// <remarks>無効化した部分木のコンポーネントには <c>OnDestroy</c> が、有効化では <c>OnCreated</c> が走る</remarks>
    public void SetActive(bool active)
    {
        if (Active == active)
            return;

        if (_world is not { } world || !_inWorld)
        {
            Log($"SetActive({active}) ignored: '{Name}' is not in the world (destroyed or detached).");
            return;
        }

        if (!active)
        {
            var destroyed = new List<(IAttachable, GameObject)>();
            if (ActiveInHierarchy)
                CollectActive(destroyed);
            Active = false;
            foreach ((IAttachable attachable, GameObject owner) in destroyed)
                world.InvokeDestroy(attachable, owner);
        }
        else
        {
            var created = new List<(IAttachable, GameObject)>();
            var deserialized = new List<(IAttachable, GameObject)>();
            Active = true;
            if (ActiveInHierarchy)
                CollectActive(created, deserialized);
            foreach ((IAttachable attachable, GameObject owner) in deserialized)
                if (owner.ActiveInHierarchy) world.InvokeDeserialized(attachable, owner);
            foreach ((IAttachable attachable, GameObject owner) in created)
                if (owner.ActiveInHierarchy) world.InvokeCreated(attachable, owner);
        }
    }

    /// <summary>フレーム境界でシーンから取り除くよう予約する</summary>
    /// <remarks>同じオブジェクトへの複数回の要求は一度だけ処理される。世界に属していなければ何もしない。</remarks>
    public void Destroy()
    {
        if (_world is { } world && _inWorld)
            world.ScheduleDestroy(this);
    }

    /// <summary>このシーンインスタンスのロード時の初期状態への復帰</summary>
    /// <remarks>ルートオブジェクト専用</remarks>
    public void Reload()
    {
        if (_world is not { } world || !_inWorld)
            throw new InvalidOperationException($"'{Name}' is not a live object owned by a world.");

        world.ReloadRoot(this);
    }

    /// <summary>生のコンポーネント列</summary>
    internal List<IAttachable> Attachables => _attachables;

    /// <summary>破棄までのトークン</summary>
    internal CancellationToken Lifetime =>
        _lifetimeEnded ? new CancellationToken(canceled: true) : (_lifetime ??= new CancellationTokenSource()).Token;

    /// <summary>寿命の打ち切り</summary>
    internal void EndLifetime()
    {
        if (_lifetimeEnded)
            return;

        _lifetimeEnded = true;
        CancellationTokenSource? lifetime = _lifetime;
        _lifetime = null;
        if (lifetime is null)
            return;

        try
        {
            lifetime.Cancel();
        }
        finally
        {
            lifetime.Dispose();
        }
    }

    /// <summary>擬似破棄による有効状態</summary>
    internal bool Active { get; set; }

    /// <summary>自身とすべての祖先を考慮した、ライフサイクル上の有効状態</summary>
    internal bool ActiveInHierarchy => Active && (_parent?.ActiveInHierarchy ?? true);

    /// <summary>世界につながった生きたオブジェクトか</summary>
    public bool IsAlive => _inWorld;

    /// <summary><paramref name="world"/> が抱えている生きたオブジェクトか</summary>
    internal bool IsIn(SceneWorld world) => IsAlive && ReferenceEquals(_world, world);

    /// <summary>世界からの切り離し</summary>
    internal void Detach() => _inWorld = false;

    internal void SetName(string name) => Name = name;

    /// <summary>木を辿らずに引ける生の子列</summary>
    internal List<GameObject> ChildList => _children;

    /// <summary>このノードが属する木の根</summary>
    internal GameObject RootNode
    {
        get
        {
            GameObject node = this;
            while (node._parent is not null)
                node = node._parent;
            return node;
        }
    }

    internal void SetChildren(List<GameObject> children)
    {
        _children = children;
        foreach (GameObject child in _children)
            child._parent = this;
        InvalidateSceneIndex();
    }

    /// <summary>子の末尾への追加</summary>
    internal void AddChild(GameObject child)
    {
        _children.Add(child);
        child._parent = this;
        InvalidateSceneIndex();
    }

    /// <summary>直接の子からの 1 つの取り除き</summary>
    internal bool RemoveChild(GameObject child)
    {
        if (!_children.Remove(child))
            return false;

        InvalidateSceneIndex();
        return true;
    }

    /// <summary>この木の Id 索引の破棄</summary>
    internal void InvalidateSceneIndex() => RootNode._sceneIndex = null;

    internal void SetAttachables(List<IAttachable> attachables)
    {
        _attachables.Clear();
        _attachables.AddRange(attachables);
    }

    public T? GetAttachable<T>() where T : class, IAttachable
    {
        foreach (IAttachable attachable in _attachables)
        {
            if (attachable is T typed)
            {
                return typed;
            }
        }

        return null;
    }

    public IEnumerable<IAttachable> GetAllAttachables() => new List<IAttachable>(_attachables);

    /// <summary>同一シーンインスタンス内で指定した Id を持つオブジェクト</summary>
    public IObject? Find(string id)
    {
        if (id is null)
            return null;

        GameObject root = RootNode;
        Dictionary<string, GameObject> index = root._sceneIndex ??= BuildSceneIndex(root);
        return index.TryGetValue(id, out GameObject? target) ? target : null;
    }

    /// <summary>有効な部分木のコンポーネントの収集</summary>
    private void CollectActive(List<(IAttachable, GameObject)> into)
    {
        if (!ActiveInHierarchy)
            return;

        foreach (IAttachable attachable in _attachables)
            into.Add((attachable, this));
        foreach (GameObject child in _children)
            child.CollectActive(into);
    }

    private void CollectActive(
        List<(IAttachable, GameObject)> created,
        List<(IAttachable, GameObject)> deserialized)
    {
        CollectActive(created);
        CollectActive(deserialized);
    }

    private void Log(string message)
    {
        if (_world is { } world)
            world.Log(message);
        else
            Console.Error.WriteLine($"[scene] {message}");
    }

    private static Dictionary<string, GameObject> BuildSceneIndex(GameObject root)
    {
        var index = new Dictionary<string, GameObject>(StringComparer.Ordinal);
        Add(root, index);
        return index;

        static void Add(GameObject node, Dictionary<string, GameObject> index)
        {
            index[node.Id] = node;
            foreach (GameObject child in node._children)
                Add(child, index);
        }
    }
}
