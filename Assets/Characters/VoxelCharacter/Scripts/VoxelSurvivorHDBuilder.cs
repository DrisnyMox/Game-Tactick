using System;
using System.Collections.Generic;
using UnityEngine;

namespace VoxelCharacterSystem
{
    /// <summary>
    /// Высокодетализированный воксельный строитель Ultra-HD персонажа Survivor.
    /// Размер вокселя: 0.008м (~245 вокселей в высоту = рост ~1.96м).
    /// Все части тела смоделированы с органической анатомией, радиальными формами,
    /// круглыми линзами очков, круглыми фильтрами маски, арочной покрышкой и сложным рельефом.
    /// </summary>
    public static class VoxelSurvivorHDBuilder
    {
        public const float TargetVoxelSize = 0.008f;

        public static List<VoxelCharacterPart> BuildAllParts()
        {
            var parts = new List<VoxelCharacterPart>
            {
                BuildTorso(),
                BuildHead(),
                BuildTirePauldron(),   // Правое плечо персонажа (viewer's left, +X)
                BuildHazardPauldron(), // Левое плечо персонажа (viewer's right, -X)
                BuildRightArm(),       // Правая рука персонажа (+X)
                BuildLeftArm(),        // Левая рука персонажа (-X)
                BuildRightLeg(),       // Правая нога персонажа (+X, наколенник)
                BuildLeftLeg()         // Левая нога персонажа (-X, шипованный наголенник)
            };

            return parts;
        }

