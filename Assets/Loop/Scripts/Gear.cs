using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Свойства частей снаряжения: имя на экране, слот, модель на игроке (дочерний объект с этим именем, его ставит
/// Tools/Unity/SetupGear.cs), комплект, описание. Базовый набор есть у каждого, при уничтожении не теряется.
/// Цвет качества (GearQuality) — у каждой части свой: базовый набор серый, скрафченное получает случайный цвет
/// не ниже белого (RollCraftQuality). Цвет красит светлую полосу части на игроке (Dress).
/// </summary>
public static class Gear
{
    public const int SlotCount = 5;
    public const int QualityCount = 6;

    struct Info
    {
        public string name;
        public GearSlot slot;
        public bool basic;
        public string set;
        public string about;
    }

    static readonly Info[] info =
    {
        new() { name = "Тканевый нагрудник", slot = GearSlot.Chest, basic = true, set = "Базовый набор",
            about = "Плотная ткань со шнуровкой по бокам и ремнями на плечах." },
        new() { name = "Тканевые перчатки", slot = GearSlot.Hands, basic = true, set = "Базовый набор",
            about = "Обмотки на кисти и предплечья с серой манжетой. Пальцы открыты: спуск чувствуется." },
        new() { name = "Штаны с верёвочным поясом", slot = GearSlot.Legs, basic = true, set = "Базовый набор",
            about = "Серые ремни на бёдрах и заплатка на колене." },
        new() { name = "Тканевые ботинки", slot = GearSlot.Feet, basic = true, set = "Базовый набор",
            about = "Шнуровка и два серых ремня." },
        new() { name = "Туника кочевника", slot = GearSlot.Chest, set = "Островные Кочевники",
            about = "Выгоревший лён без рукавов, бирюзовая накидка на плечах, шарф и пояс-обмотка с тремя сумками." },
        new() { name = "Ботинки кочевника", slot = GearSlot.Feet, set = "Островные Кочевники",
            about = "Высокое холщовое голенище, плетёная верёвочная подошва и бирюзовая обмотка." },
        new() { name = "Штаны кочевника", slot = GearSlot.Legs, set = "Островные Кочевники",
            about = "Льняные шорты до колена с подвёрнутой манжетой, пояс-кушак и бирюзовые обмотки крест-накрест на голенях." },
        new() { name = "Перчатки кочевника", slot = GearSlot.Hands, set = "Островные Кочевники",
            about = "Кожаные перчатки без пальцев, серые обмотки крест-накрест на запястье и светлая манжета на предплечье." },
    };

    public static readonly GearKind[] All = (GearKind[])System.Enum.GetValues(typeof(GearKind));

    public static string Name(GearKind kind) => info[(int)kind].name;
    public static GearSlot Slot(GearKind kind) => info[(int)kind].slot;
    /// <summary>Базовый набор: есть всегда, в вылазке не теряется</summary>
    public static bool IsBasic(GearKind kind) => info[(int)kind].basic;
    public static string Set(GearKind kind) => info[(int)kind].set;
    public static string About(GearKind kind) => info[(int)kind].about;
    /// <summary>Имя дочернего объекта игрока с моделью части — оно же имя FBX в Assets/Models/Gear</summary>
    public static string Model(GearKind kind) => kind.ToString();

    public static string QualityName(GearQuality quality) => quality switch
    {
        GearQuality.Gray => "Серое",
        GearQuality.White => "Белое",
        GearQuality.Green => "Зелёное",
        GearQuality.Blue => "Синее",
        GearQuality.Purple => "Фиолетовое",
        _ => "Оранжевое",
    };

    /// <summary>Цвет качества в окне и на части (sRGB)</summary>
    public static Color QualityColor(GearQuality quality) => quality switch
    {
        GearQuality.Gray => new Color(0.58f, 0.6f, 0.62f),
        GearQuality.White => new Color(0.93f, 0.94f, 0.95f),
        GearQuality.Green => new Color(0.36f, 0.8f, 0.36f),
        GearQuality.Blue => new Color(0.27f, 0.56f, 0.98f),
        GearQuality.Purple => new Color(0.67f, 0.4f, 0.95f),
        _ => new Color(1f, 0.58f, 0.16f),
    };

    /// <summary>Какого цвета бывает часть: базовый набор — только серый, остальное — от белого и выше</summary>
    public static bool HasQuality(GearKind kind, GearQuality quality) =>
        IsBasic(kind) ? quality == GearQuality.Gray : quality != GearQuality.Gray;

    // Шанс цвета при крафте, по порядку от белого. Опыт крафта и уровень ремесла его позже поднимут (README, шаг 5)
    static readonly (GearQuality quality, float chance)[] craftChances =
    {
        (GearQuality.White, 0.60f),
        (GearQuality.Green, 0.25f),
        (GearQuality.Blue, 0.10f),
        (GearQuality.Purple, 0.04f),
        (GearQuality.Orange, 0.01f),
    };

    public static GearQuality RollCraftQuality()
    {
        float roll = Random.value;
        foreach (var (quality, chance) in craftChances)
        {
            if (roll < chance)
                return quality;
            roll -= chance;
        }
        return GearQuality.White;
    }

    /// <summary>Имя части с цветом качества — для итога вылазки и строки риска</summary>
    public static string FullName(GearPiece piece) => $"{Name(piece.kind)} ({QualityName(piece.quality).ToLower()})";

    public static string SlotName(GearSlot slot) => slot switch
    {
        GearSlot.Head => "Шлем",
        GearSlot.Chest => "Нагрудник",
        GearSlot.Hands => "Перчатки",
        GearSlot.Legs => "Штаны",
        _ => "Ботинки",
    };

    /// <summary>Показать на игроке эти части в их цвете качества, остальные снять</summary>
    public static void Dress(WornGear worn, IEnumerable<GearPiece> pieces)
    {
        var colors = new Dictionary<string, Color>();
        foreach (var piece in pieces)
            colors.Add(Model(piece.kind), QualityColor(piece.quality));
        worn.Show(colors);
    }
}
