using UnityEngine;

namespace VoxelCharacterSystem
{
    /// <summary>
    /// Генератор воксельной модели персонажа-выжившего (Survivor / Raider),
    /// полностью воссоздающий персонажа из референса:
    /// - Ирокез (Mohawk)
    /// - Тактические очки и респиратор/противогаз
    /// - Наплечник из покрышки с шипами (правое плечо)
    /// - Сварной броненаплечник с полосами опасности Hazard (левое плечо)
    /// - Кожаный жилет с портупеей, патронташем (гильзы), цепью и воротником
    /// - Тактический ремень с пряжкой, подсумками, кобурой и мотком провода
    /// - Руки в Т-позе с бинтовыми обмотками и перчатками без пальцев
    /// - Штаны карго с защитной пластиной на одном колене и шипованным наголенником на другом
    /// - Армейские ботинки с усиленными носками
    /// </summary>
    public static class VoxelSurvivorBuilder
    {
        public const int SizeX = 64;
        public const int SizeY = 72;
        public const int SizeZ = 28;

        public static VoxelModel Build()
        {
            var model = new VoxelModel(SizeX, SizeY, SizeZ);

            BuildBoots(model);
            BuildLegsAndArmor(model);
            BuildPelvisAndBelt(model);
            BuildTorsoAndVest(model);
            BuildShouldersAndPauldrons(model);
            BuildArmsAndHands(model);
            BuildHeadNeckAndAccessories(model);

            return model;
        }

        static void BuildBoots(VoxelModel m)
        {
            // Подошвы (протектор Y=0, выступает на 1 воксел)
            m.FillBox(33, 0, 9, 41, 0, 19, SurvivorPalette.TireBlack);
            m.FillBox(23, 0, 9, 31, 0, 19, SurvivorPalette.TireBlack);

            // Основная часть ботинок (Y = 1..6)
            // Левый ботинок (персонажа) / правый для зрителя (X = 34..40)
            m.FillBox(34, 1, 10, 40, 6, 17, SurvivorPalette.BootsBrown);
            // Правый ботинок (персонажа) / левый для зрителя (X = 24..30)
            m.FillBox(24, 1, 10, 30, 6, 17, SurvivorPalette.BootsBrown);

            // Металлические носки ботинок (Z = 17..18, Y = 1..3)
            m.FillBox(34, 1, 17, 40, 3, 18, SurvivorPalette.DarkMetal);
            m.FillBox(35, 1, 18, 39, 2, 18, SurvivorPalette.MetalHighlight);

            m.FillBox(24, 1, 17, 30, 3, 18, SurvivorPalette.DarkMetal);
            m.FillBox(25, 1, 18, 29, 2, 18, SurvivorPalette.MetalHighlight);

            // Ремни и пряжки на ботинках (Y = 4..5)
            m.FillBox(33, 4, 11, 41, 5, 17, SurvivorPalette.BeltDark);
            m.Set(37, 5, 18, SurvivorPalette.BrassBuckle);

            m.FillBox(23, 4, 11, 31, 5, 17, SurvivorPalette.BeltDark);
            m.Set(27, 5, 18, SurvivorPalette.BrassBuckle);
        }

