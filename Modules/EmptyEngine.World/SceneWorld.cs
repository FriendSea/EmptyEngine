using System.Numerics;
using EmptyEngine.Core;
using EmptyEngine.Serialization;
using EmptyEngine.ObjectModel;

namespace EmptyEngine.World;

/// <summary>ロード済みルートを抱えた生きた世界</summary>
public sealed class SceneWorld : ISceneSerializer, IDisposable
{
    private sealed record PreviousObjectState(
        GameObject Owner,
        bool ActiveInHierarchy,
        IReadOnlyList<IAttachable> Attachables);

    /// <summary>ランタイムが同時保持する 1 プレハブインスタンス分の状態</summary>
    private sealed class RootInstance(string instanceId, GameObject root, byte[] template)
    {
        public string InstanceId { get; set; } = instanceId;

        public GameObject Root { get; set; } = root;

        /// <summary>ロード時に一度だけ焼いた初期状態の単一ルート blob</summary>
        public byte[] Template { get; } = template;
    }

    private readonly IAssetResolver? _assetResolver;
    private readonly Action<string>? _log;
    private readonly IServiceProvider? _services;
    private readonly List<GameObject> _pendingDestructions = [];
    private readonly HashSet<GameObject> _pendingDestructionSet = new(ReferenceEqualityComparer.Instance);
    private List<RootInstance> _roots;
    private bool _flushingDestructions;
    private bool _disposed;

    public SceneWorld()
        : this(assetResolver: null)
    {
    }

    /// <param name="services">コンポーネントのコンストラクタ引数を解決する DI コンテナ。</param>
    public SceneWorld(IAssetResolver? assetResolver, Action<string>? log = null, IServiceProvider? services = null)
    {
        _assetResolver = assetResolver;
        _log = log;
        _services = services;
        _roots = [];
    }

    /// <summary>現在ワールドにロードされているシーンインスタンス名の一覧（読み込み中シーンの確認・ログ用）</summary>
    public IReadOnlyList<string> LoadedSceneNames => _roots.Select(s => s.Root.Name).ToList();

    /// <summary>ロードされているシーンインスタンスのルート一覧（ロード順）</summary>
    public IReadOnlyList<IObject> Roots => _roots.Select(s => (IObject)s.Root).ToList();

    /// <summary>ロード時の初期状態への復帰（<see cref="GameObject.Reload"/> の受け口）</summary>
    internal void ReloadRoot(GameObject target)
    {
        for (int i = 0; i < _roots.Count; i++)
        {
            if (!ReferenceEquals(_roots[i].Root, target))
                continue;

            RootInstance instance = _roots[i];

            var destroyed = new List<(IAttachable Attachable, GameObject Owner)>();
            var removed = new List<GameObject>();
            var created = new List<(IAttachable Attachable, GameObject Owner)>();
            var deserialized = new List<(IAttachable Attachable, GameObject Owner)>();

            CollectDestroy(instance.Root, destroyed, removed);
            ObjectData template = SceneBlob.Decode(instance.Template, _log, _services)[0].Root;
            instance.Root = BuildNewObject(template, created, deserialized);
            instance.Root.InvalidateSceneIndex();

            RunDestroy(destroyed, removed);
            foreach ((IAttachable attachable, GameObject owner) in deserialized)
                if (owner.ActiveInHierarchy) InvokeDeserialized(attachable, owner);
            foreach ((IAttachable attachable, GameObject owner) in created)
                if (owner.ActiveInHierarchy) InvokeCreated(attachable, owner);
            return;
        }

        throw new InvalidOperationException($"'{target.Name}' is not a live root instance of this world.");
    }

    private static byte[] EncodeTemplate(string instanceId, ObjectData data)
        => SceneBlob.Encode(new[] { new RootInstanceData(instanceId, data) });

    public byte[] SerializeScene()
    {
        var roots = _roots
            .Select(s => new RootInstanceData(s.InstanceId, ToObjectData(s.Root)))
            .ToList();
        return SceneBlob.Encode(roots);
    }

