using System;
using UnityEngine;

/// <summary>
/// Шум на вылазке: выстрел слышно далеко, бег — рядом, шаг — не слышно.
/// Рейдер (ShooterBot) слышит шум врага в пределах радиуса и идёт проверить, откуда он.
/// Выстрел шумит из SurvivorShooter, бег игрока — из RaidDirector, взрыв гранаты — из Grenade.
/// </summary>
public static class Noise
{
    /// <summary>Выстрел слышно на столько метров. Добыча вокселей — тоже выстрелы</summary>
    public const float ShotRadius = 60f;
    /// <summary>Бег слышно на столько метров</summary>
    public const float SprintRadius = 14f;
    /// <summary>Взрыв гранаты слышно на столько метров</summary>
    public const float BlastRadius = 90f;

    /// <summary>Шум: где, на сколько метров слышно, кто шумел</summary>
    public static event Action<Vector3, float, CharacterHealth> Made;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => Made = null;

    public static void Make(Vector3 position, float radius, CharacterHealth source) => Made?.Invoke(position, radius, source);
}
