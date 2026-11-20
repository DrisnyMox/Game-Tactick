using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

/// <summary>
/// Генератор раскладки: сначала структура (горы по краю, уровни, базы, вода, рампы по графу
/// связности), потом коридоры и детали. Карта симметрична поворотом на 180° вокруг центра.
/// Шаги 1:1 повторяют прототип на Python, на котором проверялась проходимость.
/// Карты шума считаются параллельной джобой, остальное — одной Burst-джобой
/// (шаги идут друг за другом и зависят от предыдущих).
/// </summary>
public static partial class ArenaLayoutGenerator
{
    // Выход джобы, который не помещается в ArenaLayout напрямую
    internal const int InfoBaseAx = 0, InfoBaseAz = 1, InfoBaseBx = 2, InfoBaseBz = 3, InfoGateSide = 4,
              InfoRegions = 5, InfoRamps = 6, InfoFlattened = 7, InfoConnected = 8, InfoCaptures = 9, InfoCount = 10;

    /// <summary>Раскладка, джобы которой запущены: Finish ждёт их и отдаёт готовую раскладку</summary>
    public sealed class Pending
    {
        internal ArenaLayout layout;
        internal NativeArray<float4> noiseMaps;
        internal NativeArray<int> info;
        internal NativeArray<float2> axis;
        internal JobHandle handle;

        public bool IsCompleted => handle.IsCompleted;

        public ArenaLayout Finish()
        {
            handle.Complete();
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
    }

    public static ArenaLayout Generate(ArenaGenSettings settings, ArenaBiome biome, int sizeX, int sizeY, int sizeZ, int cellVoxels = 2) =>
        Schedule(settings, biome, sizeX, sizeY, sizeZ, cellVoxels).Finish();

    /// <summary>
    /// Запускает джобы раскладки и сразу возвращается. Память джоб — Persistent: раскладку можно ждать
    /// много кадров (экран загрузки), а TempJob живёт не больше четырёх
    /// </summary>
    public static Pending Schedule(ArenaGenSettings settings, ArenaBiome biome, int sizeX, int sizeY, int sizeZ, int cellVoxels)
    {
        if (settings.shape == ArenaShape.Island)
            return IslandLayout.Schedule(settings, sizeX, sizeY, sizeZ, cellVoxels);

        var layout = new ArenaLayout(sizeX, sizeZ);
        int n = sizeX * sizeZ;

        var random = new Unity.Mathematics.Random(math.hash(new int2(settings.seed, 7)) | 1u);
        var levelOffset = random.NextFloat2(new float2(-5000f), new float2(5000f));
        var waterOffset = random.NextFloat2(new float2(-5000f), new float2(5000f));
        var forestOffset = random.NextFloat2(new float2(-5000f), new float2(5000f));
        var mountainOffset = random.NextFloat2(new float2(-5000f), new float2(5000f));
        var borderOffset = random.NextFloat2(new float2(-5000f), new float2(5000f));

        var noiseMaps = new NativeArray<float4>(n, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        var info = new NativeArray<int>(InfoCount, Allocator.Persistent);
        var axis = new NativeArray<float2>(1, Allocator.Persistent);

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

        var handle = new BuildLayoutJob
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
        }.Schedule(noiseHandle);
        JobHandle.ScheduleBatchedJobs();

        return new Pending { layout = layout, noiseMaps = noiseMaps, info = info, axis = axis, handle = handle };
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
    partial struct BuildLayoutJob : IJob
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
    }
}
