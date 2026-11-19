using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

/// <summary>
/// Раскладка архипелага для вылазки (ArenaShape.Island): Бирюзовые Пределы из лора.
/// Крупные острова, между ними мелководье. Вода стоит до SeaLevel клеток минус один воксель
/// (ArenaLayout.waterDropVoxels, в вокселях — seaTop): у берега воды 1 воксель, дальше до 3,
/// персонаж ростом 8 вокселей бегает по дну. Глубоко только в кольце у края карты.
///  * Острова — пятна крупного шума; к краю карты шум гаснет, там открытая вода.
///  * Высоты — в вокселях с дробной частью (ArenaLayout.surface): берег у воды вровень с ней, дальше суша
///    поднимается на воксель через каждые 8 вокселей от воды, до 3 над водой. Дно так же уходит до 3 вокселей.
///  * Причалы (ResourceKind.Landing): 6 лучей от края карты к середине, первый берег на луче. Первый причал —
///    высадка игрока (baseOne), самый дальний от него — baseTwo. Ворота (gates) — 10 клеток от причала в сторону суши.
///  * Ржавые остовы (Wreck) — 3 на мелководье, руины (Ruin) — 5 на островах, на ровной площадке 17×17 клеток.
///  * Постройки ArenaDecorator пишет вокселями под рост персонажа (8 вокселей), а не клетками.
///  * Пальмы на островах, на больших островах вперемешку с лиственными деревьями; валуны на суше и в воде.
/// Баз, стен, рамп нет. height — та же высота в клетках с округлением вниз (для построек в клетках и проверок воды).
/// </summary>
public static class IslandLayout
{
    /// <summary>Вода стоит до этой высоты (клетки раскладки) без одного вокселя</summary>
    public const int SeaLevel = 8;
    const int WaterDrop = 1;

    public static ArenaLayoutGenerator.Pending Schedule(ArenaGenSettings settings, int sizeX, int sizeY, int sizeZ, int cellVoxels)
    {
        var layout = new ArenaLayout(sizeX, sizeZ)
        {
            seaLevel = SeaLevel,
            waterDropVoxels = WaterDrop,
            wadeableWater = true,
            grassRaiseVoxels = 1,
            fineSurface = true,
        };
        var random = new Random(math.hash(new int2(settings.seed, 11)) | 1u);
        var info = new NativeArray<int>(ArenaLayoutGenerator.InfoCount, Allocator.Persistent);
        var axis = new NativeArray<float2>(1, Allocator.Persistent);
        // Pending освобождает карты шума арены; архипелагу они не нужны
        var noiseMaps = new NativeArray<float4>(1, Allocator.Persistent);

        var handle = new IslandJob
        {
            sx = sizeX,
            sz = sizeZ,
            k = cellVoxels,
            seaTop = SeaLevel * cellVoxels - WaterDrop,
            seed = random.NextUInt() | 1u,
            islandOffset = random.NextFloat2(-5000f, 5000f),
            densityOffset = random.NextFloat2(-5000f, 5000f),
            grassOffset = random.NextFloat2(-5000f, 5000f),
            outSurface = layout.surface,
            outHeight = layout.height,
            outWaterTop = layout.waterTop,
            outFlags = layout.flags,
            outGates = layout.gates,
            outResources = layout.resources,
            outTrees = layout.trees,
            outRocks = layout.rocks,
            outInfo = info,
            outAxis = axis,
        }.Schedule();
        JobHandle.ScheduleBatchedJobs();

        return new ArenaLayoutGenerator.Pending { layout = layout, noiseMaps = noiseMaps, info = info, axis = axis, handle = handle };
    }

    // Strict: архипелаг по сиду один и тот же в редакторе (Burst) и в WebGL (без Burst)
    [BurstCompile(FloatMode = FloatMode.Strict)]
    struct IslandJob : IJob
    {
        // Ширина кольца у края карты, где вода глубокая, и докуда от края острова редеют, в клетках
        const int DeepRing = 22;
        const int FadeTo = 60;
        // Острова меньше этого (клеток) всегда только из песка
        const int MinGrassIsland = 200;
        // Уступ в 1 воксель — через StepVoxels вокселей от воды (8 — рост персонажа): суша поднимается, дно уходит вниз.
        // Выше MaxLand вокселей над водой и глубже MaxDepth вокселей воды не бывает
        const float StepVoxels = 8f, MaxLand = 3f, MaxDepth = 3f, EdgeDepth = 9f;

