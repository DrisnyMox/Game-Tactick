using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

// Шаги BuildLayoutJob: горы по краю, базы, уровни высот и их сглаживание
public static partial class ArenaLayoutGenerator
{
    partial struct BuildLayoutJob
    {
        // ---------- Горы по краю ----------

        int EdgeDistance(int x, int z) => math.min(math.min(x, z), math.min(sx - 1 - x, sz - 1 - z));

        void BuildBorder()
        {
            // Ширина гор меняется по симметричному шуму — край карты неровный, с заливами
            for (int z = 0; z < sz; z++)
            {
                for (int x = 0; x < sx; x++)
                {
                    int i = Idx(x, z);
                    edge[i] = EdgeDistance(x, z);
                    float t = math.saturate(noiseMaps[i].x * 1.6f + 0.45f);
                    border[i] = edge[i] < math.lerp(s.borderMinWidth, s.borderMaxWidth, t);
                }
            }
        }

        // Горы не заходят на площадку базы и отступ вокруг неё
        void ClearBorderAroundBases()
        {
            ClearBorderAround(baseA);
            ClearBorderAround(baseB);
        }

        void ClearBorderAround(int2 b)
        {
            int r = baseHalf + 6;
            for (int z = b.y - r; z <= b.y + r; z++)
                for (int x = b.x - r; x <= b.x + r; x++)
                    if (Inside(x, z) && edge[Idx(x, z)] >= s.borderMinWidth)
                        border[Idx(x, z)] = false;
        }

        // ---------- Базы на случайной оси через центр ----------

        void PlaceBases()
        {
            baseHalf = s.wallLevel3Size / 2 + s.wallThickness + s.basePadding;

            float angle = (float)(rnd.NextDouble() * math.PI);
            axis = new float2(math.cos(angle), math.sin(angle));

            // Как можно дальше друг от друга, но площадка базы целиком внутри гор
            var center = new float2((sx - 1) * 0.5f, (sz - 1) * 0.5f);
            // За базой остаётся поле шириной baseBackSpace до самых широких гор
            float limitX = sx * 0.5f - s.borderMaxWidth - baseHalf - s.baseBackSpace;
            float limitZ = sz * 0.5f - s.borderMaxWidth - baseHalf - s.baseBackSpace;
            float dx = math.abs(axis.x) > 1e-3f ? limitX / math.abs(axis.x) : float.MaxValue;
            float dz = math.abs(axis.y) > 1e-3f ? limitZ / math.abs(axis.y) : float.MaxValue;
            baseDistance = math.min(dx, dz);

            baseA = (int2)math.round(center - axis * baseDistance);
            baseB = Mirror(baseA);

            // Ворота на стороне, которая больше всего смотрит на противника
            if (math.abs(axis.x) > math.abs(axis.y))
                gateSide = axis.x > 0 ? 1 : 3;
            else
                gateSide = axis.y > 0 ? 0 : 2;
        }

        // Поворот смещения: канонический +z (сторона ворот) в сторону gateSide
        int2 Rotate(int2 d)
        {
            return gateSide switch
            {
                0 => d,
                1 => new int2(d.y, -d.x),
                2 => new int2(-d.x, -d.y),
                _ => new int2(-d.y, d.x),
            };
        }

        // ---------- 1. Уровни ----------