        // =========================================================================
        // 1. АНАТОМИЧЕСКИЙ ТОРС (V-Taper Chest, Open Vest, 3D Shells, Chain & Belt)
        // =========================================================================
        static VoxelCharacterPart BuildTorso()
        {
            // Сетка 64 x 76 x 46
            int sx = 64, sy = 76, sz = 46;
            var m = new VoxelModel(sx, sy, sz);

            float cx = 32f;
            float cz = 23f;

            // 1. Таз и поясница (Y = 0..16)
            m.FillEllipsoid(cx, 8f, cz, 19.5f, 8.5f, 13.5f, SurvivorPalette.PantsKhaki);
            m.FillEllipsoid(cx, 2f, cz, 6.0f, 3.5f, 9.0f, SurvivorPalette.PantsShadow);

            // 2. Торс с анатомическим V-образным сужением (Y = 16..72)
            // Талия (Y=16): rx=18.5, rz=12.0 -> Грудь (Y=66): rx=24.5, rz=15.5
            m.FillTaperedEllipticY(16, 72, cx, cz, 18.5f, 24.5f, 12.0f, 15.5f, SurvivorPalette.JacketGreen);

            // Рельефные грудные мышцы спереди
            m.FillEllipsoid(cx - 8.5f, 56f, cz + 10.0f, 8.0f, 7.0f, 5.5f, SurvivorPalette.JacketGreen);
            m.FillEllipsoid(cx + 8.5f, 56f, cz + 10.0f, 8.0f, 7.0f, 5.5f, SurvivorPalette.JacketGreen);

            // 3. Кожаный боевой жилет поверх рубашки (внешний слой с V-образным вырезом)
            for (int y = 16; y <= 72; y++)
            {
                float t = (y - 16) / 56f;
                float rx = Mathf.Lerp(19.8f, 26.0f, t);
                float rz = Mathf.Lerp(13.2f, 16.8f, t);
                float openWidth = Mathf.Lerp(4.5f, 11.5f, t);

                for (int z = (int)(cz - rz); z <= (int)(cz + rz); z++)
                {
                    float nz = (z - cz) / rz;
                    float nzSq = nz * nz;
                    if (nzSq > 1f) continue;

                    for (int x = (int)(cx - rx); x <= (int)(cx + rx); x++)
                    {
                        float nx = (x - cx) / rx;
                        float distSq = nx * nx + nzSq;
                        if (distSq > 1f || distSq < 0.74f) continue;

                        // Спереди V-вырез открывает зеленую ткань
                        if (z >= (int)cz && Mathf.Abs(x - cx) < openWidth) continue;

                        m.Set(x, y, z, SurvivorPalette.LeatherDark);
                    }
                }
            }

            // 3D-лацканы жилета по краям выреза
            for (int y = 24; y <= 64; y++)
            {
                float t = (y - 16) / 56f;
                float rx = Mathf.Lerp(19.8f, 26.0f, t);
                float rz = Mathf.Lerp(13.2f, 16.8f, t);
                float openWidth = Mathf.Lerp(4.5f, 11.5f, t);

                int xLeft = (int)(cx - openWidth);
                int xRight = (int)(cx + openWidth);
                int zFront = (int)(cz + rz * 0.95f);

                m.Set(xLeft, y, zFront, SurvivorPalette.LeatherBrown);
                m.Set(xLeft - 1, y, zFront, SurvivorPalette.LeatherBrown);
                m.Set(xRight, y, zFront, SurvivorPalette.LeatherBrown);
                m.Set(xRight + 1, y, zFront, SurvivorPalette.LeatherBrown);
            }

            // Воротник-стойка сзади шеи (Y = 68..75)
            for (int y = 68; y <= 75; y++)
            {
                for (int angle = 110; angle <= 250; angle += 4)
                {
                    float rad = angle * Mathf.Deg2Rad;
                    int x = (int)(cx + Mathf.Cos(rad) * 14.5f);
                    int z = (int)(cz + Mathf.Sin(rad) * 13.0f);
                    m.Set(x, y, z, SurvivorPalette.LeatherBrown);
                    m.Set(x, y, z - 1, SurvivorPalette.LeatherDark);
                }
            }

            // 4. Тактический ремень (Y = 14..20)
            for (int y = 14; y <= 20; y++)
            {
                for (int angle = 0; angle < 360; angle += 3)
                {
                    float rad = angle * Mathf.Deg2Rad;
                    int x = (int)(cx + Mathf.Cos(rad) * 19.8f);
                    int z = (int)(cz + Mathf.Sin(rad) * 13.2f);
                    m.Set(x, y, z, SurvivorPalette.BeltDark);
                }
            }

            // Пряжка ремня по центру спереди
            m.FillBox(28, 14, 35, 36, 20, 36, SurvivorPalette.BrassBuckle);
            m.FillBox(30, 15, 36, 34, 19, 36, SurvivorPalette.MetalHighlight);

            // 5. Подсумки и снаряжение на бёдрах
            // Правое бедро (+X для зрителя): два двойных подсумка и круглая фляга
            m.FillBox(53, 12, 14, 57, 22, 21, SurvivorPalette.JacketGreenDark);
            m.FillBox(53, 22, 14, 57, 24, 21, SurvivorPalette.DarkPouch);
            m.FillBox(53, 12, 23, 57, 22, 30, SurvivorPalette.JacketGreenDark);
            m.FillBox(53, 22, 23, 57, 24, 30, SurvivorPalette.DarkPouch);

            // Фляга
            m.FillTaperedCylinderY(11, 20, 55f, 33f, 3.5f, 3.5f, SurvivorPalette.CanteenTeal);
            m.FillTaperedCylinderY(20, 22, 55f, 33f, 1.8f, 1.8f, SurvivorPalette.MetalHighlight);

            // Левое бедро (-X для зрителя): кобура и моток красного кабеля
            m.FillBox(7, 9, 15, 11, 21, 22, SurvivorPalette.LeatherBrown);
            m.FillBox(7, 21, 17, 10, 26, 20, SurvivorPalette.MetalHighlight);

            // Висящий красный провод (спираль)
            for (int step = 0; step < 36; step++)
            {
                float t = step / 36f;
                float angle = t * Mathf.PI * 3f;
                int px = (int)(9f + Mathf.Cos(angle) * 3f);
                int py = (int)(15f - t * 14f);
                int pz = (int)(28f + Mathf.Sin(angle) * 3.5f);
                m.Set(px, py, pz, step % 2 == 0 ? SurvivorPalette.RedWire : SurvivorPalette.DeepRed);
            }

            // 6. Диагональный патронташ с 10 выступающими патронами
            for (int step = 0; step <= 48; step++)
            {
                float t = step / 48f;
                float px = Mathf.Lerp(50f, 14f, t);
                float py = Mathf.Lerp(72f, 18f, t);

                float rx = Mathf.Lerp(26.0f, 19.8f, 1f - t);
                float rz = Mathf.Lerp(16.8f, 13.2f, 1f - t);
                float nx = (px - cx) / rx;
                float pz = cz + Mathf.Sqrt(Mathf.Max(0f, 1f - nx * nx)) * rz;

                int ix = (int)px;
                int iy = (int)py;
                int iz = (int)(pz + 0.5f);

                m.FillBox(ix - 1, iy - 1, iz, ix + 1, iy, iz + 1, SurvivorPalette.BeltDark);

                // 3D-патроны
                if (step >= 8 && step <= 40 && step % 4 == 0)
                {
                    m.FillBox(ix - 1, iy - 1, iz + 2, ix + 1, iy, iz + 2, SurvivorPalette.BrassBuckle);  // латунный цоколь
                    m.FillBox(ix - 1, iy + 1, iz + 2, ix + 1, iy + 2, iz + 2, SurvivorPalette.RedWire); // красный корпус
                }
            }

            // 7. Провисающая железная цепь по параболической дуге
            for (int x = 20; x <= 44; x++)
            {
                float tx = (x - 32f) / 12f;
                int y = (int)(54f + tx * tx * 9f);
                int z = (int)(cz + 16.5f);
                byte c = (x % 2 == 0) ? SurvivorPalette.MetalHighlight : SurvivorPalette.DarkMetal;
                m.Set(x, y, z, c);
                m.Set(x, y - 1, z, c);
            }

            Vector3 pivot = new Vector3(-sx * 0.5f, 0f, -sz * 0.5f);
            return new VoxelCharacterPart("Torso", m, new Vector3(0f, 0.745f, 0f), pivot);
        }

