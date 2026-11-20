using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

// Шаги BuildLayoutJob: вода в низинах и рампы по графу связности
public static partial class ArenaLayoutGenerator
{
    partial struct BuildLayoutJob
    {
        // ---------- 5. Вода в низинах ----------

        void PlaceWater()
        {
            float threshold = s.waterThreshold + waterBonus;

            // Вокруг 7x7 — только низина без резерва: rows — строка 7 по x внутри карты целиком такая
            for (int z = 0; z < sz; z++)
            {
                for (int x = 0; x < sx; x++)
                {
                    bool ok = x >= 3 && x < sx - 3;
                    for (int dx = -3; dx <= 3 && ok; dx++)
                        ok = level[Idx(x + dx, z)] == 0 && !reserved[Idx(x + dx, z)];
                    rows[Idx(x, z)] = ok ? 1 : 0;
                }
            }

            for (int z = 0; z < sz; z++)
            {
                for (int x = 0; x < sx; x++)
                {
                    int i = Idx(x, z);
                    if (level[i] != 0 || noiseMaps[i].z < threshold)
                        continue;

                    bool ok = z >= 3 && z < sz - 3;
                    for (int dz = -3; dz <= 3 && ok; dz++)
                        ok = rows[Idx(x, z + dz)] != 0;
                    water[i] = ok;
                }
            }

            // Мелкие лужи убираем
            BuildComponents(water, true);
            for (int i = 0; i < n; i++)
                if (water[i] && compSize[comp[i]] < s.minLakeCells)
                    water[i] = false;

            // Берег — суша, у которой в окне 5x5 есть вода: сначала вода в строке по x, затем по столбцу
            for (int z = 0; z < sz; z++)
            {
                for (int x = 0; x < sx; x++)
                {
                    bool any = false;
                    for (int dx = math.max(-2, -x); dx <= math.min(2, sx - 1 - x) && !any; dx++)
                        any = water[Idx(x + dx, z)];
                    rows[Idx(x, z)] = any ? 1 : 0;
                }
            }

            for (int z = 0; z < sz; z++)
            {
                for (int x = 0; x < sx; x++)
                {
                    if (water[Idx(x, z)])
                        continue;
                    bool any = false;
                    for (int dz = math.max(-2, -z); dz <= math.min(2, sz - 1 - z) && !any; dz++)
                        any = rows[Idx(x, z + dz)] != 0;
                    if (any)
                        shore[Idx(x, z)] = true;
                }
            }
        }

        // ---------- 6. Рампы по графу связности ----------

        int Find(int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }
            return i;
        }

        void Union(int a, int b) => parent[Find(a)] = Find(b);

        int MirrorComp(int c) => comp[Mirror(compCell[c])];

        bool IsLand(int i) => !water[i] && !border[i];

        // Клетки рампы: половина на нижнем уровне, половина на верхнем, подъём 1 блок на 2 клетки
        bool RampCells(RampSite site, int width, ref UnsafeList<RampCell> cells)
        {
            cells.Clear();

            int lo = level[Idx(site.x, site.z)];
            int hi = level[Idx(site.x + site.dx, site.z + site.dz)];
            int loH = LevelHeight(lo);
            int step = LevelHeight(hi) - loH;
            int half = step;
            int px = -site.dz, pz = site.dx;

            for (int t = -half; t < half; t++)
            {
                for (int w = -(width / 2); w <= width / 2; w++)
                {
                    int x = site.x + site.dx * (t + 1) + px * w;
                    int z = site.z + site.dz * (t + 1) + pz * w;
                    if (!Inside(x, z))
                        return false;

                    // (t + half + 0.5) * step / (2 * half) никогда не попадает ровно на .5
                    float h = loH + step * (t + half + 0.5f) / (2f * half);
                    cells.Add(new RampCell { index = Idx(x, z), lowSide = t < 0, height = (int)math.round(h) });
                }
            }
            return true;
        }

        bool RampValid(RampSite site, int width, ref UnsafeList<RampCell> cells)
        {
            if (!RampCells(site, width, ref cells))
                return false;

            int lo = level[Idx(site.x, site.z)];
            int hi = level[Idx(site.x + site.dx, site.z + site.dz)];

            for (int k = 0; k < cells.Length; k++)
            {
                var cell = cells[k];
                if (water[cell.index] || reserved[cell.index] || rampMask[cell.index])
                    return false;
                if (level[cell.index] != (cell.lowSide ? lo : hi))
                    return false;
            }
            return true;
        }

        void StampRamp(UnsafeList<RampCell> cells)
        {
            for (int k = 0; k < cells.Length; k++)
            {
                var cell = cells[k];
                height[cell.index] = cell.height;
                rampMask[cell.index] = true;
                shore[cell.index] = false;
            }
            rampCount++;
        }

        // Места рампы пары — sites[start .. start + count) в порядке обхода карты
        bool TryAddRamp(UnsafeList<PairSite> sites, int start, int count, int width, ref UnsafeList<RampSite> order)
        {
            order.Clear();
            for (int k = 0; k < count; k++)
                order.Add(sites[start + k].site);
            Shuffle(ref order);

            for (int k = 0; k < order.Length; k++)
            {
                var site = order[k];
                if (!RampValid(site, width, ref cellsA))
                    continue;

                int i = Idx(site.x, site.z);
                int mi = Mirror(i);
                if (mi == i)
                {
                    StampRamp(cellsA);
                    return true;
                }

                var mirror = new RampSite { x = mi % sx, z = mi / sx, dx = -site.dx, dz = -site.dz };
                if (!RampValid(mirror, width, ref cellsB))
                    continue;

                // Рампа и её зеркальная копия не должны налезать друг на друга
                bool overlap = false;
                for (int a = 0; a < cellsA.Length && !overlap; a++)
                    for (int b = 0; b < cellsB.Length && !overlap; b++)
                        overlap = cellsA[a].index == cellsB[b].index;
                if (overlap)
                    continue;

                StampRamp(cellsA);
                StampRamp(cellsB);
                return true;
            }
            return false;
        }

