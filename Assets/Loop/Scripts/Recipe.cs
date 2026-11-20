/// <summary>Рецепт верстака: что тратится и что получается</summary>
public sealed class Recipe
{
    public ItemId result;
    public int amount = 1;
    public (ItemId item, int count)[] cost;
    /// <summary>Чертёж, который открывает рецепт; null — рецепт открыт с начала</summary>
    public ItemId? blueprint;

    public static readonly Recipe[] All =
    {
        new() { result = ItemId.Ammo, amount = 30, cost = new[] { (ItemId.Scrap, 3), (ItemId.Stone, 2) } },
        new() { result = ItemId.Medkit, cost = new[] { (ItemId.Wood, 6), (ItemId.Scrap, 1) } },
        new() { result = ItemId.Armor, cost = new[] { (ItemId.Scrap, 10), (ItemId.Wood, 6) }, blueprint = ItemId.ArmorBlueprint },
        new() { result = ItemId.BigMagazine, cost = new[] { (ItemId.Scrap, 5), (ItemId.Tech, 2) }, blueprint = ItemId.BigMagazineBlueprint },
    };
}