        // =========================================================================
        // 2. КРУГЛАЯ ГОЛОВА (Elliptic Skull, Protruding Round Goggles, Gas Mask & Mohawk)
        // =========================================================================
        static VoxelCharacterPart BuildHead()
        {
            // Сетка 48 x 64 x 48
            int sx = 48, sy = 64, sz = 48;
            var m = new VoxelModel(sx, sy, sz);

            float cx = 24f;
            float cz = 24f;

            // 1. Шея (Y = 0..10) - круглый анатомический цилиндр
            m.FillTaperedCylinderY(0, 10, cx, cz, 8.2f, 7.6f, SurvivorPalette.SkinLight);
            m.FillTaperedCylinderY(0, 7, cx, cz - 2f, 6.0f, 5.5f, SurvivorPalette.SkinShadow);

            // 2. Череп (Y = 10..42) - эллипсоид головы
            m.FillEllipsoid(cx, 28f, cz, 12.5f, 15.0f, 13.5f, SurvivorPalette.SkinLight);

            // Клиновидная челюсть и скруглённый подбородок
            m.FillTaperedEllipticY(10, 24, cx, cz + 2f, 7.5f, 11.8f, 7.0f, 13.0f, SurvivorPalette.SkinLight);
            m.FillSphere(cx, 12f, cz + 9.0f, 4.8f, SurvivorPalette.SkinLight);
            m.FillEllipsoid(cx, 10f, cz, 8.0f, 2.5f, 7.0f, SurvivorPalette.SkinShadow);

            // 3. Анатомические уши
            m.FillEllipsoid(cx - 13.2f, 25f, cz, 1.5f, 4.5f, 3.2f, SurvivorPalette.SkinLight);
            m.FillEllipsoid(cx + 13.2f, 25f, cz, 1.5f, 4.5f, 3.2f, SurvivorPalette.SkinLight);
            m.Set((int)(cx - 12.8f), 25, (int)cz, SurvivorPalette.SkinShadow);
            m.Set((int)(cx + 12.8f), 25, (int)cz, SurvivorPalette.SkinShadow);

            // 4. ИРОКЕЗ (Mohawk) - изогнутая дуга зубцов
            for (int z = 10; z <= 37; z++)
            {
                float tz = (z - 24f) / 14f;
                float skullTop = 28f + Mathf.Sqrt(Mathf.Max(0f, 1f - tz * tz)) * 15.0f;

                float wave = Mathf.Sin(z * 0.9f) * 3.0f;
                float mohawkTop = skullTop + 8.5f + wave;
                if (z >= 21 && z <= 28) mohawkTop += 5.0f; // центральный гребень

                int topY = (int)mohawkTop;
                int baseY = (int)skullTop - 1;

                for (int y = baseY; y <= topY; y++)
                {
                    int halfW = (y >= topY - 2) ? 2 : 3;
                    byte hairCol = (y >= topY - 2) ? SurvivorPalette.LeatherBrown : SurvivorPalette.HairDark;
                    m.FillBox((int)cx - halfW, y, z, (int)cx + halfW, y, z, hairCol);
                }
            }

            // 5. ТАКТИЧЕСКИЕ ОЧКИ - выступающие круглые тороидальные оправы
            // Ремешок очков вокруг головы
            for (int angle = 0; angle < 360; angle += 3)
            {
                float rad = angle * Mathf.Deg2Rad;
                int x = (int)(cx + Mathf.Cos(rad) * 12.8f);
                int z = (int)(cz + Mathf.Sin(rad) * 13.8f);
                m.Set(x, 30, z, SurvivorPalette.BeltDark);
                m.Set(x, 31, z, SurvivorPalette.BeltDark);
            }

            // Круглые оправы линз: правый глаз (cx + 6.5), левый глаз (cx - 6.5)
            float[] eyeX = { cx + 6.5f, cx - 6.5f };
            float eyeY = 30.5f;

            foreach (float ex in eyeX)
            {
                // Оправа: цилиндрическое выступающее кольцо по Z от cz+12 до cz+18
                for (int z = (int)(cz + 12f); z <= (int)(cz + 18f); z++)
                {
                    for (int dy = -6; dy <= 6; dy++)
                    {
                        for (int dx = -6; dx <= 6; dx++)
                        {
                            float rSq = dx * dx + dy * dy;
                            // Внешнее кольцо оправы
                            if (rSq <= 6.2f * 6.2f && rSq >= 3.6f * 3.6f)
                            {
                                byte col = (z == (int)(cz + 18f)) ? SurvivorPalette.MetalHighlight : SurvivorPalette.DarkMetal;
                                m.Set((int)(ex + dx), (int)(eyeY + dy), z, col);
                            }
                            // Линза из голубого стекла (внутри оправы на z=cz+16..17)
                            else if (rSq < 3.6f * 3.6f && z >= (int)(cz + 15f) && z <= (int)(cz + 17f))
                            {
                                m.Set((int)(ex + dx), (int)(eyeY + dy), z, SurvivorPalette.GogglesGlass);
                            }
                        }
                    }
                }

                // Белый блик на стекле (в верхнем углу)
                int glintX = (ex > cx) ? (int)(ex + 2) : (int)(ex - 2);
                m.FillBox(glintX, (int)eyeY + 2, (int)(cz + 17f), glintX + 1, (int)eyeY + 3, (int)(cz + 17f), SurvivorPalette.PureWhite);
            }

            // Переносица между очками
            m.FillBox((int)(cx - 2), 30, (int)(cz + 14.5f), (int)(cx + 2), 31, (int)(cz + 16.5f), SurvivorPalette.SteelGunmetal);

            // 6. РЕСПИРАТОР / ПРОТИВОГАЗ
            // Клиновидный носовой кожух
            for (int y = 12; y <= 24; y++)
            {
                float ty = (y - 12) / 12f;
                float rz = Mathf.Lerp(8.0f, 4.2f, ty);
                float rx = Mathf.Lerp(7.0f, 3.8f, ty);
                m.FillTaperedEllipticY(y, y, cx, cz + 9f, rx, rx, rz, rz, SurvivorPalette.SteelGunmetal);
            }

            // Круглый передний клапан
            m.FillSphere(cx, 16.5f, cz + 16.5f, 3.8f, SurvivorPalette.DarkMetal);
            m.FillSphere(cx, 16.5f, cz + 18.0f, 2.0f, SurvivorPalette.MetalHighlight);

            // Круглые цилиндрические фильтры на щеках (диаметр 9 вокселей, скошены наружу)
            float[] filterX = { cx + 11.0f, cx - 11.0f };
            foreach (float fx in filterX)
            {
                m.FillSphere(fx, 16.5f, cz + 10.5f, 4.8f, SurvivorPalette.DarkMetal);
                m.FillSphere(fx, 16.5f, cz + 13.5f, 3.4f, SurvivorPalette.MetalHighlight);

                // Гофрированная трубка от фильтра назад вокруг шеи
                float dirX = (fx > cx) ? 1f : -1f;
                m.FillCapsule(
                    new Vector3(fx, 15f, cz + 8f),
                    new Vector3(cx + dirX * 12.5f, 15f, cz - 2f),
                    2.0f,
                    SurvivorPalette.TireBlack
                );
            }

            Vector3 pivot = new Vector3(-sx * 0.5f, 0f, -sz * 0.5f);
            return new VoxelCharacterPart("Head", m, new Vector3(0f, 1.32f, 0f), pivot);
        }

