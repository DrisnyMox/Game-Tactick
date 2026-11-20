/// <summary>Откуда перезарядка берёт патроны. Нет запаса (SurvivorShooter.Reserve == null) — патроны бесконечные</summary>
public interface IAmmoReserve
{
    int Reserve { get; }
    /// <summary>Забрать до count патронов, вернуть сколько забрано</summary>
    int TakeAmmo(int count);
}
