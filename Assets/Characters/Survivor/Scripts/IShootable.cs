using UnityEngine;

/// <summary>
/// То, что по-своему отвечает на попадание пули: например, воксельная арена выбивает воксели,
/// а персонаж теряет здоровье. SurvivorShooter ищет его на объекте попадания и выше по иерархии.
/// </summary>
public interface IShootable
{
    /// <summary>
    /// Во что попала пуля в этой точке (для эффекта и звука). SurvivorShooter спрашивает до OnShot:
    /// после него вокселя в точке уже может не быть.
    /// </summary>
    HitSurface SurfaceAt(RaycastHit hit);

    /// <summary>
    /// Попадание пули с уроном damage от стрелка attacker. Вернуть true, если на месте попадания нужен след от пули.
    /// </summary>
    bool OnShot(RaycastHit hit, Vector3 direction, float damage, CharacterHealth attacker);
}