        static void BuildLegsAndArmor(VoxelModel m)
        {
            // Штаны карго (Y = 7..27)
            // Левая нога (X = 34..39, Z = 11..16)
            m.FillBox(34, 7, 11, 39, 27, 16, SurvivorPalette.PantsKhaki);
            // Правая нога (X = 25..30, Z = 11..16)
            m.FillBox(25, 7, 11, 30, 27, 16, SurvivorPalette.PantsKhaki);

            // Внутреннее затенение между ног
            m.FillBox(34, 12, 12, 34, 25, 15, SurvivorPalette.PantsShadow);
            m.FillBox(30, 12, 12, 30, 25, 15, SurvivorPalette.PantsShadow);

            // Карман карго на левом бедре (X = 40, Y = 18..24, Z = 12..15)
            m.FillBox(40, 18, 12, 40, 24, 15, SurvivorPalette.OliveDark);
            m.FillBox(40, 24, 12, 40, 24, 15, SurvivorPalette.DarkPouch);

            // Бронепластина на левом колене (Y = 13..17, Z = 17, X = 34..39)
            m.FillBox(34, 13, 17, 39, 17, 17, SurvivorPalette.SteelGunmetal);
            m.FillBox(35, 14, 17, 38, 16, 17, SurvivorPalette.RustDark);
            // Заклёпки по углам пластины
            m.Set(34, 17, 17, SurvivorPalette.MetalHighlight);
            m.Set(39, 17, 17, SurvivorPalette.MetalHighlight);
            m.Set(34, 13, 17, SurvivorPalette.MetalHighlight);
            m.Set(39, 13, 17, SurvivorPalette.MetalHighlight);

            // Тяжёлый ржавый наголенник с шипами на правой ноге (Y = 8..19)
            // Передняя и боковая пластина (X = 24..30, Z = 17..18)
            m.FillBox(24, 8, 17, 30, 19, 17, SurvivorPalette.RustOrange);
            m.FillBox(23, 9, 12, 23, 19, 17, SurvivorPalette.RustDark);

            // Вертикальные ряды выступающих шипов на наголеннике
            int[] spikeY = { 10, 13, 16, 19 };
            foreach (int sy in spikeY)
            {
                // Передние шипы
                m.Set(26, sy, 18, SurvivorPalette.MetalHighlight);
                m.Set(29, sy, 18, SurvivorPalette.MetalHighlight);
                m.Set(26, sy, 19, SurvivorPalette.DarkMetal);
                m.Set(29, sy, 19, SurvivorPalette.DarkMetal);

                // Боковые шипы
                m.Set(22, sy, 14, SurvivorPalette.MetalHighlight);
                m.Set(21, sy, 14, SurvivorPalette.DarkMetal);
            }
        }

        static void BuildPelvisAndBelt(VoxelModel m)
        {
            // Таз (Y = 28..31, X = 25..39, Z = 10..17)
            m.FillBox(25, 28, 10, 39, 31, 17, SurvivorPalette.PantsKhaki);

            // Тактический ремень (Y = 31..33, X = 24..40, Z = 9..18)
            m.FillBox(24, 31, 9, 40, 33, 18, SurvivorPalette.BeltDark);
            // Пряжка ремня по центру спереди (X = 31..33, Y = 31..32, Z = 19)
            m.FillBox(31, 31, 19, 33, 32, 19, SurvivorPalette.BrassBuckle);
            m.Set(32, 31, 19, SurvivorPalette.MetalHighlight);

            // Подсумки и снаряжение на ремне:
            // Справа (для зрителя слева, X = 41..43, Y = 30..34, Z = 11..16)
            m.FillBox(41, 30, 11, 42, 34, 13, SurvivorPalette.JacketGreenDark);
            m.FillBox(41, 34, 11, 42, 34, 13, SurvivorPalette.DarkPouch); // крышка
            m.FillBox(41, 29, 14, 42, 33, 16, SurvivorPalette.CanteenTeal);    // фляга
            m.Set(41, 34, 15, SurvivorPalette.MetalHighlight);               // пробка

            // Слева (для зрителя справа, X = 21..24, Y = 25..34, Z = 11..17)
            // Кобура для ножа/пистолета
            m.FillBox(23, 26, 12, 24, 33, 15, SurvivorPalette.LeatherBrown);
            m.FillBox(23, 33, 13, 24, 35, 14, SurvivorPalette.MetalHighlight); // рукоять ножа

            // Подвешенный красный кабель / провод кольцом (Y = 25..31, X = 22..24, Z = 16..18)
            m.Set(23, 31, 17, SurvivorPalette.RedWire);
            m.Set(22, 30, 18, SurvivorPalette.RedWire);
            m.Set(22, 29, 18, SurvivorPalette.RedWire);
            m.Set(22, 28, 18, SurvivorPalette.DeepRed);
            m.Set(23, 27, 18, SurvivorPalette.DeepRed);
            m.Set(24, 26, 17, SurvivorPalette.RedWire);
            m.Set(24, 27, 16, SurvivorPalette.RedWire);
            m.Set(23, 28, 16, SurvivorPalette.RedWire);
        }

