using UnityEngine;

/// <summary>
/// Свойства предметов: имя на экране, цена у торговца, вес в рюкзаке, какой блок арены даёт материал.
/// Вес есть только у материалов: рюкзак ограничивает их число, снаряжение и чертежи места не занимают.
/// </summary>
public static class Items
{
    public const int Count = 10;

    struct Info
    {
        public string name;
        // Сколько торговец платит за штуку; 0 — не покупает
        public int sellPrice;
        public int weight;
        // Сколько выбитых вокселей дают одну штуку материала
        public int voxelsPerUnit;
        public Color color;
    }

    static readonly Info[] info =
    {
        new() { name = "Дерево", sellPrice = 1, weight = 1, voxelsPerUnit = 6, color = new Color(0.62f, 0.42f, 0.24f) },
        new() { name = "Камень", sellPrice = 1, weight = 1, voxelsPerUnit = 8, color = new Color(0.62f, 0.62f, 0.62f) },
        new() { name = "Металлолом", sellPrice = 3, weight = 1, voxelsPerUnit = 4, color = new Color(0.82f, 0.48f, 0.3f) },
        new() { name = "Электроника", sellPrice = 10, weight = 1, voxelsPerUnit = 3, color = new Color(0.35f, 0.85f, 0.95f) },
        new() { name = "Патроны", sellPrice = 0, color = new Color(0.9f, 0.85f, 0.55f) },
        new() { name = "Аптечка", sellPrice = 5, color = new Color(0.95f, 0.4f, 0.4f) },
        new() { name = "Бронежилет", sellPrice = 20, color = new Color(0.45f, 0.6f, 0.45f) },
        new() { name = "Большой магазин", sellPrice = 20, color = new Color(0.55f, 0.55f, 0.7f) },
        new() { name = "Чертёж: бронежилет", color = new Color(0.4f, 0.7f, 1f) },
        new() { name = "Чертёж: большой магазин", color = new Color(0.4f, 0.7f, 1f) },
    };

    public static string Name(ItemId id) => info[(int)id].name;
    public static int SellPrice(ItemId id) => info[(int)id].sellPrice;
    public static int Weight(ItemId id) => info[(int)id].weight;
    public static Color Tint(ItemId id) => info[(int)id].color;
    public static int VoxelsPerUnit(ItemId id) => info[(int)id].voxelsPerUnit;

    public static bool IsMaterial(ItemId id) => id <= ItemId.Tech;
    public static bool IsBlueprint(ItemId id) => id >= ItemId.ArmorBlueprint;
    /// <summary>Снаряжение: его берут со склада в вылазку</summary>
    public static bool IsGear(ItemId id) => id >= ItemId.Ammo && id <= ItemId.BigMagazine;
    /// <summary>Бронежилет и магазин работают в одном экземпляре: второй в вылазку не берут</summary>
    public static bool IsSingle(ItemId id) => id == ItemId.Armor || id == ItemId.BigMagazine;

    /// <summary>
    /// Какой материал даёт выбитый блок. Камень и бетон — камень, ржавчина и железо — металлолом, электроника —
    /// электроника (руда на арене RTS — тоже). Трава, песок, земля и листья не дают ничего
    /// </summary>
    public static bool FromBlock(byte block, out ItemId item)
    {
        switch (block)
        {
            case VoxelBlocks.Wood:
                item = ItemId.Wood;
                return true;
            case VoxelBlocks.Stone:
            case VoxelBlocks.Wall:
                item = ItemId.Stone;
                return true;
            case VoxelBlocks.Rust:
            case VoxelBlocks.Metal:
            case VoxelBlocks.IronOre:
                item = ItemId.Scrap;
                return true;
            case VoxelBlocks.Tech:
            case VoxelBlocks.GoldOre:
                item = ItemId.Tech;
                return true;
            default:
                item = default;
                return false;
        }
    }
}