    /// <summary>シーン blob のデシリアライズと現在の世界への適用</summary>
    /// <remarks>blob が参照するアセットを読み込んでから世界へ適用する。</remarks>
    public async Task DeserializeSceneAsync(byte[] blob, bool isPlaying, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<RootInstanceData> roots = SceneBlob.Decode(blob, _log, _services);
        await LoadReferencedAssetsAsync(roots, cancellationToken);
        if (_disposed)
            return;

        Apply(roots, isPlaying);
    }

    private async Task LoadReferencedAssetsAsync(IReadOnlyList<RootInstanceData> roots, CancellationToken cancellationToken)
    {
        if (_assetResolver is null)
            return;

        Task ResolveAll() => AssetResolution.ResolveAllAsync(
            roots.SelectMany(root => AssetResolution.EnumerateTree(root.Root)),
            _assetResolver,
            (target, ownerName, e) => Log($"OnResolveAssetsAsync threw on '{ownerName}' ({target.GetType().Name}): {e}")).AsTask();

        if (_assetResolver is WorldAssetResolver world)
            await world.RunTurnAsync(ResolveAll, cancellationToken);
        else
            await ResolveAll();
    }

    private void Apply(IReadOnlyList<RootInstanceData> roots, bool isPlaying)
    {
        var beforeIds = _roots.Select(s => s.InstanceId).ToHashSet(StringComparer.Ordinal);
        List<PreviousObjectState> previousObjects = CaptureObjectStates();

        var created = new List<(IAttachable Attachable, GameObject Owner)>();
        var deserialized = new List<(IAttachable Attachable, GameObject Owner)>();
        var destroyed = new List<(IAttachable Attachable, GameObject Owner)>();
        var removed = new List<GameObject>();

        _roots = ReconcileRoots(roots, _roots, created, deserialized, destroyed, removed);

        foreach (RootInstance s in _roots)
            s.Root.InvalidateSceneIndex();

        RunReconcileDestroy(previousObjects);
        foreach (GameObject obj in removed)
            EndLifetimeGuarded(obj);
        foreach ((IAttachable attachable, GameObject owner) in deserialized)
            if (owner.ActiveInHierarchy) InvokeDeserialized(attachable, owner);
        if (isPlaying)
            foreach ((IAttachable attachable, GameObject owner) in deserialized)
                if (owner.ActiveInHierarchy && NeedsCreated(previousObjects, attachable, owner))
                    InvokeCreated(attachable, owner);

        if (_log is not null && !beforeIds.SetEquals(_roots.Select(s => s.InstanceId)))
        {
            string names = _roots.Count == 0 ? "(none)" : string.Join(", ", _roots.Select(s => s.Root.Name));
            string mode = isPlaying ? "play" : "edit";
            _log($"[scene] switch: {_roots.Count} prefab(s) [{names}] mode={mode}");
        }
    }

    /// <summary>この世界に属するもの全部の取り壊し</summary>
    /// <remarks>ロード済みルートすべてに <c>OnDestroy</c> が走り、オブジェクトの寿命が打ち切られる</remarks>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _pendingDestructions.Clear();
        _pendingDestructionSet.Clear();

        var destroyed = new List<(IAttachable Attachable, GameObject Owner)>();
        var removed = new List<GameObject>();
        foreach (RootInstance s in _roots)
            CollectDestroy(s.Root, destroyed, removed);
        _roots = [];