        void BuildLevels()
        {
            // Пороги по долям площади внутри гор, чтобы соотношение уровней не зависело от сида
            var inner = new UnsafeList<float>(n, Allocator.Persistent);
            for (int i = 0; i < n; i++)
                if (!border[i])
                    inner.Add(noiseMaps[i].y);
            // Нужны только два значения отсортированного списка: выбор k-го без полной сортировки
            int k1 = math.clamp((int)(inner.Length * s.lowShare), 0, inner.Length - 1);
            int k2 = math.clamp((int)(inner.Length * (1f - s.highShare)), 0, inner.Length - 1);
            float t1 = SelectKth(ref inner, 0, inner.Length - 1, k1);
            // После выбора k1 всё правее не меньше: k2 ищется там, если он не левее
            float t2 = k2 >= k1 ? SelectKth(ref inner, k1, inner.Length - 1, k2) : SelectKth(ref inner, 0, k1, k2);
            inner.Dispose();

            for (int i = 0; i < n; i++)
            {
                float value = noiseMaps[i].y;
                level[i] = value < t1 ? 0 : value < t2 ? 1 : 2;
            }

            for (int z = 0; z < sz; z++)
            {
                for (int x = 0; x < sx; x++)
                {
                    bool inA = math.abs(x - baseA.x) <= baseHalf && math.abs(z - baseA.y) <= baseHalf;
                    bool inB = math.abs(x - baseB.x) <= baseHalf && math.abs(z - baseB.y) <= baseHalf;
                    baseMask[Idx(x, z)] = inA || inB;
                }
            }

            ForceLevels();
        }

        /// <summary>
        /// k-е по возрастанию значение среди a[lo..hi] (быстрый выбор Хоара). Переставляет элементы:
        /// после него левее k не больше a[k], правее — не меньше
        /// </summary>
        static float SelectKth(ref UnsafeList<float> a, int lo, int hi, int k)
        {
            while (lo < hi)
            {
                float pivot = a[(lo + hi) >> 1];
                int i = lo, j = hi;
                while (i <= j)
                {
                    while (a[i] < pivot) i++;
                    while (a[j] > pivot) j--;
                    if (i <= j)
                    {
                        (a[i], a[j]) = (a[j], a[i]);
                        i++;
                        j--;
                    }
                }
                // a[lo..j] <= pivot, a[i..hi] >= pivot, между ними — сам pivot
                if (k <= j)
                    hi = j;
                else if (k >= i)
                    lo = i;
                else
                    return a[k];
            }
            return a[k];
        }

        void ForceLevels()
        {
            for (int i = 0; i < n; i++)
            {
                if (baseMask[i])
                    level[i] = 1;
                if (border[i])
                    level[i] = 2;
            }
        }

        // ---------- 2. Сглаживание: самый частый уровень в окне 5x5 ----------

        // Счётчики уровней упакованы по байту на уровень (в окне не больше 25 клеток):
        // окно 5x5 считается суммой по строке, затем суммой строк по столбцу
        void SmoothLevels()
        {
            var next = new UnsafeList<int>(n, Allocator.Persistent);
            next.Resize(n);
            // Локальные копии: запись в списки не может задеть их, Burst не перечитывает поля джобы в цикле
            int w = sx, d = sz;
            var packedRows = rows;
            for (int iteration = 0; iteration < 3; iteration++)
            {
                var current = level;
                next.CopyFrom(current);
                for (int z = 0; z < d; z++)
                {
                    int row = z * w;
                    for (int x = 0; x < w; x++)
                    {
                        int packed = 0;
                        for (int dx = math.max(-2, -x); dx <= math.min(2, w - 1 - x); dx++)
                            packed += 1 << (8 * current[row + x + dx]);
                        packedRows[row + x] = packed;
                    }
                }

                for (int z = 0; z < d; z++)
                {
                    int from = math.max(-2, -z), to = math.min(2, d - 1 - z);
                    for (int x = 0; x < w; x++)
                    {
                        int i = z * w + x;
                        int packed = 0;
                        for (int dz = from; dz <= to; dz++)
                            packed += packedRows[i + dz * w];
                        var counts = new int3(packed & 0xFF, (packed >> 8) & 0xFF, packed >> 16);

                        int best = math.cmax(counts);
                        if (counts[current[i]] == best)
                            continue;

                        // При равенстве — меньший уровень (правило одно для обеих половин карты)
                        next[i] = counts.x == best ? 0 : counts.y == best ? 1 : 2;
                    }
                }
                (level, next) = (next, level);
                ForceLevels();
            }
            next.Dispose();
        }
    }
}