        public int sx, sz;
        // Вокселей в клетке; верх воды в вокселях: вода стоит в вокселях ниже seaTop
        public int k, seaTop;
        public uint seed;
        public float2 islandOffset, densityOffset, grassOffset;

        public NativeArray<float> outSurface;
        public NativeArray<int> outHeight;
        public NativeArray<int> outWaterTop;
        public NativeArray<byte> outFlags;
        public NativeList<int2> outGates;
        public NativeList<ResourcePoint> outResources;
        public NativeList<int2> outTrees;
        public NativeList<int2> outRocks;
        public NativeArray<int> outInfo;
        public NativeArray<float2> outAxis;

        Random rnd;

        int Idx(int x, int z) => z * sx + x;
        bool Inside(int2 c) => c.x >= 0 && c.y >= 0 && c.x < sx && c.y < sz;
        bool IsWater(int i) => outWaterTop[i] > outHeight[i];

        void Spread(int i, int id, NativeArray<int> island, NativeList<int> stack)
        {
            if (island[i] != 0 || IsWater(i))
                return;
            island[i] = id;
            stack.Add(i);
        }
        int EdgeDistance(int x, int z) => math.min(math.min(x, z), math.min(sx - 1 - x, sz - 1 - z));

        public void Execute()
        {
            int n = sx * sz;
            // Сколько суши: меньше 0 — вода, 0 — берег, внутри острова растёт.
            // Временные массивы — параметрами, а не полями: незаполненное поле-массив джобы не даёт её запустить
            var shape = new NativeArray<float>(n, Allocator.Temp);
            var occupied = new NativeArray<bool>(n, Allocator.Temp);
            rnd = new Random(seed);

            Terrain(shape);
            Sand(shape);

            var landings = new NativeList<int2>(8, Allocator.Temp);
            PlaceLandings(landings, occupied);
            // Луч не нашёл берега ни разу (почти пустая карта): высадка на ближайшей к середине суше
            if (landings.Length == 0)
                landings.Add(NearestLand());
            PlaceWrecks(landings, occupied);
            PlaceRuins(landings, occupied);
            PlaceVegetation(shape, occupied);

            // База один — высадка игрока, база два — самый дальний причал, ворота — в сторону суши
            int2 one = landings[0], two = landings[0];
            float best = -1f;
            for (int k = 1; k < landings.Length; k++)
            {
                float d = math.distancesq((float2)landings[k], (float2)one);
                if (d > best)
                {
                    best = d;
                    two = landings[k];
                }
            }
            outGates.Add(Inland(one));
            outGates.Add(Inland(two));
            outInfo[ArenaLayoutGenerator.InfoBaseAx] = one.x;
            outInfo[ArenaLayoutGenerator.InfoBaseAz] = one.y;
            outInfo[ArenaLayoutGenerator.InfoBaseBx] = two.x;
            outInfo[ArenaLayoutGenerator.InfoBaseBz] = two.y;
            outInfo[ArenaLayoutGenerator.InfoRegions] = 1;
            outInfo[ArenaLayoutGenerator.InfoConnected] = 1;
            outInfo[ArenaLayoutGenerator.InfoCaptures] = 1;
            outAxis[0] = math.normalizesafe((float2)(two - one));
        }

