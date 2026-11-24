/// <summary>Рецепт верстака: что тратится и что получается</summary>
public sealed class Recipe
{
    public ItemId result;
    public int amount = 1;
    public (ItemId item, int count)[] cost;
    /// <summary>Чертёж, который открывает рецепт; null — рецепт открыт с начала</summary>
    public ItemId? blueprint;
    /// <summary>Описание в окне верстака</summary>
    public string about;

    public static readonly Recipe[] All =
    {
        new() { result = ItemId.Ammo, amount = 30, cost = new[] { (ItemId.Scrap, 3), (ItemId.Stone, 2) },
            about = "Самодельные патроны к винтовке из металлолома. Магазин — 30 штук, перезарядка берёт их из рюкзака." },
        new() { result = ItemId.Medkit, cost = new[] { (ItemId.Wood, 6), (ItemId.Scrap, 1) },
            about = "Бинты и шина в жестяной коробке. На вылазке лечит по H." },
        new() { result = ItemId.Grenade, amount = 2, cost = new[] { (ItemId.Scrap, 4), (ItemId.Stone, 2) },
            about = "Корпус из обрезка трубы. Бросок по G, взрыв через 3 секунды выбивает воронку." },
        new() { result = ItemId.Armor, cost = new[] { (ItemId.Scrap, 10), (ItemId.Wood, 6) }, blueprint = ItemId.ArmorBlueprint,
            about = "Пластины из металлолома на ремнях: +50 к здоровью на вылазке." },
        new() { result = ItemId.BigMagazine, cost = new[] { (ItemId.Scrap, 5), (ItemId.Tech, 2) }, blueprint = ItemId.BigMagazineBlueprint,
            about = "Сварной магазин на 45 патронов вместо 30." },
    };
}