        // =========================================================================
        // 3. РАДИАЛЬНЫЙ НАПЛЕЧНИК-ПОКРЫШКА (Tire Pauldron, +X - Viewer's Left)
        // =========================================================================
        static VoxelCharacterPart BuildTirePauldron()
        {
            // Сетка 36 x 40 x 46
            int sx = 36, sy = 40, sz = 46;
            var m = new VoxelModel(sx, sy, sz);

            float cy = 15f;
            float cz = 23f;

            // Наплечник — радиальный сегмент колеса: R=19.0, r=11.0, ось вдоль X
            for (int x = 4; x <= 32; x++)
            {
                for (int z = 2; z <= 44; z++)
                {
                    float dz = z - cz;
                    for (int y = 6; y <= 36; y++)
                    {
                        float dy = y - cy;
                        float distSq = dy * dy + dz * dz;

                        if (distSq <= 19.0f * 19.0f && distSq >= 10.5f * 10.5f && dy >= -4f)
                        {
                            float angleDeg = Mathf.Atan2(dy, dz) * Mathf.Rad2Deg;
                            bool isGroove = (Mathf.Abs(angleDeg) % 20f) < 4.5f;

                            if (isGroove && distSq >= 16.5f * 16.5f)
                                m.Set(x, y, z, SurvivorPalette.DarkMetal); // канавка протектора
                            else
                                m.Set(x, y, z, SurvivorPalette.TireBlack); // резина
                        }
                    }
                }
            }

            // Металлическая скоба крепления
            m.FillCapsule(new Vector3(6f, 12f, 7f), new Vector3(30f, 12f, 7f), 2.2f, SurvivorPalette.SteelGunmetal);
            m.FillCapsule(new Vector3(6f, 12f, 39f), new Vector3(30f, 12f, 39f), 2.2f, SurvivorPalette.SteelGunmetal);

            // 4 острых 3D-шипа
            m.AddSpike(12, 33, 14, 0, 1, 0, 6, SurvivorPalette.MetalHighlight);
            m.AddSpike(22, 34, 23, 0, 1, 0, 7, SurvivorPalette.MetalHighlight);
            m.AddSpike(12, 33, 32, 0, 1, 0, 6, SurvivorPalette.MetalHighlight);
            m.AddSpike(33, 22, 23, 1, 0, 0, 6, SurvivorPalette.MetalHighlight);

            Vector3 pivot = new Vector3(-sx * 0.5f, -sy * 0.5f, -sz * 0.5f);
            return new VoxelCharacterPart("TirePauldron", m, new Vector3(0.20f, 1.30f, 0.01f), pivot);
        }

