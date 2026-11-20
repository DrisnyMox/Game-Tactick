using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

// Шаги BuildLayoutJob: проходимость по сетке, горы, коридоры между воротами и к точкам захвата
public static partial class ArenaLayoutGenerator
{
    partial struct BuildLayoutJob
    {
        // ---------- Проходимость по сетке ----------

        /// <summary>
        /// Полуширина прохода в клетках: проход должен быть шириной минимум 2·r+1 клеток. Считается
        /// по самому широкому агенту — технике (танк ~2 м): навмеш сужает проходы на радиус агента
        /// с каждой стороны, и более узкие места для неё закрываются.
        /// </summary>
        const int ClearanceRadius = 2;
        // Коридор без деревьев: проход техники плюс полуразмер дерева (дерево — препятствие 5×5)
        const int CorridorRadius = ClearanceRadius + 2;
        // Центр точки захвата занят рудным бугром: обход доходит до клетки не дальше этого
        const int CaptureReachRadius = 6;

        /// <summary>
        /// Обход от старта: шаг по высоте не больше блока. Клетка проходима, если в квадрате
        /// ClearanceRadius вокруг неё нет препятствий и обрывов (перепад больше блока между соседями).
        /// Достигнутые клетки — в reached; withPrevious — запомнить, откуда пришли (previous).
        /// </summary>
        void Walk(int2 start, UnsafeList<bool> obstacles, UnsafeList<bool> reached, bool withPrevious)
        {
            Clear(reached);
            if (withPrevious)
                for (int i = 0; i < n; i++)
                    previous[i] = -1;
            MarkCliffs();
            MarkWalkable(obstacles);

            int head = 0, tail = 0;
            int s0 = Idx(start);
            queue[tail++] = s0;
            reached[s0] = true;

            while (head < tail)
            {
                int i = queue[head++];
                int x = i % sx, z = i / sx;
                for (int k = 0; k < 4; k++)
                {
                    var d = Dir(k);
                    int xx = x + d.x, zz = z + d.y;
                    if (!Inside(xx, zz))
                        continue;
                    int j = Idx(xx, zz);
                    if (reached[j] || math.abs(height[j] - height[i]) > 1 || !walkable[j])
                        continue;
                    reached[j] = true;
                    if (withPrevious)
                        previous[j] = i;
                    queue[tail++] = j;
                }
            }
        }

        // Обрыв: у клетки есть сосед выше или ниже больше чем на блок (рампа — ступени по блоку)
        void MarkCliffs()
        {
            for (int z = 0; z < sz; z++)
            {
                for (int x = 0; x < sx; x++)
                {
                    int i = Idx(x, z);
                    bool c = false;
                    if (x > 0) c |= math.abs(height[i] - height[i - 1]) > 1;
                    if (x < sx - 1) c |= math.abs(height[i] - height[i + 1]) > 1;
                    if (z > 0) c |= math.abs(height[i] - height[i - sx]) > 1;
                    if (z < sz - 1) c |= math.abs(height[i] - height[i + sx]) > 1;
                    cliff[i] = c;
                }
            }
        }

        // Клетка проходима, если квадрат ClearanceRadius вокруг неё целиком на карте и в нём нет воды, гор,
        // препятствий и обрывов. Считается для всей карты один раз на обход: сначала строка по x, затем столбец
        void MarkWalkable(UnsafeList<bool> obstacles)
        {
            const int r = ClearanceRadius;
            for (int z = 0; z < sz; z++)
            {
                for (int x = 0; x < sx; x++)
                {
                    bool ok = x >= r && x < sx - r;
                    for (int dx = -r; dx <= r && ok; dx++)
                    {
                        int j = Idx(x + dx, z);
                        ok = !(water[j] || border[j] || obstacles[j] || cliff[j]);
                    }
                    rows[Idx(x, z)] = ok ? 1 : 0;
                }
            }

            for (int z = 0; z < sz; z++)
            {
                for (int x = 0; x < sx; x++)
                {
                    bool ok = z >= r && z < sz - r;
                    for (int dz = -r; dz <= r && ok; dz++)
                        ok = rows[Idx(x, z + dz)] != 0;
                    walkable[Idx(x, z)] = ok;
                }
            }
        }

        // Ближайшая к цели клетка, до которой дошёл обход (центр точки захвата занят бугром)
        int NearestSeen(int2 goal, UnsafeList<bool> reached, int radius)
        {
            for (int r = 0; r <= radius; r++)
            {
                for (int dz = -r; dz <= r; dz++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        var c = goal + new int2(dx, dz);
                        if (Inside(c) && reached[Idx(c)])
                            return Idx(c);
                    }
                }
            }
            return -1;
        }

        // ---------- Горы ----------

        void BuildMountains()
        {
            // Расстояние вглубь гор от подножия
            int head = 0, tail = 0;
            for (int i = 0; i < n; i++)
            {
                if (!border[i])
                    continue;
                int x = i % sx, z = i / sx;
                for (int k = 0; k < 4; k++)
                {
                    var d = Dir(k);
                    int xx = x + d.x, zz = z + d.y;
                    if (Inside(xx, zz) && !border[Idx(xx, zz)])
                    {
                        mountainDepth[i] = 1;
                        queue[tail++] = i;
                        break;
                    }
                }
            }
            while (head < tail)
            {
                int i = queue[head++];
                int x = i % sx, z = i / sx;
                for (int k = 0; k < 4; k++)
                {
                    var d = Dir(k);
                    int xx = x + d.x, zz = z + d.y;
                    if (!Inside(xx, zz))
                        continue;
                    int j = Idx(xx, zz);
                    if (border[j] && mountainDepth[j] == 0)
                    {
                        mountainDepth[j] = mountainDepth[i] + 1;
                        queue[tail++] = j;
                    }
                }
            }

            // Подъём от подножия, гребни (ridged noise) и отдельные вершины
            for (int i = 0; i < n; i++)
            {
                if (!border[i])
                    continue;
                var p = new float2(i % sx, i / sx);
                float t = math.saturate((float)mountainDepth[i] / s.mountainRise);
                t = t * t * (3f - 2f * t);
                float ridge = 1f - math.abs(noise.snoise(p * 0.05f + mountainOffset));
                float peaks = noise.snoise(p * 0.12f + mountainOffset * 1.7f);
                float h = s.highHeight + 1 + t * (s.mountainHeight * (0.3f + 0.7f * ridge)) + peaks * 3f;
                height[i] = math.clamp((int)math.round(h), s.highHeight + 1, sy - 3);
            }
        }

        // ---------- 7. Коридоры: от ворот к воротам противника и к каждой точке захвата ----------

        void BuildCorridors()
        {
            Walk(gateOutA, wallMask, seen, true);

            MarkPath(NearestSeen(gateOutB, seen, CaptureReachRadius));
            for (int k = 0; k < captures.Length; k++)
                MarkPath(NearestSeen(captures[k], seen, CaptureReachRadius));
        }

        void MarkPath(int cur)
        {
            while (cur >= 0)
            {
                MarkCorridor(cur);
                MarkCorridor(Mirror(cur));
                cur = previous[cur];
            }
        }

        void MarkCorridor(int i)
        {
            int x = i % sx, z = i / sx;
            for (int dz = -CorridorRadius; dz <= CorridorRadius; dz++)
                for (int dx = -CorridorRadius; dx <= CorridorRadius; dx++)
                    if (Inside(x + dx, z + dz))
                        corridor[Idx(x + dx, z + dz)] = true;
        }
    }
}
