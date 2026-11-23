using System;
using UnityEngine;

/// <summary>
/// Сохранённый прогресс игрока: склад, рюкзак на базе, кредиты, открытые чертежи, счёт вылазок.
/// Хранится в PlayerPrefs одной строкой JSON (в WebGL PlayerPrefs лежат в IndexedDB браузера).
///
/// Цикл: на базе снаряжение лежит в Carried (рюкзак). В начале вылазки RaidDirector забирает Carried в рюкзак
/// персонажа и сразу сохраняет пустой Carried: закрыл игру посреди вылазки — снаряжение потеряно.
/// После эвакуации всё из рюкзака уходит на склад, чертежи открывают рецепты.
///
/// Снаряжение — записи в gear (GearPiece). Надетое не из базового набора в начале вылазки так же уходит из профиля
/// (TakeWornForRaid) и возвращается надетым после эвакуации. Базовый набор есть всегда и не теряется.
/// </summary>
public static class Profile
{
    const string Key = "Profile.v1";

    [Serializable]
    public sealed class Data
    {
        public int credits;
        public ItemCounts stash = new();
        public ItemCounts carried = new();
        // Открытые чертежи: по индексу ItemId чертежа
        public bool[] blueprints = new bool[Items.Count];
        public int raids;
        public int extractions;
        public System.Collections.Generic.List<GearPiece> gear = new();
    }

    /// <summary>Чем кончилась последняя вылазка: показывается на базе один раз</summary>
    public sealed class RaidResult
    {
        public bool extracted;
        public ItemCounts items = new();
        // Уничтожен: что сохранил защищённый карман
        public ItemCounts kept = new();
        public string[] unlocked = Array.Empty<string>();
        // Уничтожен: надетые части, которые остались на вылазке
        public string[] lostGear = Array.Empty<string>();
    }

    static Data data;

    public static Data Current => data ??= Load();

    /// <summary>Итог вылазки для экрана базы; база показывает его и обнуляет</summary>
    public static RaidResult LastRaid;