        static void BuildTorsoAndVest(VoxelModel m)
        {
            // Рубашка / основа торса (Y = 34..50, X = 24..40, Z = 10..17)
            m.FillBox(24, 34, 10, 40, 50, 17, SurvivorPalette.JacketGreen);

            // Кожаный тактический жилет (поверх рубашки спереди, сзади и по бокам)
            // Спина (Z = 9)
            m.FillBox(24, 34, 9, 40, 50, 9, SurvivorPalette.LeatherDark);
            // Бока (X = 23 и X = 41)
            m.FillBox(23, 34, 10, 23, 50, 17, SurvivorPalette.LeatherDark);
            m.FillBox(41, 34, 10, 41, 50, 17, SurvivorPalette.LeatherDark);
            // Передние полочки жилета (Z = 18), открытые в центре V-образно
            m.FillBox(24, 34, 18, 30, 46, 18, SurvivorPalette.LeatherDark);
            m.FillBox(34, 34, 18, 40, 46, 18, SurvivorPalette.LeatherDark);

            // Воротник-стойка жилета вокруг шеи (Y = 48..51, Z = 9..11)
            m.FillBox(28, 48, 9, 36, 51, 9, SurvivorPalette.LeatherBrown);
            m.FillBox(27, 48, 10, 27, 51, 12, SurvivorPalette.LeatherBrown);
            m.FillBox(37, 48, 10, 37, 51, 12, SurvivorPalette.LeatherBrown);

            // Диагональная портупея / патронташ (от плеча X=40, Y=50, Z=12 к бедру X=26, Y=34, Z=18)
            for (int step = 0; step <= 14; step++)
            {
                float t = step / 14f;
                int x = (int)Mathf.Lerp(40, 26, t);
                int y = (int)Mathf.Lerp(50, 34, t);
                int z = (int)Mathf.Lerp(12, 18, t);

                m.Set(x, y, z + 1, SurvivorPalette.BeltDark);

                // Размещаем патроны вдоль ремня (латунная гильза + красный корпус)
                if (step % 2 == 1 && step >= 3 && step <= 11)
                {
                    m.Set(x, y, z + 2, SurvivorPalette.BrassBuckle);
                    m.Set(x, y + 1, z + 2, SurvivorPalette.RedWire);
                }
            }

            // Железная цепь, провисающая по груди (X = 28..36, Y = 45..47, Z = 18..19)
            m.Set(29, 47, 18, SurvivorPalette.MetalHighlight);
            m.Set(30, 46, 19, SurvivorPalette.DarkMetal);
            m.Set(31, 45, 19, SurvivorPalette.MetalHighlight);
            m.Set(32, 45, 19, SurvivorPalette.MetalHighlight);
            m.Set(33, 45, 19, SurvivorPalette.DarkMetal);
            m.Set(34, 46, 19, SurvivorPalette.MetalHighlight);
            m.Set(35, 47, 18, SurvivorPalette.DarkMetal);
        }

        static void BuildShouldersAndPauldrons(VoxelModel m)
        {
            // НАПЛЕЧНИК 1 (Левое плечо персонажа / правое для зрителя, X = 41..48, Y = 47..54, Z = 8..19)
            // Наплечник из автомобильной покрышки (Tire Pauldron) с рельефом и шипами
            m.FillBox(41, 47, 8, 47, 53, 18, SurvivorPalette.TireBlack);
            // Прорези протектора шины
            for (int z = 9; z <= 17; z += 2)
            {
                m.FillBox(42, 53, z, 47, 53, z, SurvivorPalette.DarkMetal);
                m.FillBox(47, 48, z, 47, 52, z, SurvivorPalette.DarkMetal);
            }
            // Шипы, вбитые в покрышку
            m.AddSpike(44, 54, 11, 0, 1, 0, 2, SurvivorPalette.MetalHighlight);
            m.AddSpike(46, 54, 14, 0, 1, 0, 3, SurvivorPalette.MetalHighlight);
            m.AddSpike(44, 54, 16, 0, 1, 0, 2, SurvivorPalette.MetalHighlight);
            m.AddSpike(48, 51, 13, 1, 0, 0, 2, SurvivorPalette.MetalHighlight);

            // НАПЛЕЧНИК 2 (Правое плечо персонажа / левое для зрителя, X = 16..23, Y = 47..54, Z = 8..19)
            // Сварной металлический наплечник со скошенными углами и полосами HAZARD
            m.FillBox(17, 47, 8, 23, 52, 18, SurvivorPalette.SteelGunmetal);
            m.FillBox(16, 48, 9, 16, 51, 17, SurvivorPalette.RustOrange); // ржавый внешний торец

            // Верхняя грань: диагональные полосы опасности (Hazard Yellow / Black stripes)
            for (int x = 17; x <= 23; x++)
            {
                for (int z = 8; z <= 18; z++)
                {
                    bool isYellow = ((x + z) % 4) < 2;
                    byte stripeColor = isYellow ? SurvivorPalette.HazardYellow : SurvivorPalette.PureBlack;
                    m.Set(x, 53, z, stripeColor);
                }
            }
        }