        // Острова — пятна крупного шума, местами гуще (второй, ещё крупнее). Высота в вокселях — от расстояния до берега:
        // первые StepVoxels вокселей суши вровень с водой, дальше на каждые StepVoxels — воксель выше, до MaxLand.
        // Дно так же: у берега воды 1 воксель, на каждые StepVoxels — воксель глубже, до MaxDepth. У края карты глубоко
        void Terrain(NativeArray<float> shape)
        {
            int n = sx * sz;
            var toWater = new NativeArray<float>(n, Allocator.Temp);
            var toLand = new NativeArray<float>(n, Allocator.Temp);
            for (int z = 0; z < sz; z++)
            {
                for (int x = 0; x < sx; x++)
                {
                    int i = Idx(x, z);
                    var p = new float2(x, z);
                    int edge = EdgeDistance(x, z);
                    float density = Fbm(p, 0.004f, 2, densityOffset) * 0.12f;
                    float s = Fbm(p, 0.011f, 4, islandOffset) - 0.1f + density;
                    s -= math.saturate((FadeTo - edge) / (float)(FadeTo - DeepRing)) * 0.7f;
                    shape[i] = s;
                    toWater[i] = s < 0f ? 0f : float.MaxValue;
                    toLand[i] = s < 0f ? float.MaxValue : 0f;
                }
            }
            Chamfer(toWater);
            Chamfer(toLand);

            for (int z = 0; z < sz; z++)
            {
                for (int x = 0; x < sx; x++)
                {
                    int i = Idx(x, z);
                    // Расстояние от середины клетки до берега в вокселях: берег — посередине между клетками суши и воды
                    float surface;
                    if (shape[i] >= 0f)
                    {
                        float fromShore = (toWater[i] - 0.5f) * k;
                        surface = seaTop + math.min(fromShore / StepVoxels, MaxLand) - 0.49f;
                    }
                    else if (EdgeDistance(x, z) < DeepRing)
                    {
                        surface = seaTop - EdgeDepth;
                    }
                    else
                    {
                        float fromShore = (toLand[i] - 0.5f) * k;
                        surface = seaTop - math.min(1f + fromShore / StepVoxels, MaxDepth) + 0.49f;
                    }
                    SetSurface(i, surface);
                }
            }
        }

        // Расстояние в клетках до ближайшей клетки с 0 (проход вперёд и назад, по диагонали √2)
        void Chamfer(NativeArray<float> dist)
        {
            const float Diagonal = 1.4142135f;
            for (int z = 0; z < sz; z++)
                for (int x = 0; x < sx; x++)
                {
                    int i = Idx(x, z);
                    float d = dist[i];
                    if (x > 0) d = math.min(d, dist[i - 1] + 1f);
                    if (z > 0) d = math.min(d, dist[i - sx] + 1f);
                    if (x > 0 && z > 0) d = math.min(d, dist[i - sx - 1] + Diagonal);
                    if (x < sx - 1 && z > 0) d = math.min(d, dist[i - sx + 1] + Diagonal);
                    dist[i] = d;
                }
            for (int z = sz - 1; z >= 0; z--)
                for (int x = sx - 1; x >= 0; x--)
                {
                    int i = Idx(x, z);
                    float d = dist[i];
                    if (x < sx - 1) d = math.min(d, dist[i + 1] + 1f);
                    if (z < sz - 1) d = math.min(d, dist[i + sx] + 1f);
                    if (x < sx - 1 && z < sz - 1) d = math.min(d, dist[i + sx + 1] + Diagonal);
                    if (x > 0 && z < sz - 1) d = math.min(d, dist[i + sx - 1] + Diagonal);
                    dist[i] = d;
                }
        }

        // Высота клетки в вокселях; height и waterTop в клетках — по ней: вода, если поверхность ниже seaTop
        void SetSurface(int i, float surface)
        {
            outSurface[i] = surface;
            int voxels = (int)math.floor(surface + 0.5f);
            outHeight[i] = voxels / k;
            outWaterTop[i] = voxels < seaTop ? SeaLevel : 0;
        }

        // Песок: дно и вся суша, кроме травы. Трава с землёй — только на части островов (на половине крупных),
        // и там пятнами в глубине острова, не у воды. Клетка травы на ArenaLayout.grassRaiseVoxels выше песка
        void Sand(NativeArray<float> shape)
        {
            int n = sx * sz;
            // Номер острова клетки + 1 (0 — вода): острова — связные куски суши
            var island = new NativeArray<int>(n, Allocator.Temp);
            var grassy = new NativeList<bool>(64, Allocator.Temp);
            var stack = new NativeList<int>(256, Allocator.Temp);
            for (int start = 0; start < n; start++)
            {
                if (island[start] != 0 || IsWater(start))
                    continue;
                int id = grassy.Length + 1;
                int size = 0;
                island[start] = id;
                stack.Add(start);
                while (stack.Length > 0)
                {
                    int i = stack[stack.Length - 1];
                    stack.RemoveAt(stack.Length - 1);
                    size++;
                    int x = i % sx, z = i / sx;
                    if (x > 0) Spread(i - 1, id, island, stack);
                    if (x < sx - 1) Spread(i + 1, id, island, stack);
                    if (z > 0) Spread(i - sx, id, island, stack);
                    if (z < sz - 1) Spread(i + sx, id, island, stack);
                }
                grassy.Add(size >= MinGrassIsland && rnd.NextFloat() < 0.5f);
            }

            for (int i = 0; i < n; i++)
            {
                if ((outFlags[i] & ArenaLayout.FlagRock) != 0)
                    continue;
                bool grass = island[i] != 0 && grassy[island[i] - 1] && shape[i] > 0.15f
                             && Fbm(new float2(i % sx, i / sx), 0.04f, 2, grassOffset) > 0.2f;
                if (!grass)
                    outFlags[i] |= ArenaLayout.FlagSand;
            }
        }

