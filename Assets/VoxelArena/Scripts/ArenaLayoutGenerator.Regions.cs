using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

// Шаги BuildLayoutJob: участки, слияние мелких участков, стены и ворота баз, точки захвата
public static partial class ArenaLayoutGenerator
{
    partial struct BuildLayoutJob
    {
        // ---------- Участки ----------

        /// <summary>
        /// Участки — связные по 4 соседям клетки одного уровня (useValid — только клетки valid, остальные -1).
        /// Номер участка — по порядку его первой клетки при обходе карты построчно, compCell — эта клетка.
        /// Два прохода по строкам с объединением множеств (queue — родитель клетки, корень множества —
        /// его наименьший индекс): без обхода в ширину и деления индекса на x и z на каждую клетку
        /// </summary>
        void BuildComponents(UnsafeList<bool> valid, bool useValid)
        {
            var root = queue;
            for (int z = 0, i = 0; z < sz; z++)
            {
                for (int x = 0; x < sx; x++, i++)
                {
                    root[i] = i;
                    if (useValid && !valid[i])
                        continue;
                    int key = level[i];
                    if (x > 0 && level[i - 1] == key && (!useValid || valid[i - 1]))
                        Join(i - 1, i);
                    if (z > 0 && level[i - sx] == key && (!useValid || valid[i - sx]))
                        Join(i - sx, i);
                }
            }

            compSize.Clear();
            compCell.Clear();
            for (int i = 0; i < n; i++)
            {
                if (useValid && !valid[i])
                {
                    comp[i] = -1;
                    continue;
                }

                // Корень множества лежит раньше клетки, его номер уже известен
                int r = FindRoot(i);
                if (r == i)
                {
                    comp[i] = compSize.Length;
                    compSize.Add(0);
                    compCell.Add(i);
                }
                else
                {
                    comp[i] = comp[r];
                }
                compSize[comp[i]]++;
            }
        }

        int FindRoot(int i)
        {
            while (queue[i] != i)
            {
                queue[i] = queue[queue[i]];
                i = queue[i];
            }
            return i;
        }

        // Корень объединения — меньший из двух
        void Join(int a, int b)
        {
            int ra = FindRoot(a), rb = FindRoot(b);
            if (ra < rb)
                queue[rb] = ra;
            else if (rb < ra)
                queue[ra] = rb;
        }

        // По участку: сколько соседних клеток каждого уровня; best — самый частый уровень, при равенстве меньший
        static int MostCommonLevel(int3 counts)
        {
            int best = math.cmax(counts);
            if (best == 0)
                return -1;
            return counts.x == best ? 0 : counts.y == best ? 1 : 2;
        }

        // ---------- 3. Мелкие участки вливаются в соседей ----------

        void RemoveSmallRegions()
        {
            var counts = new UnsafeList<int3>(64, Allocator.Persistent);
            var target = new UnsafeList<bool>(64, Allocator.Persistent);
            var newLevel = new UnsafeList<int>(64, Allocator.Persistent);

            for (int pass = 0; pass < 6; pass++)
            {
                BuildComponents(default, false);

                int compCount = compSize.Length;
                counts.Resize(compCount, NativeArrayOptions.ClearMemory);
                Clear(counts);
                target.Resize(compCount);
                bool any = false;
                for (int c = 0; c < compCount; c++)
                {
                    target[c] = compSize[c] < s.minRegionCells;
                    any |= target[c];
                }
                if (!any)
                    break;

                for (int i = 0; i < n; i++)
                {
                    int c = comp[i];
                    if (!target[c])
                        continue;
                    int x = i % sx, z = i / sx;
                    for (int k = 0; k < 4; k++)
                    {
                        var d = Dir(k);
                        int xx = x + d.x, zz = z + d.y;
                        if (Inside(xx, zz) && comp[Idx(xx, zz)] != c)
                        {
                            var cnt = counts[c];
                            cnt[level[Idx(xx, zz)]]++;
                            counts[c] = cnt;
                        }
                    }
                }

                // Все мелкие участки меняются одновременно — симметрия сохраняется
                newLevel.Resize(compCount);
                for (int c = 0; c < compCount; c++)
                    newLevel[c] = target[c] ? MostCommonLevel(counts[c]) : -1;

                for (int i = 0; i < n; i++)
                {
                    int l = newLevel[comp[i]];
                    if (l >= 0)
                        level[i] = l;
                }

                ForceLevels();
            }

            counts.Dispose();
            target.Dispose();
            newLevel.Dispose();
        }