        static void BuildArmsAndHands(VoxelModel m)
        {
            // Руки в Т-позе (Y = 44..49, Z = 11..16)

            // ЛЕВАЯ РУКА (персонажа) / правая для зрителя (X = 41..58)
            // Плечо - закатанный оливковый рукав (X = 41..45)
            m.FillBox(41, 45, 11, 45, 49, 16, SurvivorPalette.JacketGreen);
            // Локоть - открытая кожа (X = 46..47)
            m.FillBox(46, 45, 12, 47, 48, 15, SurvivorPalette.SkinLight);
            // Предплечье - бинтовые обмотки (X = 48..53)
            m.FillBox(48, 44, 11, 53, 49, 16, SurvivorPalette.WrapsCloth);
            m.FillBox(50, 44, 11, 51, 49, 16, SurvivorPalette.DirtyCloth); // грязная ткань
            // Запястье - кожаный ремешок / браслет (X = 54)
            m.FillBox(54, 44, 11, 54, 49, 16, SurvivorPalette.LeatherDark);
            // Кисть - тактическая перчатка без пальцев (X = 55..58)
            m.FillBox(55, 45, 12, 57, 48, 15, SurvivorPalette.TireBlack);
            // Кончики пальцев телесного цвета (X = 58)
            m.FillBox(58, 45, 12, 58, 48, 15, SurvivorPalette.SkinLight);

            // ПРАВАЯ РУКА (персонажа) / левая для зрителя (X = 6..23)
            // Плечо - закатанный оливковый рукав (X = 19..23)
            m.FillBox(19, 45, 11, 23, 49, 16, SurvivorPalette.JacketGreen);
            // Локоть - открытая кожа (X = 17..18)
            m.FillBox(17, 45, 12, 18, 48, 15, SurvivorPalette.SkinLight);
            // Предплечье - бинтовые обмотки (X = 11..16)
            m.FillBox(11, 44, 11, 16, 49, 16, SurvivorPalette.WrapsCloth);
            m.FillBox(13, 44, 11, 14, 49, 16, SurvivorPalette.DirtyCloth);
            // Запястье - кожаный браслет с часами (X = 10)
            m.FillBox(10, 44, 11, 10, 49, 16, SurvivorPalette.LeatherDark);
            m.Set(10, 49, 13, SurvivorPalette.MetalHighlight); // циферблат
            // Кисть - перчатка без пальцев (X = 7..9)
            m.FillBox(7, 45, 12, 9, 48, 15, SurvivorPalette.TireBlack);
            // Кончики пальцев телесного цвета (X = 6)
            m.FillBox(6, 45, 12, 6, 48, 15, SurvivorPalette.SkinLight);
        }

