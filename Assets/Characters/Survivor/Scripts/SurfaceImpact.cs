using System;
using UnityEngine;

/// <summary>Эффект и звуки попадания в поверхность одного вида (настройка SurvivorShooter)</summary>
[Serializable]
public struct SurfaceImpact
{
    public HitSurface surface;
    [Tooltip("Префаб эффекта в точке попадания: живёт impactLifetime секунд")]
    public GameObject effect;
    [Tooltip("Масштаб эффекта: 1 - как в префабе")]
    public float scale;
    [Tooltip("Звук попадания: каждый раз случайный из списка, звучит в точке попадания")]
    public AudioClip[] sounds;
    [Range(0f, 1f)] public float volume;
}
