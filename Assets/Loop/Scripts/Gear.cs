using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Свойства частей снаряжения: имя на экране, слот, модель на игроке (дочерний объект с этим именем, его ставит
/// Tools/Unity/SetupGear.cs), комплект, описание. Серый базовый набор есть у каждого бесплатно и при уничтожении
/// не теряется (IsFree). Цвет качества (GearQuality) — у каждой части свой, на верстаке он зависит от уровня
/// комплекта (Crafting); скрафченное, в том числе базовый набор, теряется. Цвет красит светлую полосу части на игроке (Dress).
/// </summary>
public static class Gear
{
    public const int SlotCount = 5;
    public const int QualityCount = 6;
    public const int SetCount = 3;

    struct Info
    {
        public string name;
        public GearSlot slot;
        public bool basic;
        public GearSet set;
        public string about;
    }

    static readonly Info[] info =
    {
        new() { name = "Тканевый нагрудник", slot = GearSlot.Chest, basic = true, set = GearSet.Basic,
            about = "Плотная ткань со шнуровкой по бокам и ремнями на плечах." },
        new() { name = "Тканевые перчатки", slot = GearSlot.Hands, basic = true, set = GearSet.Basic,
            about = "Обмотки на кисти и предплечья с серой манжетой. Пальцы открыты: спуск чувствуется." },
        new() { name = "Штаны с верёвочным поясом", slot = GearSlot.Legs, basic = true, set = GearSet.Basic,
            about = "Серые ремни на бёдрах и заплатка на колене." },
        new() { name = "Тканевые ботинки", slot = GearSlot.Feet, basic = true, set = GearSet.Basic,
            about = "Шнуровка и два серых ремня." },
        new() { name = "Туника кочевника", slot = GearSlot.Chest, set = GearSet.Nomads,
            about = "Выгоревший лён без рукавов, бирюзовая накидка на плечах, шарф и пояс-обмотка с тремя сумками." },
        new() { name = "Ботинки кочевника", slot = GearSlot.Feet, set = GearSet.Nomads,
            about = "Высокое холщовое голенище, плетёная верёвочная подошва и бирюзовая обмотка." },
        new() { name = "Штаны кочевника", slot = GearSlot.Legs, set = GearSet.Nomads,
            about = "Льняные шорты до колена с подвёрнутой манжетой, пояс-кушак и бирюзовые обмотки крест-накрест на голенях." },
        new() { name = "Перчатки кочевника", slot = GearSlot.Hands, set = GearSet.Nomads,
            about = "Кожаные перчатки без пальцев, серые обмотки крест-накрест на запястье и светлая манжета на предплечье." },
        new() { name = "Штаны Культа Солнца", slot = GearSlot.Legs, set = GearSet.SunCult,
            about = "Индиго-чёрная ткань с тканым узором солнц, латунные наколенники и панель на бедре с медными проводами, серый пояс-повязка и обмотки." },
        new() { name = "Нагрудник Культа Солнца", slot = GearSlot.Chest, set = GearSet.SunCult,
            about = "Короткий жилет из индиго-чёрной ткани с узором солнц, латунный ворот, диски-панели на груди и спине, серые ремни крест-накрест и кушак, наплечник с солнечной панелью на левом плече." },
        new() { name = "Кеды Культа Солнца", slot = GearSlot.Feet, set = GearSet.SunCult,
            about = "Высокие кеды из тёмно-синей ткани с солнцами и раздвоенным носком, латунные накладки на носке, серая обмотка у щиколотки и латунное солнце с солнечной панелью сбоку." },
    };

    public static readonly GearKind[] All = (GearKind[])System.Enum.GetValues(typeof(GearKind));

    public static string Name(GearKind kind) => info[(int)kind].name;
    public static GearSlot Slot(GearKind kind) => info[(int)kind].slot;
    /// <summary>Часть базового набора: серая выдаётся бесплатно (IsFree), остальные цвета крафтятся</summary>
    public static bool IsBasic(GearKind kind) => info[(int)kind].basic;
    public static GearSet Set(GearKind kind) => info[(int)kind].set;
    public static string About(GearKind kind) => info[(int)kind].about;

    public static string SetName(GearSet set) => set switch
    {
        GearSet.Basic => "Базовый набор",
        GearSet.Nomads => "Островные Кочевники",
        _ => "Культ Солнца",
    };
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

    /// <summary>Бесплатная серая часть базового набора: есть всегда, в вылазке не теряется (Profile.GiveBasicSet)</summary>
    public static bool IsFree(GearKind kind, GearQuality quality) => IsBasic(kind) && quality == GearQuality.Gray;

    public static bool IsFree(GearPiece piece) => IsFree(piece.kind, piece.quality);

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
