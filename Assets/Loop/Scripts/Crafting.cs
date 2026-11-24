using UnityEngine;

/// <summary>
/// Крафт на верстаке базы: рецепты тратят материалы со склада. Снаряжение (GearRecipe) получает цвет качества по
/// уровню своего комплекта (GearSet): на каждом уровне выпадает его обычный цвет или, с шансом уровня, следующий.
/// Когда шанс следующего цвета вырос, комплект переходит на уровень, где этот цвет — обычный, а прежний уже не
/// выпадает (решено 23.11.2026). Опыт комплекта — по единице за сделанную часть. Переход от серого к белому быстрый,
/// чтобы прогресс был виден сразу, дальше медленнее. Часть, вынесенная с вылазки, цветом выше обычного цвета
/// комплекта даёт ему опыт или сразу уровень (Learn). Уровень уничтожение не отнимает, а сила остаётся в снаряжении, которое
/// можно потерять (README, «Баланс»). Припасы (Recipe) ложатся на склад, опыта не дают. Окно — WorkbenchScreen.
/// </summary>
public static class Crafting
{
    /// <summary>Уровень базового набора в новом профиле: его крафт сразу даёт белое</summary>
    public const int BasicStartLevel = 3;

    // Уровень комплекта: обычный цвет, шанс следующего цвета и сколько частей сделать до следующего уровня
    struct Step
    {
        public GearQuality usual;
        public float next;
        public int parts;

        public Step(GearQuality usual, float next, int parts)
        {
            this.usual = usual;
            this.next = next;
            this.parts = parts;
        }
    }

    static readonly Step[] steps =
    {
        // От серого к белому — по уровню за часть: после 2–3 частей белое уже выпадает чаще серого
        new(GearQuality.Gray, 0.10f, 1),
        new(GearQuality.Gray, 0.40f, 1),
        new(GearQuality.Gray, 0.75f, 1),
        new(GearQuality.White, 0.05f, 2),
        new(GearQuality.White, 0.15f, 3),
        new(GearQuality.White, 0.30f, 3),
        new(GearQuality.White, 0.50f, 4),
        new(GearQuality.Green, 0.05f, 4),
        new(GearQuality.Green, 0.15f, 5),
        new(GearQuality.Green, 0.30f, 5),
        new(GearQuality.Green, 0.50f, 6),
        new(GearQuality.Blue, 0.05f, 6),
        new(GearQuality.Blue, 0.15f, 7),
        new(GearQuality.Blue, 0.30f, 8),
        new(GearQuality.Blue, 0.50f, 9),
        new(GearQuality.Purple, 0.05f, 10),
        new(GearQuality.Purple, 0.15f, 12),
        new(GearQuality.Purple, 0.30f, 14),
        new(GearQuality.Purple, 0.50f, 0),
    };

    public static int MaxLevel => steps.Length - 1;

    /// <summary>Сколько всего опыта (сделанных частей) нужно комплекту для уровня level; уровень 0 — с начала</summary>
    public static int XpForLevel(int level)
    {
        int xp = 0;
        for (int i = 0; i < level; i++)
            xp += steps[i].parts;
        return xp;
    }

    public static int Xp(GearSet set) => Profile.Current.setXp[(int)set];

    public static int Level(GearSet set)
    {
        int level = 0;
        while (level < MaxLevel && Xp(set) >= XpForLevel(level + 1))
            level++;
        return level;
    }

    /// <summary>Цвет, который на этом уровне выпадает обычно</summary>
    public static GearQuality Usual(int level) => steps[level].usual;

    /// <summary>Шанс следующего за обычным цвета на этом уровне</summary>
    public static float NextChance(int level) => steps[level].next;

    public static float Chance(GearKind kind, GearQuality quality)
    {
        int level = Level(Gear.Set(kind));
        var usual = Usual(level);
        return quality == usual ? 1f - NextChance(level) : quality == usual + 1 ? NextChance(level) : 0f;
    }

    public static bool HasCost((ItemId item, int count)[] cost)
    {
        foreach (var (item, count) in cost)
            if (Profile.Current.stash[item] < count)
                return false;
        return true;
    }

    /// <summary>Сделать часть снаряжения: материалы со склада, часть в профиль, единица опыта её комплекту</summary>
    public static GearPiece Craft(GearRecipe recipe)
    {
        var profile = Profile.Current;
        int level = Level(Gear.Set(recipe.result));
        var quality = Random.value < NextChance(level) ? Usual(level) + 1 : Usual(level);
        Spend(recipe.cost);
        var piece = new GearPiece { kind = recipe.result, quality = quality };
        profile.gear.Add(piece);
        profile.setXp[(int)Gear.Set(recipe.result)]++;
        Profile.Save();
        return piece;
    }

    /// <summary>Сделать припасы: материалы со склада, результат на склад</summary>
    public static void Craft(Recipe recipe)
    {
        var profile = Profile.Current;
        Spend(recipe.cost);
        profile.stash[recipe.result] += recipe.amount;
        Profile.Save();
    }

    /// <summary>
    /// Часть вынесена с вылазки. Цвет, который на уровне комплекта выпадает только с шансом, даёт комплекту половину
    /// опыта этого уровня (не меньше одной части). Цвет, который ещё не выпадает, сразу поднимает комплект до первого
    /// уровня, где он начинает выпадать. Обычный цвет и ниже опыта не дают. Сохраняет вызывающий
    /// </summary>
    public static void Learn(GearPiece piece)
    {
        var set = Gear.Set(piece.kind);
        int current = Level(set);
        var usual = Usual(current);
        if (piece.quality <= usual || current == MaxLevel)
            return;
        ref int xp = ref Profile.Current.setXp[(int)set];
        if (piece.quality == usual + 1)
        {
            xp += Mathf.Max(1, (steps[current].parts + 1) / 2);
            return;
        }
        int level = current + 1;
        while (Usual(level) + 1 < piece.quality)
            level++;
        xp = Mathf.Max(xp, XpForLevel(level));
    }

    static void Spend((ItemId item, int count)[] cost)
    {
        foreach (var (item, count) in cost)
            Profile.Current.stash[item] -= count;
    }
}
