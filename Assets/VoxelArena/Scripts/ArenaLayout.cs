using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

public enum ResourceKind
{
    /// <summary>Точка захвата с ценным ресурсом в поле (внутри стен источников нет)</summary>
    CapturePoint,
}

public struct ResourcePoint
{
    public int2 cell;
    public ResourceKind kind;
    /// <summary>0 — база один, 1 — база два, -1 — ничья (точка захвата)</summary>
    public int team;
}

public struct WallCell
{
    public int2 cell;
    public bool outer;  // внешний ряд — на нём зубцы
}

/// <summary>
/// Раскладка карты по столбцам (x, z): высоты, вода, рампы, базы, ресурсы, деревья, камни.
/// Индекс столбца: z * sizeX + x. Все данные в нативной памяти — освобождаются Dispose.
/// </summary>
public sealed class ArenaLayout : IDisposable
{
    public const byte FlagSand = 1;   // сверху песок (берег, дно озера)
    public const byte FlagCliff = 2;  // край обрыва: под верхним блоком сразу камень
    public const byte FlagRamp = 4;
    public const byte FlagRock = 8;       // горы по краю: камень до самого верха
    public const byte FlagSoftTop = 16;   // пологий склон горы: сверху трава

    public readonly int sizeX;
    public readonly int sizeZ;
    public NativeArray<int> height;    // число сплошных блоков в столбце
    public NativeArray<int> waterTop;  // 0 — нет воды, иначе вода стоит до этого y (не включая)
    public NativeArray<byte> flags;

    public int2 baseOne;
    public int2 baseTwo;
    /// <summary>Сторона ворот базы один: 0 +z, 1 +x, 2 -z, 3 -x (у базы два — противоположная)</summary>
    public int gateSide;
    /// <summary>
    /// Внутри стен какого уровня лежит клетка: 0 — ни в каких, 1..3 — база один, 5..7 — база два (4 + уровень).
    /// Хранится наименьший уровень, в который клетка попадает.
    /// </summary>
    public NativeArray<byte> baseZone;
    /// <summary>Ровная площадка базы (под стены 3-го уровня и отступ): 0 — нет, 1 — база один, 2 — база два</summary>
    public NativeArray<byte> baseArea;
    /// <summary>Направление от базы один к базе два (единичный вектор в плоскости x, z)</summary>
    public float2 baseAxis;
    /// <summary>Стены 1-го уровня обеих баз</summary>
    public NativeList<WallCell> walls;
    /// <summary>Клетка сразу снаружи ворот: [0] — база один, [1] — база два</summary>
    public NativeList<int2> gates;
    public NativeList<ResourcePoint> resources;
    public NativeList<int2> trees;
    public NativeList<int2> rocks;

    // Для лога
    public int regionCount;
    public int rampCount;
    public int flattenedRegions;
    public bool basesConnected;
    public bool capturePointsReachable;

    public ArenaLayout(int sizeX, int sizeZ)
    {
        this.sizeX = sizeX;
        this.sizeZ = sizeZ;
        int n = sizeX * sizeZ;
        height = new NativeArray<int>(n, Allocator.Persistent);
        waterTop = new NativeArray<int>(n, Allocator.Persistent);
        flags = new NativeArray<byte>(n, Allocator.Persistent);
        baseZone = new NativeArray<byte>(n, Allocator.Persistent);
        baseArea = new NativeArray<byte>(n, Allocator.Persistent);
        walls = new NativeList<WallCell>(Allocator.Persistent);
        gates = new NativeList<int2>(2, Allocator.Persistent);
        resources = new NativeList<ResourcePoint>(Allocator.Persistent);
        trees = new NativeList<int2>(Allocator.Persistent);
        rocks = new NativeList<int2>(Allocator.Persistent);
    }

    public void Dispose()
    {
        height.Dispose();
        waterTop.Dispose();
        flags.Dispose();
        baseZone.Dispose();
        baseArea.Dispose();
        walls.Dispose();
        gates.Dispose();
        resources.Dispose();
        trees.Dispose();
        rocks.Dispose();
    }
}