        // =========================================================================
        // 4. СВАРНОЙ НАПЛЕЧНИК СО СКОСОМ (Hazard Pauldron, -X - Viewer's Right)
        // =========================================================================
        static VoxelCharacterPart BuildHazardPauldron()
        {
            int sx = 36, sy = 40, sz = 46;
            var m = new VoxelModel(sx, sy, sz);

            for (int y = 8; y <= 29; y++)
            {
                float ty = (y - 8) / 21f;
                float minX = Mathf.Lerp(6f, 3f, ty);
                float maxX = Mathf.Lerp(30f, 33f, ty);
                float minZ = Mathf.Lerp(8f, 5f, ty);
                float maxZ = Mathf.Lerp(38f, 41f, ty);

                for (int z = (int)minZ; z <= (int)maxZ; z++)
                {
                    for (int x = (int)minX; x <= (int)maxX; x++)
                    {
                        if ((x - minX) + (z - minZ) < 5) continue;
                        if ((maxX - x) + (z - minZ) < 5) continue;
                        if ((x - minX) + (maxZ - z) < 5) continue;
                        if ((maxX - x) + (maxZ - z) < 5) continue;

                        if (y >= 28)
                        {
                            // Диагональные полосы опасности
                            bool isYellow = ((x + z) % 9) < 5;
                            m.Set(x, y, z, isYellow ? SurvivorPalette.HazardYellow : SurvivorPalette.PureBlack);
                        }
                        else
                        {
                            bool isBorder = (x <= minX + 2 || x >= maxX - 2 || z <= minZ + 2 || z >= maxZ - 2);
                            m.Set(x, y, z, isBorder ? SurvivorPalette.RustOrange : SurvivorPalette.SteelGunmetal);
                        }
                    }
                }
            }

            // 4 заклёпки по углам
            m.Set(8, 30, 10, SurvivorPalette.MetalHighlight);
            m.Set(28, 30, 10, SurvivorPalette.MetalHighlight);
            m.Set(8, 30, 36, SurvivorPalette.MetalHighlight);
            m.Set(28, 30, 36, SurvivorPalette.MetalHighlight);

            Vector3 pivot = new Vector3(-sx * 0.5f, -sy * 0.5f, -sz * 0.5f);
            return new VoxelCharacterPart("HazardPauldron", m, new Vector3(-0.20f, 1.30f, 0.01f), pivot);
        }

