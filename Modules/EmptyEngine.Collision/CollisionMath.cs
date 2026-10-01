using System.Numerics;

namespace EmptyEngine.Collision;

public static class CollisionMath
{
    public static Vector3 ResolveSphere(Vector3 center, float radius, Vector3 otherCenter, float otherRadius)
    {
        Vector3 diff = center - otherCenter;
        float combined = radius + otherRadius;
        float distSq = diff.LengthSquared();
        if (distSq >= combined * combined)
            return Vector3.Zero;

        float dist = MathF.Sqrt(distSq);
        if (dist <= 0f)
            return new Vector3(0f, combined, 0f);

        return diff * ((combined - dist) / dist);
    }

    public static Vector3 ClosestOnSegment(Vector3 point, Vector3 a, Vector3 b)
    {
        Vector3 ab = b - a;
        float lengthSq = ab.LengthSquared();
        if (lengthSq <= 0f)
            return a;

        float t = Math.Clamp(Vector3.Dot(point - a, ab) / lengthSq, 0f, 1f);
        return a + ab * t;
    }

    public static bool RaycastSphere(Vector3 a, Vector3 b, Vector3 center, float radius, out float t)
    {
        Vector3 toStart = a - center;
        float startDistSq = toStart.LengthSquared();
        float radiusSq = radius * radius;
        if (startDistSq <= radiusSq)
        {
            t = 0f;
            return true;
        }

        t = 1f;
        Vector3 direction = b - a;
        float directionSq = direction.LengthSquared();
        if (directionSq <= 0f)
            return false;

        float projection = Vector3.Dot(toStart, direction);
        float discriminant = projection * projection - directionSq * (startDistSq - radiusSq);
        if (discriminant < 0f)
            return false;

        float hit = (-projection - MathF.Sqrt(discriminant)) / directionSq;
        if (hit < 0f || hit > 1f)
            return false;

        t = hit;
        return true;
    }

    public static bool RaycastBox(
        Vector3 a,
        Vector3 b,
        Vector3 center,
        Vector3 unitX,
        Vector3 unitY,
        Vector3 unitZ,
        float halfX,
        float halfY,
        float halfZ,
        out float t)
    {
        t = 1f;
        if (halfX <= 0f || halfY <= 0f || halfZ <= 0f)
            return false;

        Vector3 delta = a - center;
        Vector3 direction = b - a;
        Vector3 localStart = new(
            Vector3.Dot(delta, unitX),
            Vector3.Dot(delta, unitY),
            Vector3.Dot(delta, unitZ));
        Vector3 localDirection = new(
            Vector3.Dot(direction, unitX),
            Vector3.Dot(direction, unitY),
            Vector3.Dot(direction, unitZ));

        float enter = 0f;
        float exit = 1f;
        if (!ClipSlab(localStart.X, localDirection.X, halfX, ref enter, ref exit)
            || !ClipSlab(localStart.Y, localDirection.Y, halfY, ref enter, ref exit)
            || !ClipSlab(localStart.Z, localDirection.Z, halfZ, ref enter, ref exit))
        {
            return false;
        }

        t = enter;
        return true;
    }

    public static bool RaycastCapsule(
        Vector3 a, Vector3 b, Vector3 capsuleA, Vector3 capsuleB, float radius, out float t)
    {
        Vector3 closest = ClosestOnSegment(a, capsuleA, capsuleB);
        if (Vector3.DistanceSquared(a, closest) <= radius * radius)
        {
            t = 0f;
            return true;
        }

        t = 1f;
        bool hit = false;
        Vector3 direction = b - a;
        Vector3 axis = capsuleB - capsuleA;
        float axisSq = axis.LengthSquared();
        float directionSq = direction.LengthSquared();
        if (directionSq <= 0f)
            return false;

        if (axisSq > 0f)
        {
            Vector3 origin = a - capsuleA;
            float axisDirection = Vector3.Dot(axis, direction);
            float axisOrigin = Vector3.Dot(axis, origin);
            float directionOrigin = Vector3.Dot(direction, origin);
            float originSq = origin.LengthSquared();
            float qa = axisSq * directionSq - axisDirection * axisDirection;
            float qb = axisSq * directionOrigin - axisOrigin * axisDirection;
            float qc = axisSq * originSq - axisOrigin * axisOrigin - radius * radius * axisSq;
            float discriminant = qb * qb - qa * qc;

            if (MathF.Abs(qa) > 1e-8f && discriminant >= 0f)
            {
                float body = (-qb - MathF.Sqrt(discriminant)) / qa;
                float axisHit = axisOrigin + body * axisDirection;
                if (body >= 0f && body <= 1f && axisHit >= 0f && axisHit <= axisSq)
                {
                    t = body;
                    hit = true;
                }
            }
        }

        if (RaycastSphere(a, b, capsuleA, radius, out float firstCap) && (!hit || firstCap < t))
        {
            t = firstCap;
            hit = true;
        }

        if (RaycastSphere(a, b, capsuleB, radius, out float secondCap) && (!hit || secondCap < t))
        {
            t = secondCap;
            hit = true;
        }

        return hit;
    }

    private static bool ClipSlab(float start, float direction, float half, ref float enter, ref float exit)
    {
        if (MathF.Abs(direction) <= 1e-8f)
            return MathF.Abs(start) <= half;

        float t1 = (-half - start) / direction;
        float t2 = (half - start) / direction;
        if (t1 > t2)
            (t1, t2) = (t2, t1);

        enter = MathF.Max(enter, t1);
        exit = MathF.Min(exit, t2);
        return enter <= exit;
    }
}
