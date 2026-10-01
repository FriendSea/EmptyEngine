using System.Numerics;

namespace EmptyEngine.Collision;

/// <summary>A capsule collider whose spine follows the transform's local Y axis.</summary>
public sealed class CapsuleCollider : ColliderComponent
{
    /// <summary>Capsule radius in world units.</summary>
    public float Radius { get; set; } = 0.2f;

    /// <summary>Local Y coordinate of the top of the spine.</summary>
    public float Top { get; set; } = 0.25f;

    /// <summary>Local Y coordinate of the bottom of the spine.</summary>
    public float Bottom { get; set; } = -0.25f;

    private Vector3 _bottom;
    private Vector3 _top;

    protected override void SyncShape(Matrix4x4 worldTransform)
    {
        _bottom = Vector3.Transform(new Vector3(0f, Bottom, 0f), worldTransform);
        _top = Vector3.Transform(new Vector3(0f, Top, 0f), worldTransform);
    }

    private (Vector3 A, Vector3 B) Segment()
    {
        EnsureSynced();
        return (_bottom, _top);
    }

    public override bool Overlap(Vector3 center, float radius)
    {
        (Vector3 a, Vector3 b) = Segment();
        Vector3 closest = CollisionMath.ClosestOnSegment(center, a, b);
        float combined = Radius + radius;
        return Vector3.DistanceSquared(closest, center) <= combined * combined;
    }

    public override Vector3 ResolvePenetration(Vector3 center, float radius)
    {
        (Vector3 a, Vector3 b) = Segment();
        Vector3 closest = CollisionMath.ClosestOnSegment(center, a, b);
        return CollisionMath.ResolveSphere(center, radius, closest, Radius);
    }

    public override bool Raycast(Vector3 a, Vector3 b, out float t)
    {
        (Vector3 bottom, Vector3 top) = Segment();
        return CollisionMath.RaycastCapsule(a, b, bottom, top, Radius, out t);
    }

    protected override Vector3 PenetrationOffset(ColliderRegistry target)
    {
        Vector3 start = Position;
        (Vector3 bottom, Vector3 top) = Segment();
        Vector3 resolved = start;

        foreach (Vector3 endpoint in stackalloc[] { bottom, top })
        {
            Vector3 sphere = resolved + endpoint - start;
            resolved += target.ResolvePenetration(sphere, Radius, ignore: this) - sphere;
        }

        return resolved - start;
    }
}
