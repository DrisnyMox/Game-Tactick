using UnityEngine;

namespace VoxelCharacterSystem
{
    /// <summary>
    /// Палитра воксельного персонажа, соответствующая текстуре Survivor_Palette.png (8x4 ячеек, 32 цвета).
    /// Предоставляет индексы слотов, UV координаты и цвета Color32.
    /// </summary>
    public static class SurvivorPalette
    {
        public const int Columns = 8;
        public const int Rows = 4;
        public const int TotalColors = 32;

        public const byte Air = 255;

        // Ряд 0 (Y=0, UV y=0.1250)
        public const byte SkinLight = 0;       // #B98863 Светлая кожа
        public const byte SkinShadow = 1;      // #8F6446 Тень кожи
        public const byte HairDark = 2;        // #2A1D16 Тёмные волосы (ирокез)
        public const byte JacketGreen = 3;     // #59613F Оливковая ткань рубашки/жилета
        public const byte JacketGreenDark = 4; // #3A4029 Тёмно-оливковый
        public const byte LeatherBrown = 5;    // #6A4527 Коричневая кожа
        public const byte LeatherDark = 6;     // #35251B Тёмная состаренная кожа
        public const byte PantsKhaki = 7;      // #7A6D4F Штаны хаки

        // Ряд 1 (Y=1, UV y=0.3750)
        public const byte PantsShadow = 8;     // #5A4E38 Тень штанов
        public const byte BootsBrown = 9;      // #4A4232 Коричневые армейские ботинки
        public const byte BeltDark = 10;       // #3A2A1E Ремень / тёмная кожа
        public const byte TireBlack = 11;      // #151515 Чёрная резина (наплечник из покрышки)
        public const byte DarkMetal = 12;      // #202224 Тёмный оружейный металл
        public const byte RustOrange = 13;     // #93502A Ржавчина
        public const byte RustDark = 14;       // #60301A Тёмная ржавчина / коррозия
        public const byte MetalHighlight = 15; // #7C8284 Светлый металл / заклёпки / цепь

        // Ряд 2 (Y=2, UV y=0.6250)
        public const byte SteelGunmetal = 16;  // #3D4143 Оружейная сталь / респиратор
        public const byte WrapsCloth = 17;     // #B8AA88 Бинты / обмотки на руках
        public const byte DirtyCloth = 18;     // #8E8063 Загрязнённая ткань
        public const byte RedWire = 19;        // #92301F Красный провод / гильзы
        public const byte HazardYellow = 20;   // #E0A82A Ярко-жёлтый (опасная зона на наплечнике)
        public const byte OliveStrap = 21;     // #383E31 Оливковый ремешок
        public const byte DesertTan = 22;      // #8E8E5A Песочный оттенок
        public const byte BrassBuckle = 23;    // #AD8836 Латунь / пряжки / гильзы

        // Ряд 3 (Y=3, UV y=0.8750)
        public const byte OliveDark = 24;      // #686040 Карман хаки
        public const byte DarkPouch = 25;      // #483F29 Подсумок
        public const byte GogglesGlass = 26;   // #4A5D70 Стекло тактических очков
        public const byte BoneLight = 27;      // #DDD7C4 Светлая кость / светлые детали
        public const byte DeepRed = 28;        // #5C1C15 Тёмно-красный патрон
        public const byte CanteenTeal = 29;    // #3D6E6A Фляга / бирюзовый элемент
        public const byte PureBlack = 30;      // #101010 Глубокий чёрный (полосы опасности)
        public const byte PureWhite = 31;      // #E8E8E8 Белый блик

        static readonly Color32[] colors =
        {
            new Color32(185, 136, 99, 255),  // 00 SkinLight
            new Color32(143, 100, 70, 255),  // 01 SkinShadow
            new Color32(42, 29, 22, 255),    // 02 HairDark
            new Color32(89, 97, 63, 255),    // 03 JacketGreen
            new Color32(58, 64, 41, 255),    // 04 JacketGreenDark
            new Color32(106, 69, 39, 255),   // 05 LeatherBrown
            new Color32(53, 37, 27, 255),    // 06 LeatherDark
            new Color32(122, 109, 79, 255),  // 07 PantsKhaki
            new Color32(90, 78, 56, 255),    // 08 PantsShadow
            new Color32(74, 66, 50, 255),    // 09 BootsBrown
            new Color32(58, 42, 30, 255),    // 10 BeltDark
            new Color32(21, 21, 21, 255),    // 11 TireBlack
            new Color32(32, 34, 36, 255),    // 12 DarkMetal
            new Color32(147, 80, 42, 255),   // 13 RustOrange
            new Color32(96, 48, 26, 255),    // 14 RustDark
            new Color32(124, 130, 132, 255), // 15 MetalHighlight
            new Color32(61, 65, 67, 255),    // 16 SteelGunmetal
            new Color32(184, 170, 136, 255), // 17 WrapsCloth
            new Color32(142, 128, 99, 255),  // 18 DirtyCloth
            new Color32(146, 48, 31, 255),   // 19 RedWire
            new Color32(224, 168, 42, 255),  // 20 HazardYellow
            new Color32(56, 62, 49, 255),    // 21 OliveStrap
            new Color32(142, 142, 90, 255),  // 22 DesertTan
            new Color32(173, 136, 54, 255),  // 23 BrassBuckle
            new Color32(104, 96, 64, 255),   // 24 OliveDark
            new Color32(72, 63, 41, 255),    // 25 DarkPouch
            new Color32(74, 93, 112, 255),   // 26 GogglesGlass
            new Color32(221, 215, 196, 255), // 27 BoneLight
            new Color32(92, 28, 21, 255),    // 28 DeepRed
            new Color32(61, 110, 106, 255),  // 29 CanteenTeal
            new Color32(16, 16, 16, 255),    // 30 PureBlack
            new Color32(232, 232, 232, 255)  // 31 PureWhite
        };

        public static Color32 GetColor(byte index)
        {
            if (index < colors.Length)
                return colors[index];
            return new Color32(255, 255, 255, 255);
        }

        /// <summary>
        /// Возвращает точный центр ячейки в палитре Survivor_Palette.png (8x4).
        /// </summary>
        public static Vector2 GetUV(byte index)
        {
            if (index >= TotalColors)
                return new Vector2(0.5f, 0.5f);

            int col = index % Columns;
            int row = index / Columns;

            float u = (col + 0.5f) / Columns;
            float v = (row + 0.5f) / Rows;
            return new Vector2(u, v);
        }
    }
}
