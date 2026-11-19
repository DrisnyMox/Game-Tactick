using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

/// <summary>
/// Раскладка архипелага для вылазки (ArenaShape.Island): Бирюзовые Пределы из лора.
/// Много маленьких островов, между ними мелководье. Вода стоит до SeaLevel клеток минус один воксель
/// (ArenaLayout.waterDropVoxels): над дном мелководья (SeaLevel - 1) воды 1 воксель, над протоками (SeaLevel - 2) —
/// 3 вокселя, персонаж ростом 8 вокселей бегает по дну. Глубоко только в кольце у края карты.
///  * Острова — пятна шума; к краю карты шум гаснет, там открытая вода.
//  * Острова плоские: песок на 1 воксель над водой, в глубине на 3, редкие дюны на 5. Соседние клетки отличаются
///    не больше чем на одну: и острова, и дно проходимы.
///  * Причалы (ResourceKind.Landing): 6 лучей от края карты к середине, первый берег на луче. Первый причал —
///    высадка игрока (baseOne), самый дальний от него — baseTwo. Ворота (gates) — 10 клеток от причала в сторону суши.
//  * Ржавые остовы (Wreck) — 3 на мелководье, руины (Ruin) — 5 на ровных местах 17×17 клеток, на суше или затопленные.
///  * Постройки ArenaDecorator пишет вокселями под рост персонажа (8 вокселей), а не клетками.
///  * Пальмы на островах, на больших островах вперемешку с лиственными деревьями; валуны на суше и в воде.
/// Баз, стен, рамп нет. Высоты — в клетках раскладки, как у арены.
/// </summary>
public static class IslandLayout
{
    /// <summary>Вода стоит до этой высоты (клетки раскладки) без одного вокселя</summary>
    public const int SeaLevel = 8;

    public static ArenaLayoutGenerator.Pending Schedule(ArenaGenSettings settings, int sizeX, int sizeY, int sizeZ)
    {
        var layout = new ArenaLayout(sizeX, sizeZ) { seaLevel = SeaLevel, waterDropVoxels = 1, wadeableWater = true };
        var random = new Random(math.hash(new int2(settings.seed, 11)) | 1u);
        var info = new NativeArray<int>(ArenaLayoutGenerator.InfoCount, Allocator.Persistent);
        var axis = new NativeArray<float2>(1, Allocator.Persistent);
        // Pending освобождает карты шума арены; архипелагу они не нужны
        var noiseMaps = new NativeArray<float4>(1, Allocator.Persistent);

        var handle = new IslandJob
        {
            sx = sizeX,
            sz = sizeZ,
            maxHeight = sizeY - 4,
            seed = random.NextUInt() | 1u,
            islandOffset = random.NextFloat2(-5000f, 5000f),
            densityOffset = random.NextFloat2(-5000f, 5000f),
            hillOffset = random.NextFloat2(-5000f, 5000f),
            channelOffset = random.NextFloat2(-5000f, 5000f),
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

        public int sx, sz, maxHeight;
        public uint seed;
        public float2 islandOffset, densityOffset, hillOffset, channelOffset;

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
            LimitSlopes();
            Sand(shape);

            var landings = new NativeList<int2>(8, Allocator.Temp);
            PlaceLandings(landings, occupied);
            // Луч не нашёл берега ни разу (почти пустая карта): высадка на ближайшей к середине суше
            if (landings.Length == 0)
                landings.Add(NearestLand());
            PlaceWrecks(landings, occupied);
            PlaceRuins(landings, occupied);
            // Засыпка под руинами могла дать ступень к воде: снова не больше клетки, а всё ниже уровня моря — под водой
            LimitSlopes();
            for (int i = 0; i < n; i++)
                if (outHeight[i] < SeaLevel)
                    outWaterTop[i] = SeaLevel;
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

        // Острова — пятна шума, местами гуще (второй, крупный шум). Вода: у берега мелко, в протоках глубже,
        // у края карты глубоко. Суша плоская: 1–3 вокселя над водой, редко 5
        void Terrain(NativeArray<float> shape)
        {
            for (int z = 0; z < sz; z++)
            {
                for (int x = 0; x < sx; x++)
                {
                    int i = Idx(x, z);
                    var p = new float2(x, z);
                    int edge = EdgeDistance(x, z);
                    float density = Fbm(p, 0.006f, 2, densityOffset) * 0.12f;
                    float s = Fbm(p, 0.022f, 4, islandOffset) - 0.1f + density;
                    s -= math.saturate((FadeTo - edge) / (float)(FadeTo - DeepRing)) * 0.7f;
                    shape[i] = s;

                    if (s < 0f)
                    {
                        int depth;
                        if (edge < DeepRing)
                            depth = 5;
                        else if (s > -0.05f)
                            depth = 1;
                        else
                            depth = Fbm(p, 0.03f, 2, channelOffset) > 0f ? 2 : 1;
                        outHeight[i] = SeaLevel - depth;
                        outWaterTop[i] = SeaLevel;
                        continue;
                    }

                    // Острова плоские: у воды песок на 1 воксель над водой, в глубине на 3, редкие дюны на 5 (~1,1 м)
                    float dune = Fbm(p, 0.03f, 2, hillOffset);
                    outHeight[i] = SeaLevel + (s > 0.12f ? 1 : 0) + (s > 0.25f && dune > 0.35f ? 1 : 0);
                }
            }
        }

        // Соседние клетки отличаются не больше чем на одну: высота не больше соседа + 1 (проход вперёд и назад)
        void LimitSlopes()
        {
            for (int z = 0; z < sz; z++)
                for (int x = 0; x < sx; x++)
                {
                    int i = Idx(x, z);
                    if (x > 0) outHeight[i] = math.min(outHeight[i], outHeight[i - 1] + 1);
                    if (z > 0) outHeight[i] = math.min(outHeight[i], outHeight[i - sx] + 1);
                }
            for (int z = sz - 1; z >= 0; z--)
                for (int x = sx - 1; x >= 0; x--)
                {
                    int i = Idx(x, z);
                    if (x < sx - 1) outHeight[i] = math.min(outHeight[i], outHeight[i + 1] + 1);
                    if (z < sz - 1) outHeight[i] = math.min(outHeight[i], outHeight[i + sx] + 1);
                }
        }

        // Песок: дно, пляж у воды и низкие края островов
        void Sand(NativeArray<float> shape)
        {
            for (int i = 0; i < sx * sz; i++)
            {
                if ((outFlags[i] & ArenaLayout.FlagRock) != 0)
                    continue;
                if (IsWater(i) || outHeight[i] <= SeaLevel || shape[i] < 0.05f)
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

                int low = int.MaxValue, high = int.MinValue, land = 0;
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
                        low = math.min(low, outHeight[j]);
                        high = math.max(high, outHeight[j]);
                    }
                if (!clear || land < 17 * 17 * 6 / 10 || high - low > 2)
                    continue;

                for (int dz = -8; dz <= 8; dz++)
                    for (int dx = -8; dx <= 8; dx++)
                    {
                        int j = Idx(c.x + dx, c.y + dz);
                        outHeight[j] = low;
                        outWaterTop[j] = 0;
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
