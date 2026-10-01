using System.Numerics;
using EmptyEngine.Generators;
using EmptyEngine.ObjectModel;

namespace EmptyEngine.Collision;

public interface ISyncableCollider
{
    void Sync();
}

/// <summary>Base class for collider components.</summary>
public abstract partial class ColliderComponent : ICollider, ISyncableCollider
{
    private IObject? _owner;

    [ResolveAsset("Registry")]
    private ColliderRegistry? _resolved;

    private ColliderRegistry? _overrideRegistry;
    private ColliderRegistry? _registeredIn;
    private bool _live;
    private bool _disabled;
    private bool _synced;
    private Matrix4x4 _syncedTransform;

    public IObject? Owner => _owner;

    public ColliderRegistry? ResolvedRegistry => _resolved;

    public bool Enabled
    {
        set
        {
            _disabled = !value;
            UpdateRegistration();
        }
    }

    /// <summary>登録先レジストリの差し替え</summary>
    /// <remarks>null で宣言どおりの登録先へ戻す</remarks>
    public void ChangeRegistry(ColliderRegistry? registry)
    {
        if (ReferenceEquals(_overrideRegistry, registry))
            return;

        _overrideRegistry = registry;
        UpdateRegistration();
    }

    public void OnDeserialized(IObject owner)
    {
        _owner = owner;
        Sync();
        UpdateRegistration();
    }

    public void OnCreated(IObject owner)
    {
        _owner = owner;
        _live = true;
        Sync();
        UpdateRegistration();
    }

    public void OnDestroy(IObject owner)
    {
        _live = false;
        _synced = false;
        UpdateRegistration();
        _owner = null;
        _resolved = null;
        _overrideRegistry = null;
    }

    private void UpdateRegistration()
    {
        ColliderRegistry? target = _live && !_disabled ? _overrideRegistry ?? _resolved : null;
        if (ReferenceEquals(target, _registeredIn))
            return;

        _registeredIn?.Unregister(this);
        target?.Register(this);
        _registeredIn = target;
    }

    /// <summary>
    /// Captures the current world transform and rebuilds the collider's cached world-space shape.
    /// Collision queries use this snapshot until the next registry sync.
    /// </summary>
    public void Sync()
    {
        _syncedTransform = TransformComponent.GetWorldMatrix(_owner);
        SyncShape(_syncedTransform);
        _synced = true;
    }

    /// <summary>
    /// Keeps direct collider queries usable before the first registry sync.
    /// Once synchronized, queries never touch the scene transform until the next explicit sync.
    /// </summary>
    protected void EnsureSynced()
    {
        if (!_synced)
            Sync();
    }

    protected Matrix4x4 SyncedTransform
    {
        get
        {
            EnsureSynced();
            return _syncedTransform;
        }
    }

    protected Vector3 Position => SyncedTransform.Translation;

    /// <summary>Returns the world transform captured by the most recent sync.</summary>
    protected Matrix4x4 ReadTransform() => SyncedTransform;

    /// <summary>Rebuilds shape-specific world-space data from one captured world transform.</summary>
    protected virtual void SyncShape(Matrix4x4 worldTransform)
    {
    }

    public abstract bool Overlap(Vector3 center, float radius);

    public abstract Vector3 ResolvePenetration(Vector3 center, float radius);

    public abstract bool Raycast(Vector3 a, Vector3 b, out float t);

    /// <summary>Convenience overload for collision queries on the XY plane.</summary>
    public bool Overlap(Vector2 center, float radius)
        => Overlap(new Vector3(center, 0f), radius);

    /// <summary>Convenience overload for collision queries on the XY plane.</summary>
    public Vector2 ResolvePenetration(Vector2 center, float radius)
    {
        Vector3 offset = ResolvePenetration(new Vector3(center, 0f), radius);
        return new Vector2(offset.X, offset.Y);
    }

    /// <summary>Convenience overload for collision queries on the XY plane.</summary>
    public bool Raycast(Vector2 a, Vector2 b, out float t)
        => Raycast(new Vector3(a, 0f), new Vector3(b, 0f), out t);

    public Vector3 ResolvePenetration(ColliderRegistry? target)
    {
        if (target is null || _owner is null)
            return Vector3.Zero;

        Vector3 offset = PenetrationOffset(target);
        if (offset == Vector3.Zero)
            return Vector3.Zero;

        if (_owner.GetAttachable<TransformComponent>() is not { } transform)
            return Vector3.Zero;

        transform.WorldPosition += offset;
        return offset;
    }

    protected abstract Vector3 PenetrationOffset(ColliderRegistry target);
}