        // =========================================================================
        // 5. ПРАВАЯ РУКА (RightArm, +X - Viewer's Left, Bicep, Wraps, Wrist Compass)
        // =========================================================================
        static VoxelCharacterPart BuildRightArm()
        {
            // Сетка 64 x 24 x 24. Shoulder на X=0, hand на X=63
            int sx = 64, sy = 24, sz = 24;
            var m = new VoxelModel(sx, sy, sz);

            float cy = 12f;
            float cz = 12f;

            // 1. Плечо и бицепс (X = 0..18)
            m.FillTaperedCylinderX(0, 18, cy, cz, 8.8f, 8.0f, SurvivorPalette.JacketGreen);
            // Закатанный рукав
            m.FillTaperedCylinderX(18, 22, cy, cz, 9.2f, 9.2f, SurvivorPalette.JacketGreenDark);

            // 2. Локоть (X = 22..29)
            m.FillTaperedCylinderX(22, 29, cy, cz, 7.5f, 6.8f, SurvivorPalette.SkinLight);
            m.FillSphere(25.5f, cy, cz - 2f, 5.5f, SurvivorPalette.SkinShadow);

            // 3. Предплечье с бинтами (X = 29..48)
            m.FillTaperedCylinderX(29, 48, cy, cz, 7.6f, 5.6f, SurvivorPalette.WrapsCloth);
            for (int x = 31; x <= 47; x += 4)
            {
                for (int angle = 0; angle < 360; angle += 12)
                {
                    float rad = angle * Mathf.Deg2Rad;
                    float r = Mathf.Lerp(7.6f, 5.6f, (x - 29f) / 19f);
                    int y = (int)(cy + Mathf.Cos(rad) * r);
                    int z = (int)(cz + Mathf.Sin(rad) * r);
                    m.Set(x, y, z, SurvivorPalette.DirtyCloth);
                }
            }

            // 4. Запястье и компас (X = 48..54)
            m.FillTaperedCylinderX(48, 54, cy, cz, 6.0f, 6.0f, SurvivorPalette.LeatherDark);
            m.FillCylinderY(51f, cz, 3.2f, 17, 19, SurvivorPalette.MetalHighlight);
            m.Set(51, 19, (int)cz, SurvivorPalette.GogglesGlass);

            // 5. Кисть и 5 пальцев (X = 54..63)
            m.FillEllipsoid(58.0f, cy, cz, 4.5f, 4.2f, 5.8f, SurvivorPalette.TireBlack);
            m.FillEllipsoid(58.0f, cy + 2.0f, cz, 3.2f, 1.8f, 4.8f, SurvivorPalette.DarkMetal);

            float[] fingerZ = { cz - 3.6f, cz - 1.2f, cz + 1.2f, cz + 3.6f };
            foreach (float fz in fingerZ)
            {
                m.FillCapsule(new Vector3(60f, cy - 0.8f, fz), new Vector3(62f, cy - 0.8f, fz), 1.6f, SurvivorPalette.TireBlack);
                m.FillSphere(63f, cy - 0.8f, fz, 1.5f, SurvivorPalette.SkinLight);
            }
            // Большой палец
            m.FillCapsule(new Vector3(58f, cy + 1.8f, cz + 4.2f), new Vector3(60f, cy + 2.5f, cz + 6.0f), 1.6f, SurvivorPalette.TireBlack);
            m.FillSphere(61f, cy + 3.0f, cz + 6.8f, 1.5f, SurvivorPalette.SkinLight);

            Vector3 pivot = new Vector3(0f, -sy * 0.5f, -sz * 0.5f);
            return new VoxelCharacterPart("RightArm", m, new Vector3(0.192f, 1.25f, 0f), pivot);
        }

