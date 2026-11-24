/// <summary>Рецепт верстака для части снаряжения: материалы со склада, часть — в профиль (Profile.gear)</summary>
public sealed class GearRecipe
{
    public GearKind result;
    public (ItemId item, int count)[] cost;

    // Ткани в игре пока нет: части шьются из того, что есть. Базовый набор дешевле кочевников: серый у каждого и так
    // есть бесплатно, крафт даёт его в цвете выше серого
    public static readonly GearRecipe[] All =
    {
        new() { result = GearKind.ClothChest, cost = new[] { (ItemId.Wood, 3), (ItemId.Scrap, 2) } },
        new() { result = GearKind.ClothBoots, cost = new[] { (ItemId.Wood, 2), (ItemId.Scrap, 2) } },
        new() { result = GearKind.ClothPantsRope, cost = new[] { (ItemId.Wood, 3), (ItemId.Scrap, 1) } },
        new() { result = GearKind.ClothGloves, cost = new[] { (ItemId.Wood, 2), (ItemId.Scrap, 1) } },
        new() { result = GearKind.NomadChest, cost = new[] { (ItemId.Wood, 6), (ItemId.Scrap, 4), (ItemId.Tech, 1) } },
        new() { result = GearKind.NomadBoots, cost = new[] { (ItemId.Wood, 4), (ItemId.Scrap, 3) } },
        new() { result = GearKind.NomadPants, cost = new[] { (ItemId.Wood, 5), (ItemId.Scrap, 3) } },
        new() { result = GearKind.NomadGloves, cost = new[] { (ItemId.Wood, 3), (ItemId.Scrap, 2) } },
        // Культ Солнца: латунь и солнечные панели старого мира - металлолом и электроника
        new() { result = GearKind.SunPants, cost = new[] { (ItemId.Wood, 4), (ItemId.Scrap, 4), (ItemId.Tech, 2) } },
    };
}