        /// <summary>Возвращает true, если вся суша связана с базой.</summary>
        bool PlaceRamps(ref int flattened)
        {
            for (int i = 0; i < n; i++)
                land[i] = IsLand(i);

            BuildComponents(land, true);

            for (int i = 0; i < n; i++)
                height[i] = LevelHeight(level[i]);
            Clear(rampMask);
            rampCount = 0;

            // Кандидаты: пары (нижний участок, верхний участок) через уступ в один уровень
            var sites = new UnsafeList<PairSite>(1024, Allocator.Persistent);
            for (int z = 0; z < sz; z++)
            {
                for (int x = 0; x < sx; x++)
                {
                    int i = Idx(x, z);
                    if (!land[i])
                        continue;
                    for (int k = 0; k < 4; k++)
                    {
                        var d = Dir(k);
                        int xx = x + d.x, zz = z + d.y;
                        if (!Inside(xx, zz))
                            continue;
                        int j = Idx(xx, zz);
                        if (!land[j] || level[j] - level[i] != 1)
                            continue;

                        sites.Add(new PairSite
                        {
                            key = (long)comp[i] * 1_000_000 + comp[j],
                            order = sites.Length,
                            site = new RampSite { x = x, z = z, dx = d.x, dz = d.y },
                        });
                    }
                }
            }
            // По паре — непрерывный отрезок мест в порядке обхода; пары по возрастанию ключа
            sites.Sort();

            var pairKey = new UnsafeList<long>(64, Allocator.Persistent);
            var pairStart = new UnsafeList<int>(64, Allocator.Persistent);
            for (int k = 0; k < sites.Length; k++)
            {
                if (k == 0 || sites[k].key != sites[k - 1].key)
                {
                    pairKey.Add(sites[k].key);
                    pairStart.Add(k);
                }
            }
            pairStart.Add(sites.Length);
            int pairCount = pairKey.Length;

            var pairs = new UnsafeList<int>(pairCount, Allocator.Persistent);
            for (int p = 0; p < pairCount; p++)
                pairs.Add(p);
            Shuffle(ref pairs);

            int compCount = compSize.Length;
            parent.Resize(compCount);
            for (int c = 0; c < compCount; c++)
                parent[c] = c;

            var order = new UnsafeList<RampSite>(64, Allocator.Persistent);
            var withRamp = new UnsafeList<bool>(pairCount, Allocator.Persistent);
            withRamp.Resize(pairCount, NativeArrayOptions.ClearMemory);

            // Остов: рампа там, где участки ещё не связаны
            for (int k = 0; k < pairCount; k++)
            {
                int p = pairs[k];
                long key = pairKey[p];
                int a = (int)(key / 1_000_000), b = (int)(key % 1_000_000);
                if (Find(a) == Find(b))
                    continue;
                if (!TryAddRamp(sites, pairStart[p], pairStart[p + 1] - pairStart[p], s.rampWidth, ref order))
                    continue;

                Union(a, b);
                Union(MirrorComp(a), MirrorComp(b));
                withRamp[p] = true;
            }

            // Лишние рампы — чтобы к каждому месту было больше одного пути
            for (int k = 0; k < pairCount; k++)
            {
                int p = pairs[k];
                if (withRamp[p] || rnd.NextDouble() >= s.extraRampChance)
                    continue;
                if (TryAddRamp(sites, pairStart[p], pairStart[p + 1] - pairStart[p], s.rampWidth, ref order))
                    withRamp[p] = true;
            }

            sites.Dispose();
            pairKey.Dispose();
            pairStart.Dispose();
            pairs.Dispose();
            order.Dispose();
            withRamp.Dispose();

            int baseComp = comp[Idx(baseA)];
            int root = Find(baseComp);
            bool allConnected = true;
            for (int c = 0; c < compCount && allConnected; c++)
                allConnected = Find(c) == root;
            if (allConnected)
                return true;

            // Запасной путь: оторванный участок выравнивается под соседний связанный уровень
            var counts = new UnsafeList<int3>(compCount, Allocator.Persistent);
            counts.Resize(compCount, NativeArrayOptions.ClearMemory);
            var detached = new UnsafeList<bool>(compCount, Allocator.Persistent);
            detached.Resize(compCount);
            int detachedCount = 0;
            for (int c = 0; c < compCount; c++)
            {
                detached[c] = Find(c) != root;
                if (detached[c])
                    detachedCount++;
            }

            for (int i = 0; i < n; i++)
            {
                if (!land[i] || !detached[comp[i]])
                    continue;
                int x = i % sx, z = i / sx;
                for (int k = 0; k < 4; k++)
                {
                    var d = Dir(k);
                    int xx = x + d.x, zz = z + d.y;
                    if (!Inside(xx, zz))
                        continue;
                    int j = Idx(xx, zz);
                    if (land[j] && comp[j] != comp[i] && Find(comp[j]) == root)
                    {
                        var cnt = counts[comp[i]];
                        cnt[level[j]]++;
                        counts[comp[i]] = cnt;
                    }
                }
            }

            for (int i = 0; i < n; i++)
            {
                if (!land[i] || !detached[comp[i]])
                    continue;
                int l = MostCommonLevel(counts[comp[i]]);
                if (l >= 0)
                    level[i] = l;
            }

            counts.Dispose();
            detached.Dispose();
            flattened += detachedCount;
            return false;
        }
    }
}