    // Play Mode входит без перезагрузки домена: прочитанный в прошлом запуске профиль сбрасывается
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        data = null;
        LastRaid = null;
    }

    static Data Load()
    {
        var json = PlayerPrefs.GetString(Key, "");
        if (string.IsNullOrEmpty(json))
            return NewProfile();
        var loaded = JsonUtility.FromJson<Data>(json);
        loaded.stash.Fit();
        loaded.carried.Fit();
        if (loaded.blueprints.Length != Items.Count)
            Array.Resize(ref loaded.blueprints, Items.Count);
        // Сохранение до снаряжения: списка нет вовсе
        loaded.gear ??= new();
        // Сохранение до цвета качества: у всех частей серое, а скрафченные тогда были белыми
        foreach (var piece in loaded.gear)
            if (!Gear.IsBasic(piece.kind) && piece.quality == GearQuality.Gray)
                piece.quality = GearQuality.White;
        GiveBasicSet(loaded);
        return loaded;
    }

    // Первая вылазка: патроны на три магазина и аптечка, немного кредитов на патроны у торговца, надет базовый набор
    static Data NewProfile()
    {
        var fresh = new Data { credits = 30 };
        fresh.carried[ItemId.Ammo] = 90;
        fresh.carried[ItemId.Medkit] = 1;
        GiveBasicSet(fresh);
        return fresh;
    }

    /// <summary>Каждая часть базового набора есть в профиле одна; в пустой слот она сразу надевается</summary>
    static void GiveBasicSet(Data profile)
    {
        foreach (var kind in Gear.All)
        {
            if (!Gear.IsBasic(kind) || profile.gear.Exists(p => p.kind == kind))
                continue;
            profile.gear.Add(new GearPiece { kind = kind, worn = WornIn(profile, Gear.Slot(kind)) == null });
        }
    }

    static GearPiece WornIn(Data profile, GearSlot slot) => profile.gear.Find(p => p.worn && Gear.Slot(p.kind) == slot);

    /// <summary>Что надето в слоте; null — слот пуст</summary>
    public static GearPiece WornIn(GearSlot slot) => WornIn(Current, slot);

    /// <summary>Надеть часть: то, что было в её слоте, снимается</summary>
    public static void Wear(GearPiece piece)
    {
        var old = WornIn(Gear.Slot(piece.kind));
        if (old != null)
            old.worn = false;
        piece.worn = true;
        Save();
    }

    public static void TakeOff(GearSlot slot)
    {
        var old = WornIn(slot);
        if (old == null)
            return;
        old.worn = false;
        Save();
    }

    /// <summary>Что надето сейчас</summary>
    public static System.Collections.Generic.IEnumerable<GearPiece> Worn()
    {
        foreach (var piece in Current.gear)
            if (piece.worn)
                yield return piece;
    }

    /// <summary>
    /// Начало вылазки: надетое не из базового набора уходит из профиля, сохранение уже без него — как рюкзак
    /// в TakeCarriedForRaid. Базовый набор остаётся в профиле: потерять его нельзя
    /// </summary>
    public static System.Collections.Generic.List<GearPiece> TakeWornForRaid()
    {
        var taken = Current.gear.FindAll(p => p.worn && !Gear.IsBasic(p.kind));
        Current.gear.RemoveAll(taken.Contains);
        Save();
        return taken;
    }

    public static void Save()
    {
        PlayerPrefs.SetString(Key, JsonUtility.ToJson(Current));
        PlayerPrefs.Save();
    }

    /// <summary>Стереть прогресс и начать заново</summary>
    public static void Reset()
    {
        data = NewProfile();
        Save();
    }

    /// <summary>Бесплатный набор: патроны на два магазина и аптечка</summary>
    public static ItemCounts FreeKit
    {
        get
        {
            var kit = new ItemCounts();
            kit[ItemId.Ammo] = 60;
            kit[ItemId.Medkit] = 1;
            return kit;
        }
    }

    /// <summary>Бесплатный набор даётся, только когда патронов нет ни в рюкзаке, ни на складе: иначе его копили бы</summary>
    public static bool CanTakeFreeKit => Current.carried[ItemId.Ammo] == 0 && Current.stash[ItemId.Ammo] == 0;

    public static void TakeFreeKit()
    {
        var kit = FreeKit;
        for (int i = 0; i < Items.Count; i++)
            Current.carried[(ItemId)i] += kit[(ItemId)i];
        Save();
    }

    public static bool HasBlueprint(ItemId blueprint) => Current.blueprints[(int)blueprint];

    public static bool IsUnlocked(Recipe recipe) => recipe.blueprint == null || HasBlueprint(recipe.blueprint.Value);

    /// <summary>Начало вылазки: рюкзак с базы уходит в вылазку, сохранение уже без него</summary>
    public static ItemCounts TakeCarriedForRaid()
    {
        var taken = new ItemCounts();
        taken.CopyFrom(Current.carried);
        Current.carried.Clear();
        Current.raids++;
        Save();
        return taken;
    }

    /// <summary>
    /// Эвакуация: чертежи открывают рецепты, всё остальное ложится на склад, кроме снаряжения —
    /// оно возвращается в рюкзак базы, чтобы не собирать его заново перед каждой вылазкой.
    /// Надетые части (wornGear из TakeWornForRaid) возвращаются надетыми: пока шла вылазка, их слоты были пусты
    /// </summary>
    public static void Extract(ItemCounts brought, System.Collections.Generic.List<GearPiece> wornGear)
    {
        foreach (var piece in wornGear)
        {
            var basic = WornIn(Gear.Slot(piece.kind));
            if (basic != null)
                basic.worn = false;
            piece.worn = true;
            Current.gear.Add(piece);
        }
        var unlocked = new System.Collections.Generic.List<string>();
        for (int i = 0; i < Items.Count; i++)
        {
            var id = (ItemId)i;
            int count = brought[id];
            if (count <= 0)
                continue;
            if (Items.IsBlueprint(id))
            {
                if (!Current.blueprints[i])
                    unlocked.Add(Items.Name(id));
                Current.blueprints[i] = true;
            }
            else if (Items.IsGear(id))
                Current.carried[id] += count;
            else
                Current.stash[id] += count;
        }
        Current.extractions++;
        Save();

        LastRaid = new RaidResult { extracted = true, unlocked = unlocked.ToArray() };
        LastRaid.items.CopyFrom(brought);
    }

    /// <summary>
    /// Персонаж уничтожен: рюкзак потерян, кроме защищённого кармана kept — чертёж из него открывает рецепт,
    /// материалы ложатся на склад. Надетые части wornGear потеряны, в пустые слоты надевается базовый набор
    /// </summary>
    public static void Lose(ItemCounts lost, ItemCounts kept, System.Collections.Generic.List<GearPiece> wornGear)
    {
        foreach (var piece in wornGear)
        {
            var slot = Gear.Slot(piece.kind);
            var basic = Current.gear.Find(p => Gear.IsBasic(p.kind) && Gear.Slot(p.kind) == slot);
            if (basic != null && WornIn(slot) == null)
                basic.worn = true;
        }
        var unlocked = new System.Collections.Generic.List<string>();
        for (int i = 0; i < Items.Count; i++)
        {
            var id = (ItemId)i;
            if (kept[id] <= 0)
                continue;
            if (Items.IsBlueprint(id))
            {
                if (!Current.blueprints[i])
                    unlocked.Add(Items.Name(id));
                Current.blueprints[i] = true;
            }
            else
                Current.stash[id] += kept[id];
        }
        Save();
        LastRaid = new RaidResult
        {
            extracted = false,
            unlocked = unlocked.ToArray(),
            lostGear = wornGear.ConvertAll(Gear.FullName).ToArray(),
        };
        LastRaid.items.CopyFrom(lost);
        LastRaid.kept.CopyFrom(kept);
    }
}
