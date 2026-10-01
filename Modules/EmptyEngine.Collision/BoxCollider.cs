using System.Numerics;

namespace EmptyEngine.Collision;

/// <summary>An oriented box collider.</summary>
/// <remarks>Translation, rotation, and per-axis scale from the transform are applied.</remarks>
public sealed class BoxCollider : ColliderComponent
{
    /// <summary>Box width, height, and depth in local space.</summary>
    public Vector3 Size { get; set; } = Vector3.One;

    /// <summary>Center offset in local space.</summary>
    public Vector3 Offset { get; set; }

    private readonly record struct BoxFrame(
        Vector3 Center,
        Vector3 UnitX,
        Vector3 UnitY,
        Vector3 UnitZ,
        float HalfX,
        float HalfY,
        float HalfZ)
    {
        public bool IsValid => HalfX > 0f && HalfY > 0f && HalfZ > 0f;

        public Vector3 ToLocal(Vector3 point)
        {
            Vector3 delta = point - Center;
            return new Vector3(
                Vector3.Dot(delta, UnitX),
                Vector3.Dot(delta, UnitY),
                Vector3.Dot(delta, UnitZ));
        }
    }

    private BoxFrame _frame;

    protected override void SyncShape(Matrix4x4 worldTransform)
    {
        Vector3 worldCenter = Vector3.Transform(Offset, worldTransform);
        Vector3 axisX = Vector3.Transform(Offset + new Vector3(Size.X * 0.5f, 0f, 0f), worldTransform) - worldCenter;
        Vector3 axisY = Vector3.Transform(Offset + new Vector3(0f, Size.Y * 0.5f, 0f), worldTransform) - worldCenter;
        Vector3 axisZ = Vector3.Transform(Offset + new Vector3(0f, 0f, Size.Z * 0.5f), worldTransform) - worldCenter;
        float halfX = axisX.Length();
        float halfY = axisY.Length();
        float halfZ = axisZ.Length();

        _frame = new BoxFrame(
            worldCenter,
            halfX > 0f ? axisX / halfX : Vector3.Zero,
            halfY > 0f ? axisY / halfY : Vector3.Zero,
            halfZ > 0f ? axisZ / halfZ : Vector3.Zero,
            halfX,
            halfY,
            halfZ);
    }

    /// <summary>Returns the shape snapshot captured by the most recent registry sync.</summary>
    private BoxFrame Frame()
    {
        EnsureSynced();
        return _frame;
    }

    public override bool Overlap(Vector3 center, float radius)
    {
        BoxFrame frame = Frame();
        if (!frame.IsValid)
            return false;

        Vector3 local = frame.ToLocal(center);
        Vector3 closest = Vector3.Clamp(
            local,
            new Vector3(-frame.HalfX, -frame.HalfY, -frame.HalfZ),
            new Vector3(frame.HalfX, frame.HalfY, frame.HalfZ));
        return Vector3.DistanceSquared(local, closest) <= radius * radius;
    }

    public override Vector3 ResolvePenetration(Vector3 center, float radius)
    {
        BoxFrame frame = Frame();
        if (!frame.IsValid)
            return Vector3.Zero;

        Vector3 local = frame.ToLocal(center);
        bool inside = MathF.Abs(local.X) <= frame.HalfX
                      && MathF.Abs(local.Y) <= frame.HalfY
                      && MathF.Abs(local.Z) <= frame.HalfZ;

        Vector3 localOffset;
        if (inside)
        {
            float x = NearestExit(local.X, frame.HalfX, radius);
            float y = NearestExit(local.Y, frame.HalfY, radius);
            float z = NearestExit(local.Z, frame.HalfZ, radius);
            localOffset = MathF.Abs(x) <= MathF.Abs(y) && MathF.Abs(x) <= MathF.Abs(z)
                ? new Vector3(x, 0f, 0f)
                : MathF.Abs(y) <= MathF.Abs(z)
                    ? new Vector3(0f, y, 0f)
                    : new Vector3(0f, 0f, z);
        }
        else
        {
            Vector3 closest = Vector3.Clamp(
                local,
                new Vector3(-frame.HalfX, -frame.HalfY, -frame.HalfZ),
                new Vector3(frame.HalfX, frame.HalfY, frame.HalfZ));
            Vector3 diff = local - closest;
            float distSq = diff.LengthSquared();
            if (distSq >= radius * radius || distSq <= 0f)
                return Vector3.Zero;

            float dist = MathF.Sqrt(distSq);
            localOffset = diff * ((radius - dist) / dist);
        }

        return localOffset.X * frame.UnitX + localOffset.Y * frame.UnitY + localOffset.Z * frame.UnitZ;
    }

    public override bool Raycast(Vector3 a, Vector3 b, out float t)
    {
        BoxFrame frame = Frame();
        return CollisionMath.RaycastBox(
            a, b, frame.Center, frame.UnitX, frame.UnitY, frame.UnitZ,
            frame.HalfX, frame.HalfY, frame.HalfZ, out t);
    }

    protected override Vector3 PenetrationOffset(ColliderRegistry target)
    {
        BoxFrame frame = Frame();
        if (!frame.IsValid)
            return Vector3.Zero;

        Vector3 x = frame.UnitX * frame.HalfX;
        Vector3 y = frame.UnitY * frame.HalfY;
        Vector3 z = frame.UnitZ * frame.HalfZ;
        Span<Vector3> corners =
        [
            -x - y - z, x - y - z, -x + y - z, x + y - z,
            -x - y + z, x - y + z, -x + y + z, x + y + z,
        ];

        Vector3 resolved = frame.Center;
        foreach (Vector3 corner in corners)
            resolved = target.ResolvePenetration(resolved + corner, 0f, ignore: this) - corner;

        return resolved - frame.Center;
    }

    private static float NearestExit(float coordinate, float half, float radius)
    {
        float positive = half + radius - coordinate;
        float negative = -half - radius - coordinate;
        return MathF.Abs(negative) < MathF.Abs(positive) ? negative : positive;
    }
}
