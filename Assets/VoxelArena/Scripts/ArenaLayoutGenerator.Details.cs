using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

// Шаги BuildLayoutJob: деревья и камни, перенос итога в ArenaLayout
public static partial class ArenaLayoutGenerator
{
    partial struct BuildLayoutJob
    {
        // ---------- 8. Деревья и камни ----------

        void PlaceTreesAndRocks()
        {
            float density = s.forestDensity * forestScale;
            float rockChance = s.rockChance * rockScale;

            for (int z = 0; z < sz; z++)
            {
                for (int x = 0; x < sx; x++)
                {
                    int i = Idx(x, z);
                    blocked[i] |= reserved[i] || water[i] || shore[i] || corridor[i] || border[i];
                    if (!rampMask[i])
                        continue;
                    for (int dz = -1; dz <= 1; dz++)
                        for (int dx = -1; dx <= 1; dx++)
                            if (Inside(x + dx, z + dz))
                                blocked[Idx(x + dx, z + dz)] = true;
                }
            }

            var candidates = new UnsafeList<int>(n / 2, Allocator.Persistent);
            for (int i = 0; i < n / 2; i++)
                candidates.Add(i);
            Shuffle(ref candidates);

            for (int k = 0; k < candidates.Length; k++)
            {
                int i = candidates[k];
                bool tree;
                if (noiseMaps[i].w > s.forestThreshold && rnd.NextDouble() < density)
                    tree = true;
                else if (rnd.NextDouble() < rockChance)
                    tree = false;
                else
                    continue;

                int mi = Mirror(i);
                if (!Free(i) || !Flat3(i) || !Free(mi) || !Flat3(mi))
                    continue;

                AddTreeOrRock(i, tree);
                AddTreeOrRock(mi, tree);
            }
            candidates.Dispose();

            PlaceMountainTrees(density);
        }

        void AddTreeOrRock(int c, bool tree)
        {
            var cell = new int2(c % sx, c / sx);
            if (tree)
                outTrees.Add(cell);
            else
                outRocks.Add(cell);
            Mark(c);
        }

        // Пологий склон: соседи отличаются по высоте не больше чем на блок
        bool Gentle(int x, int z)
        {
            int h = height[Idx(x, z)];
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                    if (!Inside(x + dx, z + dz) || math.abs(height[Idx(x + dx, z + dz)] - h) > 1)
                        return false;
            return true;
        }

        // Лес на нижних пологих склонах гор. Горы непроходимы, симметрия тут не нужна.
        // Занятые клетки — в seen: он уже не нужен после коридоров
        void PlaceMountainTrees(float density)
        {
            var taken = seen;
            Clear(taken);
            for (int i = 0; i < n; i++)
            {
                if (!border[i] || mountainDepth[i] < 2 || mountainDepth[i] > 10)
                    continue;
                if (noiseMaps[i].w < s.forestThreshold - 0.1f || rnd.NextDouble() > density * 0.35)
                    continue;

                int x = i % sx, z = i / sx;
                if (!Gentle(x, z))
                    continue;

                bool free = true;
                for (int dz = -2; dz <= 2 && free; dz++)
                    for (int dx = -2; dx <= 2 && free; dx++)
                        free = Inside(x + dx, z + dz) && !taken[Idx(x + dx, z + dz)];
                if (!free)
                    continue;

                outTrees.Add(new int2(x, z));
                for (int dz = -2; dz <= 2; dz++)
                    for (int dx = -2; dx <= 2; dx++)
                        taken[Idx(x + dx, z + dz)] = true;
            }
        }

        // Дерево или камень занимает квадрат 5x5
        bool Free(int i)
        {
            int x = i % sx, z = i / sx;
            for (int dz = -2; dz <= 2; dz++)
            {
                for (int dx = -2; dx <= 2; dx++)
                {
                    int xx = x + dx, zz = z + dz;
                    if (!Inside(xx, zz))
                        return false;
                    int j = Idx(xx, zz);
                    if (blocked[j] || occupied[j])
                        return false;
                }
            }
            return true;
        }

        bool Flat3(int i)
        {
            int x = i % sx, z = i / sx;
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                    if (!Inside(x + dx, z + dz) || height[Idx(x + dx, z + dz)] != height[i])
                        return false;
            return true;
        }

        void Mark(int i)
        {
            int x = i % sx, z = i / sx;
            for (int dz = -2; dz <= 2; dz++)
                for (int dx = -2; dx <= 2; dx++)
                    if (Inside(x + dx, z + dz))
                        occupied[Idx(x + dx, z + dz)] = true;
        }