        RunDestroy(destroyed, removed);
    }

    /// <summary>オブジェクトの複製と世界への追加</summary>
    /// <param name="position">複製を置くワールド座標。省略時は複製元の位置のまま。</param>
    /// <exception cref="InvalidOperationException">複製が参照するアセットがまだ読み込まれていないとき（読み込みは <see cref="InstantiateAsync"/>）</exception>
    public IObject Instantiate(IObject source, IObject? parent = null, Vector3? position = null)
    {
        GameObject template = RequireTemplate(source, nameof(source));
        GameObject spawned = CloneStructure(template);

        var attachables = new List<(IAttachable Attachable, GameObject Owner)>(CountAttachables(spawned));
        CollectPreOrder(spawned, attachables);

        foreach ((IAttachable attachable, GameObject ownerObj) in attachables)
        {
            if (!ResolveAssetsNow(attachable, ownerObj))
                throw new InvalidOperationException(
                    $"'{template.Name}' references assets that are not loaded yet. Use InstantiateAsync, "
                    + "or resolve the prefab from an asset resolution hook so that its assets load in advance.");
        }

        return Place(spawned, attachables, parent, position);
    }

    /// <summary>参照先のプレハブの読み込みとその複製の世界への追加</summary>
    public async ValueTask<IObject> InstantiateAsync(
        AssetReference<IObject> source, IObject? parent = null, CancellationToken cancellationToken = default)
    {
        IObject? loaded = _assetResolver is null
            ? null
            : await _assetResolver.ResolveAsync(source, cancellationToken);
        GameObject template = RequireTemplate(loaded, nameof(source));
        GameObject spawned = CloneStructure(template);

        var attachables = new List<(IAttachable Attachable, GameObject Owner)>(CountAttachables(spawned));
        CollectPreOrder(spawned, attachables);
        await AssetResolution.ResolveAllAsync(
            attachables.Select(entry => ((object)entry.Attachable, entry.Owner.Name)),
            _assetResolver!,
            (target, ownerName, e) => Log($"OnResolveAssetsAsync threw on '{ownerName}' ({target.GetType().Name}): {e}"));

        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Place(spawned, attachables, parent, position: null);
    }

    private static GameObject RequireTemplate(IObject? source, string parameterName)
    {
        if (source is null)
            throw new ArgumentNullException(
                parameterName,
                "Source prefab is null. The AssetReference<IObject> it came from did not resolve "
                + "(missing artifact, or the reference was never assigned).");

        if (source is not GameObject template)
            throw new ArgumentException("Source must be a runtime object produced by this world.", parameterName);

        return template;
    }

    /// <summary>参照の解決が済んだ複製の世界への追加とライフサイクルの通知</summary>
    private GameObject Place(
        GameObject spawned,
        List<(IAttachable Attachable, GameObject Owner)> attachables,
        IObject? parent,
        Vector3? position)
    {
        GameObject? parentObject = null;
        if (parent is not null)
        {
            if (parent is not GameObject live || !live.IsIn(this))
                throw new ArgumentException("Parent must be a live object owned by this world.", nameof(parent));

            parentObject = live;
        }

        if (position is { } worldPosition && spawned.GetAttachable<TransformComponent>() is { } transform)
            transform.Position = ToLocalPosition(worldPosition, parentObject);

        if (parentObject is null)
        {
            string instanceId = Guid.NewGuid().ToString();
            _roots.Add(new RootInstance(instanceId, spawned, EncodeTemplate(instanceId, ToObjectData(spawned))));
        }
        else
        {
            parentObject.AddChild(spawned);
        }

        foreach ((IAttachable attachable, GameObject ownerObj) in attachables)
            if (ownerObj.ActiveInHierarchy) InvokeDeserialized(attachable, ownerObj, resolveAssets: false);
        foreach ((IAttachable attachable, GameObject ownerObj) in attachables)
            if (ownerObj.ActiveInHierarchy) InvokeCreated(attachable, ownerObj);

        return spawned;
    }

    /// <exception cref="InvalidOperationException">親のワールド行列が逆行列を持たないとき</exception>
    private static Vector3 ToLocalPosition(Vector3 worldPosition, GameObject? parent)
    {
        if (parent is null)
            return worldPosition;

        if (!Matrix4x4.Invert(TransformComponent.GetWorldMatrix(parent), out Matrix4x4 inverse))
            throw new InvalidOperationException("Cannot place the instance because the parent transform is not invertible.");

        return Vector3.Transform(worldPosition, inverse);
    }

    /// <summary>取り除きの予約（<see cref="GameObject.Destroy"/> の受け口）</summary>
    internal void ScheduleDestroy(GameObject obj)
    {
        if (_disposed)
            return;

        if (_pendingDestructionSet.Add(obj))
            _pendingDestructions.Add(obj);
    }

    /// <summary><see cref="GameObject.Destroy"/> で予約されたオブジェクトの破棄を適用する</summary>
    /// <remarks>
    /// シーンオブジェクトの列挙が完了したフレーム境界で呼び出す。
    /// 破棄コールバックから追加された要求も、このメソッドから戻る前に適用される。
    /// </remarks>
    public void FlushPendingDestructions()
    {
        if (_disposed || _flushingDestructions)
            return;

        _flushingDestructions = true;
        try
        {
            while (_pendingDestructions.Count > 0)
            {
                List<GameObject> pending = new(_pendingDestructions);
                _pendingDestructions.Clear();
                _pendingDestructionSet.Clear();

                foreach (GameObject obj in pending)
                    DestroyNow(obj);
            }
        }
        finally
        {
            _flushingDestructions = false;
        }
    }

    private void DestroyNow(GameObject obj)
    {
        var destroyed = new List<(IAttachable Attachable, GameObject Owner)>();
        var removed = new List<GameObject>();

        for (int i = 0; i < _roots.Count; i++)
        {
            if (!ReferenceEquals(_roots[i].Root, obj))
                continue;

            CollectDestroy(_roots[i].Root, destroyed, removed);
            _roots.RemoveAt(i);

            RunDestroy(destroyed, removed, onlyActive: false);
            return;
        }

        if (obj.IsIn(this)
            && obj.Parent is GameObject parent
            && parent.RemoveChild(obj))
        {
            CollectDestroy(obj, destroyed, removed);
            RunDestroy(destroyed, removed, onlyActive: false);
        }
    }

    private GameObject CloneStructure(GameObject source)
    {
        List<IAttachable> sourceAttachables = source.Attachables;
        var attachables = new List<IAttachable>(sourceAttachables.Count);
        for (int i = 0; i < sourceAttachables.Count; i++)
        {
            attachables.Add((IAttachable)InstanceActivator.CreateDeep(sourceAttachables[i], _services));
        }

        List<GameObject> sourceChildren = source.ChildList;
        var children = new List<GameObject>(sourceChildren.Count);
        for (int i = 0; i < sourceChildren.Count; i++)
            children.Add(CloneStructure(sourceChildren[i]));

        return new GameObject(this, source.Id, source.Name, attachables, children, source.Active);
    }

    private static int CountAttachables(GameObject node)
    {
        int count = node.Attachables.Count;
        foreach (GameObject child in node.ChildList)
            count += CountAttachables(child);
        return count;
    }

    private List<RootInstance> ReconcileRoots(
        IReadOnlyList<RootInstanceData> newRoots,
        IReadOnlyList<RootInstance> oldRoots,
        List<(IAttachable, GameObject)> created,
        List<(IAttachable, GameObject)> deserialized,
        List<(IAttachable, GameObject)> destroyed,
        List<GameObject> removed)
    {
        var oldById = new Dictionary<string, Queue<RootInstance>>(StringComparer.Ordinal);
        foreach (RootInstance s in oldRoots)
            Enqueue(oldById, s.InstanceId, s);

        var result = new List<RootInstance>(newRoots.Count);

        foreach (RootInstanceData nd in newRoots)
        {
            if (oldById.TryGetValue(nd.InstanceId, out Queue<RootInstance>? q) && q.Count > 0)
            {
                RootInstance old = q.Dequeue();
                old.Root = ReconcileObject(nd.Root, old.Root, created, deserialized, destroyed, removed);
                result.Add(old);
            }
            else
            {
                result.Add(new RootInstance(
                    nd.InstanceId,
                    BuildNewObject(nd.Root, created, deserialized),
                    EncodeTemplate(nd.InstanceId, nd.Root)));
            }
        }

        foreach (Queue<RootInstance> q in oldById.Values)
            foreach (RootInstance leftover in q)
                CollectDestroy(leftover.Root, destroyed, removed);

        return result;
    }

    private static void Enqueue<T>(Dictionary<string, Queue<T>> byKey, string key, T value)
    {
        if (!byKey.TryGetValue(key, out Queue<T>? queue))
        {
            queue = new Queue<T>();
            byKey[key] = queue;
        }

        queue.Enqueue(value);
    }

    private static ObjectData ToObjectData(GameObject obj)
    {
        var components = obj.GetAllAttachables()
            .Select(a => new ComponentData(a.GetType().FullName ?? a.GetType().Name, a))
            .ToList();
        var children = obj.Children.Select(ToObjectData).ToList();
        return new ObjectData(obj.Id, obj.Name, components, children, obj.Active);
    }

    private List<GameObject> ReconcileList(
        IReadOnlyList<ObjectData> newDatas,
        IReadOnlyList<GameObject> oldObjects,
        List<(IAttachable, GameObject)> created,
        List<(IAttachable, GameObject)> deserialized,
        List<(IAttachable, GameObject)> destroyed,
        List<GameObject> removed)
    {
        var oldById = new Dictionary<string, Queue<GameObject>>(StringComparer.Ordinal);
        foreach (GameObject o in oldObjects)
            Enqueue(oldById, o.Id, o);

        var result = new List<GameObject>(newDatas.Count);

        foreach (ObjectData nd in newDatas)
        {
            if (oldById.TryGetValue(nd.Id, out Queue<GameObject>? q) && q.Count > 0)
            {
                result.Add(ReconcileObject(nd, q.Dequeue(), created, deserialized, destroyed, removed));
            }
            else
            {
                result.Add(BuildNewObject(nd, created, deserialized));
            }
        }

        foreach (Queue<GameObject> q in oldById.Values)
            foreach (GameObject leftover in q)
                CollectDestroy(leftover, destroyed, removed);

        return result;
    }

    private GameObject ReconcileObject(
        ObjectData nd,
        GameObject old,
        List<(IAttachable, GameObject)> created,
        List<(IAttachable, GameObject)> deserialized,
        List<(IAttachable, GameObject)> destroyed,
        List<GameObject> removed)
    {
        old.SetName(nd.Name);
        List<IAttachable> newAttachables =
            ReconcileComponents(nd.Components, old.Attachables, old, created, deserialized, destroyed);
        old.SetAttachables(newAttachables);
        old.Active = nd.Active;

        List<GameObject> newChildren =
            ReconcileList(nd.Children, old.Children, created, deserialized, destroyed, removed);
        old.SetChildren(newChildren);

        return old;
    }

    private List<IAttachable> ReconcileComponents(
        IReadOnlyList<ComponentData> newComps,
        IReadOnlyList<IAttachable> oldAttachables,
        GameObject owner,
        List<(IAttachable, GameObject)> created,
        List<(IAttachable, GameObject)> deserialized,
        List<(IAttachable, GameObject)> destroyed)
    {
        var oldByType = new Dictionary<string, Queue<IAttachable>>(StringComparer.Ordinal);
        foreach (IAttachable attachable in oldAttachables)
        {
            Type attachableType = attachable.GetType();
            Enqueue(oldByType, attachableType.FullName ?? attachableType.Name, attachable);
        }

        var newAttachables = new List<IAttachable>(newComps.Count);
        foreach (ComponentData cd in newComps)
        {
            if (cd.Component is null)
            {
                continue;
            }

            if (oldByType.TryGetValue(cd.TypeName, out Queue<IAttachable>? q) && q.Count > 0)
            {
                IAttachable kept = UpdateAttachable(q.Dequeue(), cd.Component);
                newAttachables.Add(kept);
                deserialized.Add((kept, owner));
            }
            else
            {
                IAttachable attachable = cd.Component;
                newAttachables.Add(attachable);
                created.Add((attachable, owner));
                deserialized.Add((attachable, owner));
            }
        }

        foreach (Queue<IAttachable> q in oldByType.Values)
            foreach (IAttachable leftover in q)
                destroyed.Add((leftover, owner));

        return newAttachables;
    }

    private GameObject BuildNewObject(
        ObjectData nd,
        List<(IAttachable, GameObject)> created,
        List<(IAttachable, GameObject)> deserialized)
    {
        GameObject obj = BuildStructure(nd);
        CollectPreOrder(obj, created, deserialized);
        return obj;
    }

    private GameObject BuildStructure(ObjectData nd)
    {
        var attachables = nd.Components
            .Where(c => c.Component is not null)
            .Select(c => c.Component!)
            .ToList();
        var children = nd.Children.Select(BuildStructure).ToList();
        return new GameObject(this, nd.Id, nd.Name, attachables, children, nd.Active);
    }

    private static void CollectPreOrder(
        GameObject obj,
        List<(IAttachable, GameObject)> created,
        List<(IAttachable, GameObject)> deserialized)
    {
        CollectPreOrder(obj, created);
        CollectPreOrder(obj, deserialized);
    }

    private static void CollectPreOrder(GameObject obj, List<(IAttachable, GameObject)> into)
    {
        foreach (IAttachable attachable in obj.Attachables)
            into.Add((attachable, obj));
        foreach (GameObject child in obj.ChildList)
            CollectPreOrder(child, into);
    }

    private List<PreviousObjectState> CaptureObjectStates()
    {
        var states = new List<PreviousObjectState>();
        foreach (RootInstance root in _roots)
            CaptureObjectStates(root.Root, states);
        return states;
    }

    private static void CaptureObjectStates(GameObject owner, List<PreviousObjectState> states)
    {
        states.Add(new PreviousObjectState(
            owner, owner.ActiveInHierarchy, new List<IAttachable>(owner.Attachables)));
        foreach (GameObject child in owner.ChildList)
            CaptureObjectStates(child, states);
    }

    private void RunReconcileDestroy(IReadOnlyList<PreviousObjectState> previousObjects)
    {
        foreach (PreviousObjectState previous in previousObjects)
        {
            if (!previous.ActiveInHierarchy)
                continue;

            bool ownerStillActive = previous.Owner.IsIn(this)
                && previous.Owner.ActiveInHierarchy;
            foreach (IAttachable attachable in previous.Attachables)
            {
                if (!ownerStillActive || !ContainsReference(previous.Owner.Attachables, attachable))
                    InvokeDestroy(attachable, previous.Owner);
            }
        }
    }

    private static bool NeedsCreated(
        IReadOnlyList<PreviousObjectState> previousObjects,
        IAttachable attachable,
        GameObject owner)
    {
        foreach (PreviousObjectState previous in previousObjects)
        {
            if (!ReferenceEquals(previous.Owner, owner))
                continue;
            return !previous.ActiveInHierarchy || !ContainsReference(previous.Attachables, attachable);
        }

        return true;
    }

    private static bool ContainsReference(
        IReadOnlyList<IAttachable> attachables,
        IAttachable target)
    {
        foreach (IAttachable attachable in attachables)
            if (ReferenceEquals(attachable, target))
                return true;
        return false;
    }

    /// <summary>部分木のコンポーネントの取り壊し対象への収集と、世界からの切り離し</summary>
    /// <param name="removed">寿命を打ち切るオブジェクトの収集先。</param>
    private static void CollectDestroy(
        GameObject old, List<(IAttachable, GameObject)> destroyed, List<GameObject> removed)
    {
        removed.Add(old);
        old.Detach();
        foreach (IAttachable attachable in old.Attachables)
            destroyed.Add((attachable, old));
        foreach (GameObject child in old.ChildList)
            CollectDestroy(child, destroyed, removed);
    }

    /// <summary>一致した既存コンポーネントへの新インスタンスの値の反映</summary>
    private static IAttachable UpdateAttachable(IAttachable old, IAttachable newInstance)
    {
        InstanceActivator.CopySerializedMembers(old, newInstance);
        return old;
    }

    /// <summary>取り壊し 1 回分の実行</summary>
    /// <param name="removed">寿命を打ち切るオブジェクト。</param>
    /// <param name="onlyActive">擬似破棄で既に <c>OnDestroy</c> を通したものを飛ばすなら <c>true</c>。</param>
    private void RunDestroy(
        List<(IAttachable Attachable, GameObject Owner)> destroyed,
        List<GameObject> removed,
        bool onlyActive = true)
    {
        foreach ((IAttachable attachable, GameObject owner) in destroyed)
            if (!onlyActive || owner.ActiveInHierarchy) InvokeDestroy(attachable, owner);

        foreach (GameObject obj in removed)
            EndLifetimeGuarded(obj);
    }

    /// <summary>寿命の打ち切り</summary>
    private void EndLifetimeGuarded(GameObject owner)
    {
        try
        {
            owner.EndLifetime();
        }
        catch (Exception e) when (IsCancellation(e))
        {
        }
        catch (Exception e)
        {
            Log($"Lifetime cancellation threw on '{owner.Name}': {e}");
        }
    }

    /// <summary>キャンセルの合図とみなせる例外か</summary>
    private static bool IsCancellation(Exception exception) => exception switch
    {
        OperationCanceledException => true,
        AggregateException aggregate => aggregate.InnerExceptions.All(IsCancellation),
        _ => false,
    };

    internal void InvokeCreated(IAttachable attachable, GameObject owner)
    {
        if (attachable is not ILifecycleAttachable lifecycle)
            return;

        try
        {
            lifecycle.OnCreated(owner);
        }
        catch (Exception e) when (IsCancellation(e))
        {
        }
        catch (Exception e)
        {
            LogHookFailure("OnCreated", attachable, owner, e);
        }
    }

    /// <inheritdoc cref="InvokeCreated"/>
    /// <remarks>先に解決フック（<see cref="IAssetResolutionHook"/>）を同期に済ませる。</remarks>
    internal void InvokeDeserialized(IAttachable attachable, GameObject owner, bool resolveAssets = true)
    {
        if (resolveAssets && !ResolveAssetsNow(attachable, owner))
            Log($"'{owner.Name}' ({attachable.GetType().Name}) was applied before the assets it references finished loading.");

        if (attachable is not ILifecycleAttachable lifecycle)
            return;

        try
        {
            lifecycle.OnDeserialized(owner);
        }
        catch (Exception e) when (IsCancellation(e))
        {
        }
        catch (Exception e)
        {
            LogHookFailure("OnDeserialized", attachable, owner, e);
        }
    }

    /// <inheritdoc cref="InvokeCreated"/>
    internal void InvokeDestroy(IAttachable attachable, GameObject owner)
    {
        if (attachable is not ILifecycleAttachable lifecycle)
            return;

        try
        {
            lifecycle.OnDestroy(owner);
        }
        catch (Exception e) when (IsCancellation(e))
        {
        }
        catch (Exception e)
        {
            LogHookFailure("OnDestroy", attachable, owner, e);
        }
    }

    /// <summary>解決フックの同期の呼び出し</summary>
    /// <returns>読み込み待ちが残れば <c>false</c>（フックは読み込みの完了後に続きを終える）</returns>
    private bool ResolveAssetsNow(IAttachable attachable, GameObject owner)
    {
        if (_assetResolver is null || attachable is not IAssetResolutionHook hook)
            return true;

        ValueTask resolving;
        try
        {
            resolving = hook.OnResolveAssetsAsync(_assetResolver);
        }
        catch (Exception e) when (IsCancellation(e))
        {
            return true;
        }
        catch (Exception e)
        {
            LogHookFailure(nameof(IAssetResolutionHook.OnResolveAssetsAsync), attachable, owner, e);
            return true;
        }

        if (!resolving.IsCompleted)
        {
            _ = ObserveLateResolutionAsync(resolving, attachable, owner);
            return false;
        }

        try
        {
            resolving.GetAwaiter().GetResult();
        }
        catch (Exception e) when (IsCancellation(e))
        {
        }
        catch (Exception e)
        {
            LogHookFailure(nameof(IAssetResolutionHook.OnResolveAssetsAsync), attachable, owner, e);
        }

        return true;
    }

    private async Task ObserveLateResolutionAsync(ValueTask resolving, IAttachable attachable, GameObject owner)
    {
        try
        {
            await resolving;
        }
        catch (Exception e) when (IsCancellation(e))
        {
        }
        catch (Exception e)
        {
            LogHookFailure(nameof(IAssetResolutionHook.OnResolveAssetsAsync), attachable, owner, e);
        }
    }

    private void LogHookFailure(string hook, IAttachable attachable, GameObject owner, Exception e)
        => Log($"{hook} threw on '{owner.Name}' ({attachable.GetType().Name}): {e}");

    internal void Log(string message)
    {
        if (_log is not null)
            _log($"[scene] {message}");
        else
            Console.Error.WriteLine($"[scene] {message}");
    }
}
