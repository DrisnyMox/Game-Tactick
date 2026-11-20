using System;
using UnityEngine;

/// <summary>Эффект и звуки попадания в поверхность одного вида (настройка SurvivorShooter)</summary>
[Serializable]
public struct SurfaceImpact
{
    [SerializeField] HitSurface surface;
    [Tooltip("Префаб эффекта в точке попадания: живёт impactLifetime секунд")]
    [SerializeField] GameObject effect;
    [Tooltip("Масштаб эффекта: 1 - как в префабе")]
    [SerializeField] float scale;
    [Tooltip("Звук попадания: каждый раз случайный из списка, звучит в точке попадания")]
    [SerializeField] AudioClip[] sounds;
    [Range(0f, 1f)] [SerializeField] float volume;

    public HitSurface Surface => surface;
    public GameObject Effect => effect;
    public float Scale => scale;
    public AudioClip[] Sounds => sounds;
    public float Volume => volume;
}