        static void BuildHeadNeckAndAccessories(VoxelModel m)
        {
            // Шея (Y = 49..52, X = 30..34, Z = 12..15)
            m.FillBox(30, 49, 12, 34, 52, 15, SurvivorPalette.SkinLight);
            m.FillBox(30, 49, 12, 34, 50, 13, SurvivorPalette.SkinShadow);

            // Голова (Y = 53..63, X = 28..36, Z = 10..17)
            m.FillBox(28, 53, 10, 36, 63, 17, SurvivorPalette.SkinLight);
            // Тень под подбородком
            m.FillBox(28, 53, 10, 36, 54, 13, SurvivorPalette.SkinShadow);

            // Уши (X = 27 и X = 37, Y = 56..58, Z = 13..14)
            m.FillBox(27, 56, 13, 27, 58, 14, SurvivorPalette.SkinLight);
            m.FillBox(37, 56, 13, 37, 58, 14, SurvivorPalette.SkinLight);

            // ИРОКЕЗ (Mohawk) (X = 31..33, Y = 62..70, Z = 9..17)
            // Зубчатый панковский гребень
            m.FillBox(31, 64, 9, 33, 67, 17, SurvivorPalette.HairDark);
            // Центральные шипы ирокеза повыше (до Y = 70)
            m.FillBox(31, 68, 11, 33, 70, 15, SurvivorPalette.HairDark);
            m.FillBox(31, 69, 12, 33, 70, 14, SurvivorPalette.LeatherBrown); // светлые кончики волос
            m.Set(32, 71, 13, SurvivorPalette.HairDark);                       // пиковый шип

            // ОЧКИ ТАКТИЧЕСКИЕ (Goggles) (Y = 58..61)
            // Ремешок очков вокруг головы (Z = 10, X = 27..37)
            m.FillBox(27, 59, 10, 37, 59, 10, SurvivorPalette.BeltDark);
            m.FillBox(27, 59, 11, 27, 59, 16, SurvivorPalette.BeltDark);
            m.FillBox(37, 59, 11, 37, 59, 16, SurvivorPalette.BeltDark);

            // Металлическая оправа очков спереди (Z = 18)
            // Левый глаз (персонажа) / правый для зрителя (X = 33..36)
            m.FillBox(33, 58, 18, 36, 61, 18, SurvivorPalette.DarkMetal);
            // Правый глаз (персонажа) / левый для зрителя (X = 28..31)
            m.FillBox(28, 58, 18, 31, 61, 18, SurvivorPalette.DarkMetal);
            // Переносица
            m.Set(32, 59, 18, SurvivorPalette.DarkMetal);

            // Стёкла очков (Z = 18..19)
            m.FillBox(34, 59, 18, 35, 60, 18, SurvivorPalette.GogglesGlass);
            m.Set(35, 60, 19, SurvivorPalette.PureWhite); // блик стекла

            m.FillBox(29, 59, 18, 30, 60, 18, SurvivorPalette.GogglesGlass);
            m.Set(30, 60, 19, SurvivorPalette.PureWhite); // блик стекла

            // РЕСПИРАТОР / ПРОТИВОГАЗ (Y = 53..57, Z = 17..20)
            // Центральная часть маски с решёткой
            m.FillBox(31, 53, 18, 33, 57, 19, SurvivorPalette.SteelGunmetal);
            m.FillBox(31, 54, 20, 33, 56, 20, SurvivorPalette.DarkMetal);
            m.Set(32, 55, 20, SurvivorPalette.MetalHighlight); // центральный фильтр/клапан

            // Боковые цилиндрические фильтры на щеках
            // Левая щека (X = 36..37, Y = 53..56, Z = 17..19)
            m.FillBox(36, 53, 17, 37, 56, 19, SurvivorPalette.DarkMetal);
            m.FillBox(36, 54, 20, 37, 55, 20, SurvivorPalette.MetalHighlight);

            // Правая щека (X = 27..28, Y = 53..56, Z = 17..19)
            m.FillBox(27, 53, 17, 28, 56, 19, SurvivorPalette.DarkMetal);
            m.FillBox(27, 54, 20, 28, 55, 20, SurvivorPalette.MetalHighlight);

            // Шланги / трубки дыхания, идущие от фильтров назад к ушам
            m.Set(37, 53, 16, SurvivorPalette.TireBlack);
            m.Set(37, 53, 15, SurvivorPalette.TireBlack);
            m.Set(37, 54, 14, SurvivorPalette.SteelGunmetal);

            m.Set(27, 53, 16, SurvivorPalette.TireBlack);
            m.Set(27, 53, 15, SurvivorPalette.TireBlack);
            m.Set(27, 54, 14, SurvivorPalette.SteelGunmetal);
        }
    }
}