/// <summary>
/// Генератор раскладки: сначала структура (горы по краю, уровни, базы, вода, рампы по графу
/// связности), потом коридоры и детали. Карта симметрична поворотом на 180° вокруг центра.
/// Шаги 1:1 повторяют прототип на Python, на котором проверялась проходимость.
/// Карты шума считаются параллельной джобой, остальное — одной Burst-джобой
/// (шаги идут друг за другом и зависят от предыдущих).
/// </summary>
public static class ArenaLayoutGenerator
{
    // Выход джобы, который не помещается в ArenaLayout напрямую
    const int InfoBaseAx = 0, InfoBaseAz = 1, InfoBaseBx = 2, InfoBaseBz = 3, InfoGateSide = 4,
              InfoRegions = 5, InfoRamps = 6, InfoFlattened = 7, InfoConnected = 8, InfoCaptures = 9, InfoCount = 10;

    public static ArenaLayout Generate(ArenaGenSettings settings, ArenaBiome biome, int sizeX, int sizeY, int sizeZ)
    {
        var layout = new ArenaLayout(sizeX, sizeZ);
        int n = sizeX * sizeZ;

        var random = new Unity.Mathematics.Random(math.hash(new int2(settings.seed, 7)) | 1u);
        var levelOffset = random.NextFloat2(new float2(-5000f), new float2(5000f));
        var waterOffset = random.NextFloat2(new float2(-5000f), new float2(5000f));
        var forestOffset = random.NextFloat2(new float2(-5000f), new float2(5000f));
        var mountainOffset = random.NextFloat2(new float2(-5000f), new float2(5000f));
        var borderOffset = random.NextFloat2(new float2(-5000f), new float2(5000f));

        var noiseMaps = new NativeArray<float4>(n, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
        var info = new NativeArray<int>(InfoCount, Allocator.TempJob);
        var axis = new NativeArray<float2>(1, Allocator.TempJob);

        var noiseHandle = new SymmetricNoiseJob
        {
            settings = settings,
            sizeX = sizeX,
            sizeZ = sizeZ,
            borderOffset = borderOffset,
            levelOffset = levelOffset,
            waterOffset = waterOffset,
            forestOffset = forestOffset,
            maps = noiseMaps,
        }.Schedule((n + 1) / 2, 256);

        new BuildLayoutJob
        {
            s = settings,
            waterBonus = ArenaBiomes.WaterThresholdBonus(biome),
            forestScale = ArenaBiomes.ForestDensityScale(biome),
            rockScale = ArenaBiomes.RockChanceScale(biome),
            sx = sizeX,
            sy = sizeY,
            sz = sizeZ,
            mountainOffset = mountainOffset,
            noiseMaps = noiseMaps,
            outHeight = layout.height,
            outWaterTop = layout.waterTop,
            outFlags = layout.flags,
            outBaseZone = layout.baseZone,
            outBaseArea = layout.baseArea,
            outWalls = layout.walls,
            outGates = layout.gates,
            outResources = layout.resources,
            outTrees = layout.trees,
            outRocks = layout.rocks,
            outInfo = info,
            outAxis = axis,
        }.Schedule(noiseHandle).Complete();

        layout.baseOne = new int2(info[InfoBaseAx], info[InfoBaseAz]);
        layout.baseTwo = new int2(info[InfoBaseBx], info[InfoBaseBz]);
        layout.gateSide = info[InfoGateSide];
        layout.baseAxis = axis[0];
        layout.regionCount = info[InfoRegions];
        layout.rampCount = info[InfoRamps];
        layout.flattenedRegions = info[InfoFlattened];
        layout.basesConnected = info[InfoConnected] != 0;
        layout.capturePointsReachable = info[InfoCaptures] != 0;

        noiseMaps.Dispose();
        info.Dispose();
        axis.Dispose();
        return layout;
    }

    static float Fbm(float2 p, float scale, int octaves, float2 offset)
    {
        float sum = 0f, amplitude = 1f, norm = 0f, frequency = scale;
        for (int o = 0; o < octaves; o++)
        {
            sum += noise.snoise(p * frequency + offset) * amplitude;
            norm += amplitude;
            amplitude *= 0.5f;
            frequency *= 2f;
        }
        return sum / norm;
    }

    /// <summary>
    /// Четыре карты шума на каждую клетку: x — ширина гор, y — уровни, z — вода, w — лес.
    /// Каждая — среднее двух зеркальных точек, симметричная непрерывная функция: у зеркальной клетки
    /// (индекс n - 1 - i) значения те же, джоба считает половину карты и пишет обе клетки.
    /// Strict: без перестановок и слияния операций с float — в редакторе (Burst) и в WebGL (без Burst)
    /// пороги шума срабатывают одинаково, карта по сиду та же
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Strict)]
    struct SymmetricNoiseJob : IJobParallelFor
    {
        public ArenaGenSettings settings;
        public int sizeX, sizeZ;
        public float2 borderOffset, levelOffset, waterOffset, forestOffset;
        [WriteOnly, NativeDisableParallelForRestriction] public NativeArray<float4> maps;

        public void Execute(int i)
        {
            int x = i % sizeX, z = i / sizeX;
            var p = new float2(x, z);
            var q = new float2(sizeX - 1 - x, sizeZ - 1 - z);
            // a + b == b + a точно: у зеркальной клетки p и q меняются местами, сумма та же
            maps[i] = maps[maps.Length - 1 - i] = new float4(
                Symmetric(p, q, settings.borderNoiseScale, 3, borderOffset),
                Symmetric(p, q, settings.levelNoiseScale, 3, levelOffset),
                Symmetric(p, q, settings.waterNoiseScale, 2, waterOffset),
                Symmetric(p, q, settings.forestNoiseScale, 2, forestOffset));
        }

        static float Symmetric(float2 p, float2 q, float scale, int octaves, float2 offset) =>
            0.5f * (Fbm(p, scale, octaves, offset) + Fbm(q, scale, octaves, offset));
    }

    struct RampSite
    {
        public int x, z, dx, dz;  // (x, z) — нижняя клетка у обрыва, d — шаг к верхней
    }

    struct RampCell
    {
        public int index;
        public bool lowSide;
        public int height;
    }

    // Место рампы между парой участков: key — (нижний участок, верхний участок), order — порядок обхода карты
    struct PairSite : IComparable<PairSite>
    {
        public long key;
        public int order;
        public RampSite site;

        public int CompareTo(PairSite other) => key != other.key ? key.CompareTo(other.key) : order.CompareTo(other.order);
    }

    [BurstCompile(FloatMode = FloatMode.Strict)]
    struct BuildLayoutJob : IJob
    {
        public ArenaGenSettings s;
        public float waterBonus, forestScale, rockScale;
        public int sx, sy, sz;
        public float2 mountainOffset;
        [ReadOnly] public NativeArray<float4> noiseMaps;

        public NativeArray<int> outHeight;
        public NativeArray<int> outWaterTop;
        public NativeArray<byte> outFlags;
        public NativeArray<byte> outBaseZone;
        public NativeArray<byte> outBaseArea;
        public NativeList<WallCell> outWalls;
        public NativeList<int2> outGates;
        public NativeList<ResourcePoint> outResources;
        public NativeList<int2> outTrees;
        public NativeList<int2> outRocks;
        public NativeArray<int> outInfo;
        public NativeArray<float2> outAxis;

        int n;
        DotNetRandom rnd;

        UnsafeList<int> level, edge, mountainDepth, height, comp, parent, queue, previous;
        UnsafeList<bool> border, baseMask, reserved, water, shore, rampMask, wallMask, corridor;
        UnsafeList<bool> land, captureReach, seen, cliff, blocked, occupied, walkable;
        // Промежуточный проход по строкам для окон вокруг клетки (сглаживание, вода, проходимость)
        UnsafeList<int> rows;
        UnsafeList<int> compSize, compCell;
        UnsafeList<int2> captures;
        UnsafeList<RampCell> cellsA, cellsB;

        int2 baseA, baseB;
        float2 axis;       // от A к B
        int gateSide;      // 0: +z, 1: +x, 2: -z, 3: -x
        int baseHalf;      // полуразмер ровной площадки базы
        float baseDistance;
        int2 gateOutA, gateOutB;
        int rampCount;

        static UnsafeList<T> Cells<T>(int count) where T : unmanaged
        {
            var list = new UnsafeList<T>(count, Allocator.Persistent);
            list.Resize(count, NativeArrayOptions.ClearMemory);
            return list;
        }

        // Burst сводит такой цикл к заполнению памяти
        static void Clear<T>(UnsafeList<T> list) where T : unmanaged
        {
            for (int i = 0; i < list.Length; i++)
                list[i] = default;
        }

        public void Execute()
        {
            n = sx * sz;
            rnd = new DotNetRandom(s.seed);

            level = Cells<int>(n); edge = Cells<int>(n); mountainDepth = Cells<int>(n); height = Cells<int>(n);
            comp = Cells<int>(n); queue = Cells<int>(n); previous = Cells<int>(n);
            parent = new UnsafeList<int>(64, Allocator.Persistent);
            border = Cells<bool>(n); baseMask = Cells<bool>(n); reserved = Cells<bool>(n); water = Cells<bool>(n);
            shore = Cells<bool>(n); rampMask = Cells<bool>(n); wallMask = Cells<bool>(n); corridor = Cells<bool>(n);
            land = Cells<bool>(n); captureReach = Cells<bool>(n); seen = Cells<bool>(n); cliff = Cells<bool>(n);
            blocked = Cells<bool>(n); occupied = Cells<bool>(n); walkable = Cells<bool>(n);
            rows = Cells<int>(n);
            compSize = new UnsafeList<int>(64, Allocator.Persistent);
            compCell = new UnsafeList<int>(64, Allocator.Persistent);
            captures = new UnsafeList<int2>(8, Allocator.Persistent);
            cellsA = new UnsafeList<RampCell>(64, Allocator.Persistent);
            cellsB = new UnsafeList<RampCell>(64, Allocator.Persistent);

            Run();

            level.Dispose(); edge.Dispose(); mountainDepth.Dispose(); height.Dispose();
            comp.Dispose(); queue.Dispose(); previous.Dispose(); parent.Dispose();
            border.Dispose(); baseMask.Dispose(); reserved.Dispose(); water.Dispose();
            shore.Dispose(); rampMask.Dispose(); wallMask.Dispose(); corridor.Dispose();
            land.Dispose(); captureReach.Dispose(); seen.Dispose(); cliff.Dispose();
            blocked.Dispose(); occupied.Dispose(); walkable.Dispose(); rows.Dispose();
            compSize.Dispose(); compCell.Dispose(); captures.Dispose(); cellsA.Dispose(); cellsB.Dispose();
        }

        int Idx(int x, int z) => z * sx + x;
        int Idx(int2 c) => c.y * sx + c.x;
        bool Inside(int x, int z) => x >= 0 && z >= 0 && x < sx && z < sz;
        bool Inside(int2 c) => Inside(c.x, c.y);
        int Mirror(int i) => n - 1 - i;
        int2 Mirror(int2 c) => new(sx - 1 - c.x, sz - 1 - c.y);
        int LevelHeight(int l) => l == 0 ? s.lowHeight : l == 1 ? s.midHeight : s.highHeight;

        static int2 Dir(int k) => k switch
        {
            0 => new int2(1, 0),
            1 => new int2(-1, 0),
            2 => new int2(0, 1),
            _ => new int2(0, -1),
        };

        void Run()
        {
            BuildBorder();
            PlaceBases();
            ClearBorderAroundBases();
            BuildLevels();
            SmoothLevels();
            RemoveSmallRegions();
            PlaceBaseContent();
            PlaceWater();

            int flattened = 0;
            bool connected = false;
            for (int attempt = 0; attempt < 6 && !connected; attempt++)
                connected = PlaceRamps(ref flattened);

            // Точки захвата — после рамп: выбираются только там, куда от ворот можно дойти
            PlaceCapturePoints();
            BuildMountains();
            BuildCorridors();

            outInfo[InfoBaseAx] = baseA.x;
            outInfo[InfoBaseAz] = baseA.y;
            outInfo[InfoBaseBx] = baseB.x;
            outInfo[InfoBaseBz] = baseB.y;
            outInfo[InfoGateSide] = gateSide;
            outInfo[InfoRegions] = compSize.Length;
            outInfo[InfoRamps] = rampCount;
            outInfo[InfoFlattened] = flattened;
            outAxis[0] = axis;
            outGates.Add(gateOutA);
            outGates.Add(gateOutB);

            PlaceTreesAndRocks();
            FillLayout();
            FillBaseZones();
            CheckReachability();
        }

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
