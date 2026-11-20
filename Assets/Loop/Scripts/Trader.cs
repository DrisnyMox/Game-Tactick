/// <summary>Что торговец продаёт за кредиты</summary>
public static class Trader
{
    public static readonly (ItemId item, int amount, int price)[] Offers =
    {
        (ItemId.Ammo, 30, 12),
        (ItemId.Medkit, 1, 15),
    };
}