        // =========================================================================
        // 6. ЛЕВАЯ РУКА (LeftArm, -X - Viewer's Right, Bicep, Wraps, Gloves)
        // =========================================================================
        static VoxelCharacterPart BuildLeftArm()
        {
            // Сетка 64 x 24 x 24. Shoulder на X=63, hand на X=0
            int sx = 64, sy = 24, sz = 24;
            var m = new VoxelModel(sx, sy, sz);

            float cy = 12f;
            float cz = 12f;

            // 1. Плечо и бицепс (X = 46..63)
            m.FillTaperedCylinderX(46, 63, cy, cz, 8.0f, 8.8f, SurvivorPalette.JacketGreen);
            // Закатанный рукав
            m.FillTaperedCylinderX(42, 46, cy, cz, 9.2f, 9.2f, SurvivorPalette.JacketGreenDark);

            // 2. Локоть (X = 35..42)
            m.FillTaperedCylinderX(35, 42, cy, cz, 6.8f, 7.5f, SurvivorPalette.SkinLight);
            m.FillSphere(38.5f, cy, cz - 2f, 5.5f, SurvivorPalette.SkinShadow);

            // 3. Предплечье с бинтами (X = 16..35)
            m.FillTaperedCylinderX(16, 35, cy, cz, 5.6f, 7.6f, SurvivorPalette.WrapsCloth);
            for (int x = 18; x <= 33; x += 4)
            {
                for (int angle = 0; angle < 360; angle += 12)
                {
                    float rad = angle * Mathf.Deg2Rad;
                    float r = Mathf.Lerp(5.6f, 7.6f, (x - 16f) / 19f);
                    int y = (int)(cy + Mathf.Cos(rad) * r);
                    int z = (int)(cz + Mathf.Sin(rad) * r);
                    m.Set(x, y, z, SurvivorPalette.DirtyCloth);
                }
            }

            // 4. Запястье (X = 10..16)
            m.FillTaperedCylinderX(10, 16, cy, cz, 6.0f, 6.0f, SurvivorPalette.LeatherDark);

            // 5. Кисть и пальцы (X = 0..10)
            m.FillEllipsoid(6.0f, cy, cz, 4.5f, 4.2f, 5.8f, SurvivorPalette.TireBlack);
            m.FillEllipsoid(6.0f, cy + 2.0f, cz, 3.2f, 1.8f, 4.8f, SurvivorPalette.DarkMetal);

            float[] fingerZ = { cz - 3.6f, cz - 1.2f, cz + 1.2f, cz + 3.6f };
            foreach (float fz in fingerZ)
            {
                m.FillCapsule(new Vector3(4f, cy - 0.8f, fz), new Vector3(2f, cy - 0.8f, fz), 1.6f, SurvivorPalette.TireBlack);
                m.FillSphere(1f, cy - 0.8f, fz, 1.5f, SurvivorPalette.SkinLight);
            }
            // Большой палец
            m.FillCapsule(new Vector3(6f, cy + 1.8f, cz + 4.2f), new Vector3(4f, cy + 2.5f, cz + 6.0f), 1.6f, SurvivorPalette.TireBlack);
            m.FillSphere(3f, cy + 3.0f, cz + 6.8f, 1.5f, SurvivorPalette.SkinLight);

            Vector3 pivot = new Vector3(-sx, -sy * 0.5f, -sz * 0.5f);
            return new VoxelCharacterPart("LeftArm", m, new Vector3(-0.192f, 1.25f, 0f), pivot);
        }

        // =========================================================================
        // 7. ПРАВАЯ НОГА (RightLeg, +X - Viewer's Left, Knee Pad, Cargo Pocket)
        // =========================================================================
        static VoxelCharacterPart BuildRightLeg()
        {
            // Сетка 28 x 96 x 40
            int sx = 28, sy = 96, sz = 40;
            var m = new VoxelModel(sx, sy, sz);

            float cx = 14f;
            float cz = 19f;

            // 1. Бедро (Y = 54..95)
            m.FillTaperedCylinderY(54, 95, cx, cz, 7.2f, 9.8f, SurvivorPalette.PantsKhaki);
            m.FillEllipsoid(cx + 7.5f, 72f, cz, 3.2f, 9.0f, 7.0f, SurvivorPalette.OliveDark); // карман

            // 2. Колено (Y = 46..56)
            m.FillSphere(cx, 51f, cz, 7.4f, SurvivorPalette.PantsKhaki);

            // Круглый наколенник
            m.FillEllipsoid(cx, 51f, cz + 7.2f, 6.8f, 7.8f, 2.8f, SurvivorPalette.SteelGunmetal);
            m.FillEllipsoid(cx, 51f, cz + 7.8f, 4.2f, 5.2f, 2.0f, SurvivorPalette.RustDark);
            m.Set((int)(cx - 4.5f), 56, (int)(cz + 8.0f), SurvivorPalette.MetalHighlight);
            m.Set((int)(cx + 4.5f), 56, (int)(cz + 8.0f), SurvivorPalette.MetalHighlight);
            m.Set((int)(cx - 4.5f), 46, (int)(cz + 8.0f), SurvivorPalette.MetalHighlight);
            m.Set((int)(cx + 4.5f), 46, (int)(cz + 8.0f), SurvivorPalette.MetalHighlight);

            // 3. Голень и икра (Y = 20..50)
            m.FillTaperedCylinderY(20, 50, cx, cz, 5.8f, 7.6f, SurvivorPalette.PantsKhaki);
            m.FillEllipsoid(cx, 36f, cz - 4.5f, 6.5f, 7.5f, 4.5f, SurvivorPalette.PantsKhaki);

            // 4. Армейский ботинок (Y = 0..20)
            m.FillCapsule(new Vector3(cx, 1.0f, cz - 7f), new Vector3(cx, 1.0f, cz + 10f), 8.0f, SurvivorPalette.TireBlack);
            m.FillTaperedCylinderY(3, 20, cx, cz, 7.0f, 6.2f, SurvivorPalette.BootsBrown);
            m.FillSphere(cx, 6.0f, cz + 9.5f, 6.2f, SurvivorPalette.DarkMetal);
            m.FillSphere(cx, 6.0f, cz + 10.8f, 3.8f, SurvivorPalette.MetalHighlight);

            m.FillEllipsoid(cx, 13f, cz + 1f, 7.5f, 2.0f, 7.5f, SurvivorPalette.BeltDark);
            m.Set((int)cx, 13, (int)(cz + 8.5f), SurvivorPalette.BrassBuckle);

            Vector3 pivot = new Vector3(-sx * 0.5f, 0f, -sz * 0.5f);
            return new VoxelCharacterPart("RightLeg", m, new Vector3(0.10f, 0f, 0f), pivot);
        }

