using System.Numerics;

namespace EmptyEngine.Collision;

/// <summary>A sphere collider.</summary>
public sealed class SphereCollider : ColliderComponent
{
    /// <summary>Sphere radius in world units.</summary>
    public float Radius { get; set; } = 0.5f;

    /// <summary>Center offset in local space.</summary>
    public Vector3 Offset { get; set; }

    private Vector3 _center;

    protected override void SyncShape(Matrix4x4 worldTransform)
        => _center = Vector3.Transform(Offset, worldTransform);

    private Vector3 Center
    {
        get
        {
            EnsureSynced();
            return _center;
        }
    }

    public override bool Overlap(Vector3 center, float radius)
    {
        float combined = Radius + radius;
        return Vector3.DistanceSquared(Center, center) <= combined * combined;
    }

    public override Vector3 ResolvePenetration(Vector3 center, float radius)
        => CollisionMath.ResolveSphere(center, radius, Center, Radius);

    public override bool Raycast(Vector3 a, Vector3 b, out float t)
        => CollisionMath.RaycastSphere(a, b, Center, Radius, out t);

    protected override Vector3 PenetrationOffset(ColliderRegistry target)
    {
        Vector3 center = Center;
        return target.ResolvePenetration(center, Radius, ignore: this) - center;
    }
}