        // ---------- 4. База: стены и ворота ----------

        void PlaceBaseContent()
        {
            for (int i = 0; i < n; i++)
                reserved[i] = baseMask[i] || border[i];

            // Стены 1-го уровня базы A в каноническом виде (ворота на +z), затем поворот к противнику.
            // База B — зеркальная копия.
            int h = s.wallLevel1Size / 2;
            int t = s.wallThickness;
            int g = s.gateWidth / 2;
            for (int dz = -h - t; dz < h + t; dz++)
            {
                for (int dx = -h - t; dx < h + t; dx++)
                {
                    bool inner = dx >= -h && dx < h && dz >= -h && dz < h;
                    bool gate = dz >= h && dx >= -g && dx < g;
                    if (inner || gate)
                        continue;

                    bool outer = dx == -h - t || dx == h + t - 1 || dz == -h - t || dz == h + t - 1;
                    var cell = baseA + Rotate(new int2(dx, dz));
                    AddWall(cell, outer);
                    AddWall(Mirror(cell), outer);
                }
            }

            gateOutA = baseA + Rotate(new int2(0, h + t));
            gateOutB = Mirror(gateOutA);
        }

        void AddWall(int2 cell, bool outer)
        {
            outWalls.Add(new WallCell { cell = cell, outer = outer });
            wallMask[Idx(cell)] = true;
        }

        // ---------- Точки захвата ----------

        void PlaceCapturePoints()
        {
            Walk(gateOutA, wallMask, captureReach, false);

            var center = new float2((sx - 1) * 0.5f, (sz - 1) * 0.5f);
            var perp = new float2(-axis.y, axis.x);
            float size = math.min(sx, sz);

            for (int k = 0; k < 4; k++)
            {
                var target = k switch
                {
                    0 => (float2)baseA + axis * (baseHalf + 14),                                  // ближняя
                    1 => center - axis * baseDistance * 0.35f + perp * size * 0.30f,              // фланги
                    2 => center - axis * baseDistance * 0.35f - perp * size * 0.30f,
                    _ => center + perp * size * 0.12f,                                            // спорная у центра
                };

                if (!SnapCapturePoint((int2)math.round(target), out var cell))
                    continue;

                AddCapturePoint(cell);
                AddCapturePoint(Mirror(cell));
            }
        }

        void AddCapturePoint(int2 c)
        {
            captures.Add(c);
            outResources.Add(new ResourcePoint { cell = c, kind = ResourceKind.CapturePoint, team = -1 });
            for (int dz = -4; dz <= 4; dz++)
                for (int dx = -4; dx <= 4; dx++)
                    reserved[Idx(c.x + dx, c.y + dz)] = true;
        }

        // Ближайшая к цели клетка, вокруг которой 9x9 одного уровня и ничего не занято
        bool SnapCapturePoint(int2 target, out int2 cell)
        {
            for (int r = 0; r < 40; r++)
            {
                for (int dz = -r; dz <= r; dz++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (math.max(math.abs(dx), math.abs(dz)) != r)
                            continue;
                        var c = target + new int2(dx, dz);
                        if (CaptureSpot(c))
                        {
                            cell = c;
                            return true;
                        }
                    }
                }
            }

            cell = default;
            return false;
        }

        // Ровная площадка 9x9 на суше, без рамп, берега и резерва, и до неё (и до зеркальной) можно дойти
        bool CaptureSpot(int2 c)
        {
            if (!Inside(c) || !captureReach[Idx(c)] || !captureReach[Idx(Mirror(c))])
                return false;
            int h = height[Idx(c)];
            for (int dz = -4; dz <= 4; dz++)
            {
                for (int dx = -4; dx <= 4; dx++)
                {
                    int x = c.x + dx, z = c.y + dz;
                    if (!Inside(x, z))
                        return false;
                    int i = Idx(x, z);
                    if (height[i] != h || water[i] || border[i] || reserved[i] || rampMask[i] || shore[i])
                        return false;
                }
            }
            return true;
        }
    }
}