        // =========================================================================
        // 8. ЛЕВАЯ НОГА (LeftLeg, -X - Viewer's Right, Massive Spiked Shin Guard)
        // =========================================================================
        static VoxelCharacterPart BuildLeftLeg()
        {
            int sx = 28, sy = 96, sz = 40;
            var m = new VoxelModel(sx, sy, sz);

            float cx = 14f;
            float cz = 19f;

            // 1. Бедро (Y = 54..95)
            m.FillTaperedCylinderY(54, 95, cx, cz, 7.2f, 9.8f, SurvivorPalette.PantsKhaki);
            m.FillSphere(cx, 51f, cz, 7.4f, SurvivorPalette.PantsKhaki);

            // 2. Голень и икра (Y = 20..50)
            m.FillTaperedCylinderY(20, 50, cx, cz, 5.8f, 7.6f, SurvivorPalette.PantsKhaki);
            m.FillEllipsoid(cx, 36f, cz - 4.5f, 6.5f, 7.5f, 4.5f, SurvivorPalette.PantsKhaki);

            // Тяжёлый ржавый наголенник (изогнутый дуговой щит)
            for (int y = 22; y <= 56; y++)
            {
                for (int angle = -20; angle <= 130; angle += 4)
                {
                    float rad = angle * Mathf.Deg2Rad;
                    int x = (int)(cx + Mathf.Cos(rad) * 8.2f);
                    int z = (int)(cz + Mathf.Sin(rad) * 8.2f);
                    m.Set(x, y, z, SurvivorPalette.RustOrange);
                    m.Set(x, y, z - 1, SurvivorPalette.RustDark);
                }
            }

            // 3 вертикальных ряда острых 3D-шипов (12 шипов)
            int[] spikeY = { 26, 35, 44, 52 };
            foreach (int syPos in spikeY)
            {
                m.FillSphere(cx - 3.5f, syPos, cz + 9.2f, 2.0f, SurvivorPalette.MetalHighlight);
                m.Set((int)(cx - 3.5f), syPos, (int)(cz + 11.5f), SurvivorPalette.DarkMetal);

                m.FillSphere(cx + 3.5f, syPos, cz + 9.2f, 2.0f, SurvivorPalette.MetalHighlight);
                m.Set((int)(cx + 3.5f), syPos, (int)(cz + 11.5f), SurvivorPalette.DarkMetal);

                m.FillSphere(cx - 9.2f, syPos, cz + 2.5f, 2.0f, SurvivorPalette.MetalHighlight);
                m.Set((int)(cx - 11.5f), syPos, (int)(cz + 2.5f), SurvivorPalette.DarkMetal);
            }

            // 3. Армейский ботинок
            m.FillCapsule(new Vector3(cx, 1.0f, cz - 7f), new Vector3(cx, 1.0f, cz + 10f), 8.0f, SurvivorPalette.TireBlack);
            m.FillTaperedCylinderY(3, 20, cx, cz, 7.0f, 6.2f, SurvivorPalette.BootsBrown);
            m.FillSphere(cx, 6.0f, cz + 9.5f, 6.2f, SurvivorPalette.DarkMetal);
            m.FillSphere(cx, 6.0f, cz + 10.8f, 3.8f, SurvivorPalette.MetalHighlight);

            m.FillEllipsoid(cx, 13f, cz + 1f, 7.5f, 2.0f, 7.5f, SurvivorPalette.BeltDark);
            m.Set((int)cx, 13, (int)(cz + 8.5f), SurvivorPalette.BrassBuckle);

            Vector3 pivot = new Vector3(-sx * 0.5f, 0f, -sz * 0.5f);
            return new VoxelCharacterPart("LeftLeg", m, new Vector3(-0.10f, 0f, 0f), pivot);
        }

        static void FillCylinderY(this VoxelModel m, float cx, float cz, float radius, int minY, int maxY, byte color)
        {
            m.FillTaperedCylinderY(minY, maxY, cx, cz, radius, radius, color);
        }
    }
}
