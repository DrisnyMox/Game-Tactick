/// <summary>Рецепт верстака для части снаряжения: материалы со склада, часть — в профиль (Profile.gear)</summary>
public sealed class GearRecipe
{
    public GearKind result;
    public (ItemId item, int count)[] cost;

    // Ткани в игре пока нет: части комплекта Островных Кочевников шьются из того, что есть
    public static readonly GearRecipe[] All =
    {
        new() { result = GearKind.NomadChest, cost = new[] { (ItemId.Wood, 6), (ItemId.Scrap, 4), (ItemId.Tech, 1) } },
        new() { result = GearKind.NomadBoots, cost = new[] { (ItemId.Wood, 4), (ItemId.Scrap, 3) } },
        new() { result = GearKind.NomadPants, cost = new[] { (ItemId.Wood, 5), (ItemId.Scrap, 3) } },
        new() { result = GearKind.NomadGloves, cost = new[] { (ItemId.Wood, 3), (ItemId.Scrap, 2) } },
    };
}
