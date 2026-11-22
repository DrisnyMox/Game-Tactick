using UnityEngine;

/// <summary>IK на две кости: кисти стрелка на рукояти и цевье винтовки (SurvivorShooter)</summary>
public static class TwoBoneIK
{
    /// <summary>
    /// Кисть тянется к цели, локоть сгибается в ту же сторону, что в анимации.
    /// Поворот кисти остаётся из анимации.
    /// </summary>
    public static void Solve(Transform upper, Transform lower, Transform end, Vector3 target, float weight)
    {
        Vector3 a = upper.position, b = lower.position, c = end.position;
        Vector3 t = Vector3.Lerp(c, target, weight);
        float lab = (b - a).magnitude, lcb = (c - b).magnitude;
        float lat = Mathf.Clamp((t - a).magnitude, 0.001f, lab + lcb - 0.001f);

        float acAb0 = Angle(c - a, b - a);
        float baBc0 = Angle(a - b, c - b);
        float acAb1 = Mathf.Acos(Mathf.Clamp((lcb * lcb - lab * lab - lat * lat) / (-2f * lab * lat), -1f, 1f));
        float baBc1 = Mathf.Acos(Mathf.Clamp((lat * lat - lab * lab - lcb * lcb) / (-2f * lab * lcb), -1f, 1f));

        var bend = Vector3.Cross(c - a, b - a);
        if (bend.sqrMagnitude < 1e-8f)
            bend = Vector3.Cross(c - a, upper.up);
        bend.Normalize();

        lower.rotation = Quaternion.AngleAxis((baBc1 - baBc0) * Mathf.Rad2Deg, bend) * lower.rotation;
        upper.rotation = Quaternion.AngleAxis((acAb1 - acAb0) * Mathf.Rad2Deg, bend) * upper.rotation;

        // Теперь длина от плеча до кисти верная, осталось довернуть руку на цель
        c = end.position;
        upper.rotation = Quaternion.FromToRotation(c - a, t - a) * upper.rotation;
    }

    static float Angle(Vector3 x, Vector3 y)
    {
        return Mathf.Acos(Mathf.Clamp(Vector3.Dot(x.normalized, y.normalized), -1f, 1f));
    }
}
