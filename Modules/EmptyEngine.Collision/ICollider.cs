using System.Numerics;
using EmptyEngine.ObjectModel;

namespace EmptyEngine.Collision;

/// <summary>Contract for a collision shape in world space.</summary>
public interface ICollider
{
    /// <summary>The object that owns this shape, if it is attached to one.</summary>
    IObject? Owner { get; }

    /// <summary>Returns whether a sphere overlaps this shape.</summary>
    bool Overlap(Vector3 center, float radius);

    /// <summary>Returns the world-space offset that moves a sphere out of this shape.</summary>
    Vector3 ResolvePenetration(Vector3 center, float radius);

    /// <summary>Moves this collider out of the shapes in <paramref name="target"/>.</summary>
    Vector3 ResolvePenetration(ColliderRegistry? target);

    /// <summary>Finds where a segment first enters this shape. <paramref name="t"/> is in [0, 1].</summary>
    bool Raycast(Vector3 a, Vector3 b, out float t);

    /// <summary>Convenience overload for collision queries on the XY plane.</summary>
    bool Overlap(Vector2 center, float radius)
        => Overlap(new Vector3(center, 0f), radius);

    /// <summary>Convenience overload for collision queries on the XY plane.</summary>
    Vector2 ResolvePenetration(Vector2 center, float radius)
    {
        Vector3 offset = ResolvePenetration(new Vector3(center, 0f), radius);
        return new Vector2(offset.X, offset.Y);
    }

    /// <summary>Convenience overload for collision queries on the XY plane.</summary>
    bool Raycast(Vector2 a, Vector2 b, out float t)
        => Raycast(new Vector3(a, 0f), new Vector3(b, 0f), out t);
}