        // Причалы: 6 лучей от края карты к середине, первый берег на луче смотрит в открытую воду
        void PlaceLandings(NativeList<int2> landings, NativeArray<bool> occupied)
        {
            float start = rnd.NextFloat(0f, 2f * math.PI);
            for (int k = 0; k < 6; k++)
            {
                if (!Shore(start + k * math.PI / 3f, out var cell, out var facing) || !Far(cell, landings, 30f))
                    continue;
                landings.Add(cell);
                outResources.Add(new ResourcePoint { cell = cell, kind = ResourceKind.Landing, team = -1, facing = facing });
                Occupy(occupied, cell, 7);
            }
        }

        // Луч с края карты к середине по углу angle: первая клетка суши и шаг от неё в сторону края
        bool Shore(float angle, out int2 cell, out int2 facing)
        {
            var center = new float2((sx - 1) * 0.5f, (sz - 1) * 0.5f);
            var dir = new float2(math.cos(angle), math.sin(angle));
            facing = math.abs(dir.x) > math.abs(dir.y) ? new int2((int)math.sign(dir.x), 0) : new int2(0, (int)math.sign(dir.y));
            float far = math.min(sx, sz) * 0.5f - 2f;
            for (float t = far; t > 0f; t -= 1f)
            {
                cell = (int2)math.round(center + dir * t);
                if (!Inside(cell))
                    continue;
                int i = Idx(cell.x, cell.y);
                if (IsWater(i))
                    continue;
                return (outFlags[i] & ArenaLayout.FlagRock) == 0;
            }
            cell = default;
            return false;
        }

        // Остовы: на мелководье (не у края карты), далеко друг от друга и от причалов, нос по случайной оси
        void PlaceWrecks(NativeList<int2> landings, NativeArray<bool> occupied)
        {
            var wrecks = new NativeList<int2>(3, Allocator.Temp);
            for (int attempt = 0; attempt < 400 && wrecks.Length < 3; attempt++)
            {
                var c = new int2(rnd.NextInt(DeepRing + 10, sx - DeepRing - 10), rnd.NextInt(DeepRing + 10, sz - DeepRing - 10));
                int i = Idx(c.x, c.y);
                if (!IsWater(i) || occupied[i] || !Far(c, wrecks, 50f) || !Far(c, landings, 25f))
                    continue;
                var facing = rnd.NextInt(4) switch { 0 => new int2(1, 0), 1 => new int2(-1, 0), 2 => new int2(0, 1), _ => new int2(0, -1) };
                wrecks.Add(c);
                outResources.Add(new ResourcePoint { cell = c, kind = ResourceKind.Wreck, team = -1, facing = facing });
                Occupy(occupied, c, 16);
            }
        }

