using UnityEngine;

/// <summary>Shared trajectory math; callers retain authority over damage and shot state.</summary>
public static class PvpBallistics
{
    public static Vector3 GetPositionAtTime(Vector3 origin, Vector3 initialVelocity, float time, float gravity, float drag)
    {
        float t = Mathf.Max(0f, time);
        if (drag < 0.0001f) return origin + initialVelocity * t + Vector3.down * (0.5f * gravity * t * t);
        float k = Mathf.Max(drag, 0.0001f);
        float decay = Mathf.Exp(-k * t);
        Vector3 g = Vector3.down * gravity;
        return origin + initialVelocity / k * (1f - decay) + g / k * t + g / (k * k) * (decay - 1f);
    }

    public static bool CastSegment(Vector3 from, Vector3 to, float radius, LayerMask mask,
        Transform ignoredRoot, RaycastHit[] buffer, out RaycastHit closest)
    {
        closest = default;
        Vector3 delta = to - from;
        float length = delta.magnitude;
        if (length <= 0.00001f || buffer == null || buffer.Length == 0) return false;
        int count = Physics.SphereCastNonAlloc(from, Mathf.Max(0.001f, radius), delta / length,
            buffer, length, mask, QueryTriggerInteraction.Ignore);
        bool found = false;
        float nearest = float.PositiveInfinity;
        for (int i = 0; i < count; i++)
        {
            var hit = buffer[i];
            if (hit.collider == null || (ignoredRoot != null && hit.collider.transform.IsChildOf(ignoredRoot))) continue;
            if (hit.distance < nearest) { closest = hit; nearest = hit.distance; found = true; }
        }
        return found;
    }

    public static Vector3 ApplySpray(Vector3 forward, Vector3 right, Vector3 up,
        PvpSprayDefinition pattern, int shotIndex, float magnitude)
    {
        if (pattern == null || pattern.points == null || pattern.points.Length == 0) return forward.normalized;
        int count = pattern.points.Length;
        int expanded = pattern.autoFill ? (count - 1) * 2 + 1 + (pattern.loop ? 1 : 0) : count;
        int index = pattern.loop ? Mathf.Abs(shotIndex) % expanded : Mathf.Clamp(shotIndex, 0, expanded - 1);
        Vector2 point;
        if (pattern.autoFill && count > 1)
        {
            int first = Mathf.Min(index / 2, count - 1);
            point = (index & 1) == 0 ? pattern.points[first] :
                (pattern.points[first] + pattern.points[(first + 1) % count]) * 0.5f;
        }
        else point = pattern.points[Mathf.Min(index, count - 1)];
        // The Demo applies its normalized pattern as an offset scaled by 2 / 180.
        return (forward + (right * point.x + up * point.y) * (pattern.maxAmount * Mathf.Clamp01(magnitude) / 90f)).normalized;
    }

    public static float DamageAtDistance(PvpWeaponDefinition definition, float distance)
    {
        if (definition == null) return 0f;
        return definition.damage * definition.damageOverRange.Evaluate(Mathf.Clamp01(distance / Mathf.Max(1f, definition.range)));
    }
}
