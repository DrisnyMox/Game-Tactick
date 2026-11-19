using System;
using UnityEngine;

/// <summary>
/// Сохранённый прогресс игрока: склад, рюкзак на базе, кредиты, открытые чертежи, счёт вылазок.
/// Хранится в PlayerPrefs одной строкой JSON (в WebGL PlayerPrefs лежат в IndexedDB браузера).
///
/// Цикл: на базе снаряжение лежит в Carried (рюкзак). В начале вылазки RaidDirector забирает Carried в рюкзак
/// персонажа и сразу сохраняет пустой Carried: закрыл игру посреди вылазки — снаряжение потеряно.
/// После эвакуации всё из рюкзака уходит на склад, чертежи открывают рецепты.
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
    }

    /// <summary>Чем кончилась последняя вылазка: показывается на базе один раз</summary>
    public sealed class RaidResult
    {
        public bool extracted;
        public ItemCounts items = new();
        public string[] unlocked = Array.Empty<string>();
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
        return loaded;
    }

    // Первая вылазка: патроны на три магазина и аптечка, немного кредитов на патроны у торговца
    static Data NewProfile()
    {
        var fresh = new Data { credits = 30 };
        fresh.carried[ItemId.Ammo] = 90;
        fresh.carried[ItemId.Medkit] = 1;
        return fresh;
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
    /// оно возвращается в рюкзак базы, чтобы не собирать его заново перед каждой вылазкой
    /// </summary>
    public static void Extract(ItemCounts brought)
    {
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

    /// <summary>Персонаж уничтожен: всё, что было в рюкзаке, потеряно</summary>
    public static void Lose(ItemCounts lost)
    {
        Save();
        LastRaid = new RaidResult { extracted = false };
        LastRaid.items.CopyFrom(lost);
    }
}