        // Руины: на островах, место 17×17 клеток (дом 27×19 вокселей) хотя бы на 60% суша, далеко друг от друга и от причалов.
        // Пол — по самой низкой суше; вода под местом засыпается песком вровень с полом: дом стоит на берегу, а не в море
        void PlaceRuins(NativeList<int2> landings, NativeArray<bool> occupied)
        {
            var ruins = new NativeList<int2>(5, Allocator.Temp);
            for (int attempt = 0; attempt < 1500 && ruins.Length < 5; attempt++)
            {
                var c = new int2(rnd.NextInt(DeepRing + 10, sx - DeepRing - 10), rnd.NextInt(DeepRing + 10, sz - DeepRing - 10));
                if (occupied[Idx(c.x, c.y)] || IsWater(Idx(c.x, c.y)) || !Far(c, ruins, 45f) || !Far(c, landings, 25f))
                    continue;

                float low = float.MaxValue;
                int land = 0;
                bool clear = true;
                for (int dz = -8; dz <= 8 && clear; dz++)
                    for (int dx = -8; dx <= 8; dx++)
                    {
                        int j = Idx(c.x + dx, c.y + dz);
                        if ((outFlags[j] & ArenaLayout.FlagRock) != 0 || occupied[j])
                        {
                            clear = false;
                            break;
                        }
                        if (IsWater(j))
                            continue;
                        land++;
                        low = math.min(low, outSurface[j]);
                    }
                if (!clear || land < 17 * 17 * 6 / 10)
                    continue;

                // Площадка вровень с самой низкой сушей (не ниже берега), вокруг 12 клеток спуска с тем же шагом,
                // что у берегов (воксель на StepVoxels): вода у площадки мелеет к ней, а не обрывается уступом
                float pad = math.max(math.floor(low + 0.5f), seaTop);
                for (int dz = -20; dz <= 20; dz++)
                    for (int dx = -20; dx <= 20; dx++)
                    {
                        int j = Idx(c.x + dx, c.y + dz);
                        int ring = math.max(math.abs(dx), math.abs(dz));
                        if (ring <= 8)
                        {
                            SetSurface(j, pad);
                            // Пол руин ровный: трава не поднимает клетки под домом
                            outFlags[j] |= ArenaLayout.FlagSand;
                        }
                        else
                        {
                            SetSurface(j, math.max(outSurface[j], pad - (ring - 8) * k / StepVoxels));
                        }
                    }
                ruins.Add(c);
                outResources.Add(new ResourcePoint { cell = c, kind = ResourceKind.Ruin, team = -1 });
                Occupy(occupied, c, 11);
            }
        }

        static bool Far(int2 c, NativeList<int2> others, float distance)
        {
            for (int k = 0; k < others.Length; k++)
                if (math.distance((float2)c, (float2)others[k]) < distance)
                    return false;
            return true;
        }

        // Пальмы и деревья на островах (в глубине острова гуще), валуны на суше и на мелководье
        void PlaceVegetation(NativeArray<float> shape, NativeArray<bool> occupied)
        {
            for (int z = 3; z < sz - 3; z++)
            {
                for (int x = 3; x < sx - 3; x++)
                {
                    int i = Idx(x, z);
                    if (occupied[i] || (outFlags[i] & ArenaLayout.FlagRock) != 0)
                        continue;
                    var c = new int2(x, z);
                    float roll = rnd.NextFloat();
                    if (IsWater(i))
                    {
                        if (EdgeDistance(x, z) > DeepRing && roll < 0.0015f)
                        {
                            outRocks.Add(c);
                            Occupy(occupied, c, 4);
                        }
                        continue;
                    }
                    // Кроны пальм и деревьев до 15 вокселей (3,4 м) в стороны: стволы не теснее 7 клеток (3,2 м),
                    // между ними проходы и поляны
                    float treeChance = shape[i] > 0.12f ? 0.02f : 0.008f;
                    if (roll < treeChance)
                    {
                        outTrees.Add(c);
                        Occupy(occupied, c, 7);
                    }
                    else if (roll < treeChance + 0.004f)
                    {
                        outRocks.Add(c);
                        Occupy(occupied, c, 4);
                    }
                }
            }
        }

        void Occupy(NativeArray<bool> occupied, int2 c, int radius)
        {
            for (int dz = -radius; dz <= radius; dz++)
                for (int dx = -radius; dx <= radius; dx++)
                {
                    var p = c + new int2(dx, dz);
                    if (Inside(p))
                        occupied[Idx(p.x, p.y)] = true;
                }
        }

        int2 NearestLand()
        {
            var center = new int2(sx / 2, sz / 2);
            for (int r = 0; r < math.max(sx, sz) / 2; r++)
                for (int dz = -r; dz <= r; dz++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        var c = center + new int2(dx, dz);
                        if (Inside(c) && !IsWater(Idx(c.x, c.y)))
                            return c;
                    }
            return center;
        }

        // Точка в сторону суши от причала: 10 клеток против направления на открытую воду
        int2 Inland(int2 landing)
        {
            for (int k = 0; k < outResources.Length; k++)
                if (outResources[k].cell.Equals(landing))
                    return landing - outResources[k].facing * 10;
            return landing;
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
    }
}
