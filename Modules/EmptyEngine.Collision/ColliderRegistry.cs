using System.Numerics;
using EmptyEngine.ObjectModel;

namespace EmptyEngine.Collision;

/// <summary>A named group of colliders that can be queried together.</summary>
[Asset]
public sealed class ColliderRegistry
{
    private List<ICollider>? _items;
    private HashSet<ICollider>? _live;
    private HashSet<ICollider>? _vacated;

    /// <summary>登録済みコライダーを登録順に返す</summary>
    private List<ICollider> Items
    {
        get
        {
            List<ICollider> items = _items ??= [];
            if (_vacated is { Count: > 0 }) Compact();
            return items;
        }
    }

    public IReadOnlyList<ICollider> All => Items;

    /// <summary>
    /// Captures world-space shape data for every registered collider.
    /// Call this after transforms have been updated and before running collision queries.
    /// </summary>
    public void Sync()
    {
        foreach (ICollider item in Items)
        {
            if (item is ISyncableCollider syncable)
                syncable.Sync();
        }
    }

    public void Register(ICollider item)
    {
        List<ICollider> items = _items ??= [];
        HashSet<ICollider> live = _live ??= new HashSet<ICollider>(ReferenceEqualityComparer.Instance);
        if (live.Contains(item))
            return;

        // 再登録時に空席が残らないよう、登録前に詰める。
        if (_vacated?.Remove(item) == true) Compact();

        live.Add(item);
        items.Add(item);
    }

    public void Unregister(ICollider item)
    {
        if (_live?.Remove(item) != true)
            return;

        (_vacated ??= new HashSet<ICollider>(ReferenceEqualityComparer.Instance)).Add(item);
    }

    private void Compact()
    {
        HashSet<ICollider> live = _live ??= new HashSet<ICollider>(ReferenceEqualityComparer.Instance);
        _items?.RemoveAll(item => !live.Contains(item));
        _vacated?.Clear();
    }

    public ICollider? Find(Vector3 worldPoint)
    {
        foreach (ICollider item in Items)
        {
            if (item.Overlap(worldPoint, 0f))
                return item;
        }

        return null;
    }

    public ICollider? Find(Vector2 worldPoint) => Find(new Vector3(worldPoint, 0f));

    public bool Contains(Vector3 worldPoint) => Find(worldPoint) is not null;

    public bool Contains(Vector2 worldPoint) => Find(worldPoint) is not null;

    public ICollider? Overlaps(Vector3 center, float radius, ICollider? ignore = null)
    {
        foreach (ICollider item in Items)
        {
            if (ReferenceEquals(item, ignore))
                continue;
            if (item.Overlap(center, radius))
                return item;
        }

        return null;
    }

    public ICollider? Overlaps(Vector2 center, float radius, ICollider? ignore = null)
        => Overlaps(new Vector3(center, 0f), radius, ignore);

    public ICollider? Raycast(
        Vector3 a,
        Vector3 b,
        out float t,
        ICollider? ignore = null,
        bool ignoreStartInside = false)
    {
        ICollider? nearest = null;
        t = 1f;

        foreach (ICollider item in Items)
        {
            if (ReferenceEquals(item, ignore))
                continue;
            if (ignoreStartInside && item.Overlap(a, 0f))
                continue;
            if (!item.Raycast(a, b, out float hit) || (nearest is not null && hit >= t))
                continue;

            nearest = item;
            t = hit;
        }

        return nearest;
    }

    public ICollider? Raycast(
        Vector2 a,
        Vector2 b,
        out float t,
        ICollider? ignore = null,
        bool ignoreStartInside = false)
        => Raycast(new Vector3(a, 0f), new Vector3(b, 0f), out t, ignore, ignoreStartInside);

    public Vector3 ResolvePenetration(Vector3 center, float radius, ICollider? ignore = null)
    {
        Vector3 resolved = center;
        foreach (ICollider item in Items)
        {
            if (ReferenceEquals(item, ignore))
                continue;
            resolved += item.ResolvePenetration(resolved, radius);
        }

        return resolved;
    }

    public Vector2 ResolvePenetration(Vector2 center, float radius, ICollider? ignore = null)
    {
        Vector3 resolved = ResolvePenetration(new Vector3(center, 0f), radius, ignore);
        return new Vector2(resolved.X, resolved.Y);
    }

    public Vector3 ResolvePenetration(Vector3 center, Vector3 size, ICollider? ignore = null)
    {
        Vector3 half = size * 0.5f;
        Span<Vector3> corners =
        [
            new(-half.X, -half.Y, -half.Z), new(half.X, -half.Y, -half.Z),
            new(-half.X, half.Y, -half.Z), new(half.X, half.Y, -half.Z),
            new(-half.X, -half.Y, half.Z), new(half.X, -half.Y, half.Z),
            new(-half.X, half.Y, half.Z), new(half.X, half.Y, half.Z),
        ];

        Vector3 resolved = center;
        foreach (Vector3 corner in corners)
            resolved = ResolvePenetration(resolved + corner, 0f, ignore) - corner;

        return resolved;
    }

    public Vector2 ResolvePenetration(Vector2 center, Vector2 size, ICollider? ignore = null)
    {
        Vector2 half = size * 0.5f;
        Span<Vector2> corners =
        [
            new(-half.X, -half.Y), new(half.X, -half.Y),
            new(-half.X, half.Y), new(half.X, half.Y),
        ];

        Vector2 resolved = center;
        foreach (Vector2 corner in corners)
            resolved = ResolvePenetration(resolved + corner, 0f, ignore) - corner;

        return resolved;
    }
}