        // ---------- Итог ----------

        void FillLayout()
        {
            for (int z = 0; z < sz; z++)
            {
                for (int x = 0; x < sx; x++)
                {
                    int i = Idx(x, z);
                    byte flags = 0;
                    if (shore[i])
                        flags |= ArenaLayout.FlagSand;
                    if (rampMask[i])
                        flags |= ArenaLayout.FlagRamp;

                    if (border[i])
                    {
                        outHeight[i] = height[i];
                        flags |= ArenaLayout.FlagRock;
                        if (Gentle(x, z))
                            flags |= ArenaLayout.FlagSoftTop;
                    }
                    else if (water[i])
                    {
                        // Дно на 3 блока ниже берега, вода на блок ниже берега
                        int ground = LevelHeight(level[i]);
                        outHeight[i] = ground - 3;
                        outWaterTop[i] = ground - 1;
                        flags |= ArenaLayout.FlagSand;
                    }
                    else
                    {
                        outHeight[i] = height[i];
                    }

                    outFlags[i] = flags;
                }
            }

            // Край обрыва: сосед ниже на 2 и больше — стенка будет каменной
            for (int z = 0; z < sz; z++)
            {
                for (int x = 0; x < sx; x++)
                {
                    int i = Idx(x, z);
                    if (water[i])
                        continue;
                    for (int k = 0; k < 4; k++)
                    {
                        var d = Dir(k);
                        int xx = x + d.x, zz = z + d.y;
                        if (Inside(xx, zz) && outHeight[Idx(xx, zz)] <= outHeight[i] - 2)
                            outFlags[i] |= ArenaLayout.FlagCliff;
                    }
                }
            }
        }

        // Финальная проверка со всеми препятствиями: стены, деревья, камни, рудные бугры.
        // Препятствия собираются в occupied: он уже не нужен после деревьев
        void CheckReachability()
        {
            var obstacles = occupied;
            obstacles.CopyFrom(wallMask);
            for (int k = 0; k < outTrees.Length; k++)
                MarkSquare(obstacles, outTrees[k], 2);
            for (int k = 0; k < outRocks.Length; k++)
                MarkSquare(obstacles, outRocks[k], 2);
            for (int k = 0; k < outResources.Length; k++)
                MarkSquare(obstacles, outResources[k].cell, 2);

            Walk(gateOutA, obstacles, seen, false);

            outInfo[InfoConnected] = NearestSeen(gateOutB, seen, 1) >= 0 ? 1 : 0;
            bool reachable = true;
            for (int k = 0; k < captures.Length; k++)
                if (NearestSeen(captures[k], seen, CaptureReachRadius) < 0)
                    reachable = false;
            outInfo[InfoCaptures] = reachable ? 1 : 0;
        }

        void FillBaseZones()
        {
            // Сначала 3-й уровень, потом меньшие поверх: в клетке остаётся наименьший уровень
            for (int k = 0; k < 3; k++)
            {
                int zoneLevel = 3 - k;
                int size = k == 0 ? s.wallLevel3Size : k == 1 ? s.wallLevel2Size : s.wallLevel1Size;
                int h = size / 2;
                for (int dz = -h; dz < h; dz++)
                {
                    for (int dx = -h; dx < h; dx++)
                    {
                        var cell = baseA + Rotate(new int2(dx, dz));
                        outBaseZone[Idx(cell)] = (byte)zoneLevel;
                        outBaseZone[Idx(Mirror(cell))] = (byte)(4 + zoneLevel);
                    }
                }
            }

            for (int dz = -baseHalf; dz <= baseHalf; dz++)
            {
                for (int dx = -baseHalf; dx <= baseHalf; dx++)
                {
                    var cell = baseA + new int2(dx, dz);
                    if (!Inside(cell))
                        continue;
                    outBaseArea[Idx(cell)] = 1;
                    outBaseArea[Idx(Mirror(cell))] = 2;
                }
            }
        }

        void MarkSquare(UnsafeList<bool> mask, int2 c, int r)
        {
            for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                    if (Inside(c.x + dx, c.y + dz))
                        mask[Idx(c.x + dx, c.y + dz)] = true;
        }

        void Shuffle<T>(ref UnsafeList<T> list) where T : unmanaged
        {
            for (int i = list.Length - 1; i > 0; i--)
            {
                int j = rnd.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }
    }
}
