using EmptyEngine.ObjectModel;
using EmptyEngine.World;

namespace EmptyEngine.Modules.Testing;

/// <summary>テスト用：参照先から受け取りたいコンポーネント（<see cref="Marker"/>）</summary>
public sealed class Marker : IAttachable
{
    public string Tag { get; set; } = string.Empty;
}

/// <summary>コンポーネント参照を保持し OnDeserialized で解決するテスト用コンポーネント</summary>
public sealed class TypedReferenceProbe : ILifecycleAttachable
{
    public static readonly List<TypedReferenceProbe> Log = [];

    /// <summary>コンポーネント参照</summary>
    public ComponentReference<Marker> Target { get; set; }

    private Marker? _resolved;

    public Marker? Resolved => _resolved;

    public void OnCreated(IObject owner)
    {
    }

    public void OnDeserialized(IObject owner)
    {
        _resolved = owner.FindComponent(Target);
        Log.Add(this);
    }

    public void OnDestroy(IObject owner)
    {
    }
}

/// <summary>シーン内参照を配列で保持し要素ごとに解決するテスト用コンポーネント</summary>
public sealed class MultiReferenceProbe : ILifecycleAttachable
{
    public static readonly List<MultiReferenceProbe> Log = [];

    /// <summary>参照の配列（要素の TargetId だけが直列化される）</summary>
    public ObjectReference[] Targets { get; set; } = Array.Empty<ObjectReference>();

    private IObject?[] _resolved = Array.Empty<IObject?>();

    public IReadOnlyList<IObject?> Resolved => _resolved;

    public void OnCreated(IObject owner)
    {
    }

    public void OnDeserialized(IObject owner)
    {
        _resolved = Targets.Select(target => owner.FindObject(target)).ToArray();
        Log.Add(this);
    }

    public void OnDestroy(IObject owner)
    {
    }
}

/// <summary>シーン内参照を保持し OnDeserialized で解決するテスト用コンポーネント</summary>
public sealed class ReferenceProbe : ILifecycleAttachable
{
    public static readonly List<(IObject Owner, IObject? Resolved)> Log = [];

    /// <summary>参照（TargetId が直列化される）</summary>
    public ObjectReference Target { get; set; }

    private IObject? _resolved;

    public IObject? Resolved => _resolved;

    public void OnCreated(IObject owner)
    {
    }

    public void OnDeserialized(IObject owner)
    {
        _resolved = owner.FindObject(Target);
        Log.Add((owner, _resolved));
    }

    public void OnDestroy(IObject owner)
    {
    }
}
