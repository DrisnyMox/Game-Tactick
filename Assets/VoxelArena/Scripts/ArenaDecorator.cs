using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using static VoxelBlocks;

/// <summary>
/// Ставит в воксели деревья, камни и рудные площадки из раскладки.
/// Всё из вокселей, поэтому разрушается взрывами наравне с рельефом.
/// </summary>
public static class ArenaDecorator
{
    /// <summary>
    /// Запускает джобу построек после dependsOn. Координаты всех построек — в клетках раскладки;
    /// клетка пишется кубом cellVoxels³ вокселей
    /// </summary>
    public static JobHandle Schedule(NativeArray<byte> voxels, int3 dims, int cellVoxels, ArenaLayout layout, ArenaBiome biome,
        int seed, int wallHeight, JobHandle dependsOn)
    {
        return new DecorateJob
        {
            voxels = voxels,
            dims = dims,
            cellVoxels = cellVoxels,
            sizeX = layout.sizeX,
            height = layout.height,
            flags = layout.flags,
            trees = layout.trees.AsArray(),
            rocks = layout.rocks.AsArray(),
            walls = layout.walls.AsArray(),
            resources = layout.resources.AsArray(),
            biome = biome,
            seed = seed,
            wallHeight = wallHeight,
        }.Schedule(dependsOn);
    }

    // Постройки идут по порядку: их форма зависит от общей последовательности случайных чисел.
    // Strict: деревья и камни по сиду одинаковы в редакторе (Burst) и в WebGL (без Burst)
    [BurstCompile(FloatMode = FloatMode.Strict)]
    struct DecorateJob : IJob
    {
        public NativeArray<byte> voxels;
        public int3 dims;
        public int cellVoxels;
        public int sizeX;
        [ReadOnly] public NativeArray<int> height;
        [ReadOnly] public NativeArray<byte> flags;
        [ReadOnly] public NativeArray<int2> trees;
        [ReadOnly] public NativeArray<int2> rocks;
        [ReadOnly] public NativeArray<WallCell> walls;
        [ReadOnly] public NativeArray<ResourcePoint> resources;
        public ArenaBiome biome;
        public int seed;
        public int wallHeight;

        public void Execute()
        {
            var writer = new Writer(voxels, dims, cellVoxels);
            // Постройки острова — вокселями под рост персонажа (8 вокселей), а не клетками раскладки
            var fine = new Writer(voxels, dims, 1);
            var rnd = new DotNetRandom(seed * 31 + 5);

            for (int k = 0; k < trees.Length; k++)
            {
                var cell = trees[k];
                var root = new int3(cell.x, height[cell.y * sizeX + cell.x], cell.y);

                switch (biome)
                {
                    case ArenaBiome.Winter:
                        Pine(ref writer, root, ref rnd);
                        break;
                    case ArenaBiome.Desert:
                        Cactus(ref writer, root, ref rnd);
                        break;
                    case ArenaBiome.Tropic:
                        // На пляже пальмы, в глубине острова пальмы, лиственные деревья и кусты
                        var at = VoxelRoot(cell);
                        double kind = (flags[cell.y * sizeX + cell.x] & ArenaLayout.FlagSand) != 0 ? 0.0 : rnd.NextDouble();
                        if (kind < 0.45)
                            Palm(ref fine, at, ref rnd);
                        else if (kind < 0.7)
                            JungleTree(ref fine, at, ref rnd);
                        else
                            Bush(ref fine, at, ref rnd);
                        break;
                    default:
                        RoundTree(ref writer, root, ref rnd, biome == ArenaBiome.Autumn);
                        break;
                }
            }

            for (int k = 0; k < rocks.Length; k++)
            {
                var cell = rocks[k];
                Rock(ref writer, new int3(cell.x, height[cell.y * sizeX + cell.x], cell.y), ref rnd);
            }

            for (int k = 0; k < walls.Length; k++)
            {
                var wall = walls[k];
                int ground = height[wall.cell.y * sizeX + wall.cell.x];
                int top = wallHeight + (wall.outer && Merlon(wall.cell) ? 1 : 0);
                for (int y = 0; y < top; y++)
                    writer.Set(new int3(wall.cell.x, ground + y, wall.cell.y), Wall);
            }

            for (int k = 0; k < resources.Length; k++)
            {
                var resource = resources[k];
                var cell = resource.cell;
                var root = new int3(cell.x, height[cell.y * sizeX + cell.x], cell.y);
                var facing = new int3(resource.facing.x, 0, resource.facing.y);
                switch (resource.kind)
                {
                    case ResourceKind.Landing:
                        Landing(ref fine, VoxelRoot(cell), facing);
                        break;
                    case ResourceKind.Ruin:
                        Ruin(ref fine, VoxelRoot(cell), ref rnd);
                        break;
                    case ResourceKind.Wreck:
                        Wreck(ref fine, VoxelRoot(cell), facing, ref rnd);
                        break;
                    default:
                        // Точки идут парами (точка и её зеркало): пары по очереди с железом и с золотом
                        CapturePoint(ref writer, root, k / 2 % 2 == 1 ? GoldOre : IronOre);
                        break;
                }
            }
        }

        // Воксель на поверхности посередине клетки раскладки: первый воздух над землёй
        int3 VoxelRoot(int2 cell) =>
            new int3(cell.x * cellVoxels + cellVoxels / 2, height[cell.y * sizeX + cell.x] * cellVoxels, cell.y * cellVoxels + cellVoxels / 2);
    }

    // Зубцы: два блока через два
    static bool Merlon(int2 cell) => ((cell.x + cell.y) / 2) % 2 == 0;

    // Точка захвата: светлое кольцо в земле радиусом 3 и рудный бугор (железо или золото) в центре
    static void CapturePoint(ref Writer w, int3 root, byte ore)
    {
        for (int dz = -3; dz <= 3; dz++)
        {
            for (int dx = -3; dx <= 3; dx++)
            {
                bool ring = math.max(math.abs(dx), math.abs(dz)) == 3;
                if (ring)
                    w.Set(root + new int3(dx, -1, dz), Marker);
            }
        }

        OreMound(ref w, root, ore);
    }

    // Лиственное дерево: ствол и круглая крона; осенью крона пёстрая
    static void RoundTree(ref Writer w, int3 root, ref DotNetRandom rnd, bool autumn)
    {
        int trunk = 3 + rnd.Next(2);
        for (int y = 0; y < trunk; y++)
            w.Set(root + new int3(0, y, 0), Wood);

        var center = root + new int3(0, trunk, 0);
        float radius = 2.2f + (float)rnd.NextDouble() * 0.5f;
        int r = (int)math.ceil(radius);

        for (int dy = -1; dy <= r; dy++)
        {
            for (int dz = -r; dz <= r; dz++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    // Крона чуть сплюснута снизу
                    float d = math.length(new float3(dx, dy * (dy < 0 ? 1.6f : 1f), dz));
                    if (d > radius)
                        continue;

                    var p = center + new int3(dx, dy, dz);
                    if (w.Get(p) == Wood)
                        continue;

                    double alt = autumn ? 0.4 : 0.25;
                    w.SetIfAir(p, rnd.NextDouble() < alt ? LeavesAlt : Leaves);
                }
            }
        }
    }

    // Ель: ствол и конус из слоёв хвои, сверху снег
    static void Pine(ref Writer w, int3 root, ref DotNetRandom rnd)
    {
        int trunk = 2;
        int layers = 5 + rnd.Next(2);
        for (int y = 0; y < trunk + layers - 1; y++)
            w.Set(root + new int3(0, y, 0), Wood);

        for (int l = 0; l < layers; l++)
        {
            int y = trunk + l;
            int r = math.max(0, 2 - l / 2);
            for (int dz = -r; dz <= r; dz++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    if (math.abs(dx) + math.abs(dz) > r + 1)
                        continue;
                    var p = root + new int3(dx, y, dz);
                    if (dx == 0 && dz == 0 && l < layers - 1)
                        continue;
                    w.SetIfAir(p, Leaves);
                }
            }
        }

        // Снег на верхушке и на внешних краях слоёв
        w.SetIfAir(root + new int3(0, trunk + layers, 0), Snow);
        for (int l = 0; l < layers; l += 2)
        {
            int y = trunk + l + 1;
            int r = math.max(0, 2 - l / 2);
            for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                    if (w.Get(root + new int3(dx, y - 1, dz)) == Leaves && rnd.NextDouble() < 0.5)
                        w.SetIfAir(root + new int3(dx, y, dz), Snow);
        }
    }

    // Кактус: столб и одна-две «руки»
    static void Cactus(ref Writer w, int3 root, ref DotNetRandom rnd)
    {
        int height = 3 + rnd.Next(3);
        for (int y = 0; y < height; y++)
            w.Set(root + new int3(0, y, 0), Leaves);

        int arms = 1 + rnd.Next(2);
        for (int a = 0; a < arms; a++)
        {
            var side = rnd.Next(4) switch
            {
                0 => new int3(1, 0, 0),
                1 => new int3(-1, 0, 0),
                2 => new int3(0, 0, 1),
                _ => new int3(0, 0, -1),
            };
            int at = 1 + rnd.Next(math.max(1, height - 2));
            var p = root + new int3(0, at, 0) + side;
            w.SetIfAir(p, LeavesAlt);
            w.SetIfAir(p + new int3(0, 1, 0), LeavesAlt);
            if (rnd.Next(2) == 0)
                w.SetIfAir(p + new int3(0, 2, 0), LeavesAlt);
        }
    }

    // Валун: неровный сплюснутый ком камня
    static void Rock(ref Writer w, int3 root, ref DotNetRandom rnd)
    {
        float radius = 1.4f + (float)rnd.NextDouble() * 1.2f;
        int r = (int)math.ceil(radius);
        // Порядок вызовов как в прежнем коде: x растяжения, затем z
        float stretchX = 1f + (float)rnd.NextDouble() * 0.4f;
        float stretchZ = 1f + (float)rnd.NextDouble() * 0.4f;
        var stretch = new float3(stretchX, 1.3f, stretchZ);

        for (int dy = -1; dy <= r; dy++)
        {
            for (int dz = -r; dz <= r; dz++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    float d = math.length(new float3(dx, dy, dz) * stretch);
                    if (d > radius + (float)rnd.NextDouble() * 0.4f)
                        continue;
                    w.Set(root + new int3(dx, dy, dz), Stone);
                }
            }
        }
    }

    // Рудная площадка: верхний слой вокруг — руда, в центре бугор из руды
    static void OreMound(ref Writer w, int3 root, byte ore)
    {
        for (int dz = -2; dz <= 2; dz++)
        {
            for (int dx = -2; dx <= 2; dx++)
            {
                if (math.abs(dx) == 2 && math.abs(dz) == 2)
                    continue;
                var ground = root + new int3(dx, -1, dz);
                if (w.Get(ground) != Air)
                    w.Set(ground, ore);
            }
        }

        for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
                w.Set(root + new int3(dx, 0, dz), ore);

        w.Set(root + new int3(0, 1, 0), ore);
    }

    // ---------- Остров: всё в вокселях, персонаж ростом 8 вокселей (1,8 м), воксель 0,226 м ----------

    // Пальма 6–9 м (28–40 вокселей): ствол 2×2 плавно клонится в одну сторону, наверху орехи
    // и 7–9 листьев длиной 2,5–3,4 м, которые свисают к концам
    static void Palm(ref Writer w, int3 root, ref DotNetRandom rnd)
    {
        int height = 28 + rnd.Next(13);
        double angle = rnd.NextDouble() * math.PI * 2;
        var lean = new float2((float)math.cos(angle), (float)math.sin(angle)) * (0.15f + (float)rnd.NextDouble() * 0.2f) * height;
        int3 top = root;
        for (int y = -1; y < height; y++)
        {
            float t = math.max(0, y) / (float)height;
            var off = (int2)math.round(lean * t * t);
            top = root + new int3(off.x, y, off.y);
            for (int dz = 0; dz <= 1; dz++)
                for (int dx = 0; dx <= 1; dx++)
                    w.Set(top + new int3(dx, 0, dz), Wood);
        }

        for (int dz = -1; dz <= 2; dz++)
            for (int dx = -1; dx <= 2; dx++)
                if (rnd.Next(3) == 0)
                    w.SetIfAir(top + new int3(dx, -1, dz), LeavesAlt);

        int fronds = 7 + rnd.Next(3);
        double start = rnd.NextDouble() * math.PI * 2;
        for (int f = 0; f < fronds; f++)
        {
            double a = start + f * math.PI * 2 / fronds + (rnd.NextDouble() - 0.5) * 0.4;
            var dir = new float2((float)math.cos(a), (float)math.sin(a));
            var side = new float2(-dir.y, dir.x);
            int length = 11 + rnd.Next(5);
            for (int s = 0; s <= length; s++)
            {
                float u = s / (float)length;
                // Лист сначала поднимается, потом свисает
                int y = (int)math.round(2f + 2f * u - 7f * u * u);
                int width = s > 1 && s < length - 1 ? 1 : 0;
                for (int k = -width; k <= width; k++)
                {
                    var p = (int2)math.round(new float2(0.5f, 0.5f) + dir * s + side * k);
                    w.SetIfAir(top + new int3(p.x, y, p.y), Leaves);
                }
            }
        }
    }

    // Лиственное дерево джунглей 4,5–7 м: ствол 2×2 (у земли 4×4), широкая плоская крона радиусом 2–2,5 м
    static void JungleTree(ref Writer w, int3 root, ref DotNetRandom rnd)
    {
        int height = 20 + rnd.Next(12);
        for (int y = -1; y < height; y++)
        {
            int r = y < 3 ? 1 : 0;
            for (int dz = -r; dz <= 1 + r; dz++)
                for (int dx = -r; dx <= 1 + r; dx++)
                    w.Set(root + new int3(dx, y, dz), Wood);
        }

        var center = root + new int3(0, height, 0);
        float radius = 8f + (float)rnd.NextDouble() * 3f;
        int r2 = (int)math.ceil(radius);
        for (int dy = -3; dy <= 5; dy++)
            for (int dz = -r2; dz <= r2; dz++)
                for (int dx = -r2; dx <= r2; dx++)
                {
                    float d = math.length(new float3(dx, dy * 1.8f, dz));
                    if (d > radius || d > radius - 1.5f && rnd.Next(3) == 0)
                        continue;
                    w.SetIfAir(center + new int3(dx, dy, dz), rnd.Next(4) == 0 ? LeavesAlt : Leaves);
                }
    }

    // Куст по пояс (3–5 вокселей): укрытие
    static void Bush(ref Writer w, int3 root, ref DotNetRandom rnd)
    {
        float radius = 2.5f + (float)rnd.NextDouble() * 1.5f;
        int r = (int)math.ceil(radius);
        for (int dy = 0; dy <= r; dy++)
            for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                {
                    float d = math.length(new float3(dx, dy * 1.3f, dz));
                    if (d > radius || d > radius - 1f && rnd.Next(3) == 0)
                        continue;
                    w.SetIfAir(root + new int3(dx, dy, dz), rnd.Next(3) == 0 ? LeavesAlt : Leaves);
                }
    }

    // Руины дома старого мира 6×4 м (27×19 вокселей): бетонный пол, стены в 2 вокселя толщиной до 3,2 м,
    // обломанные сверху кусками; дверной проём 2,2 м, окна, перегородка с проходом, остаток плиты крыши,
    // ржавая арматура на углах. Внутри электроника (терминал), ржавый ящик, труба. Середина свободна: там ящик
    static void Ruin(ref Writer w, int3 root, ref DotNetRandom rnd)
    {
        const int hx = 13, hz = 9, full = 14;
        for (int dz = -hz; dz <= hz; dz++)
            for (int dx = -hx; dx <= hx; dx++)
                w.Set(root + new int3(dx, -1, dz), Wall);

        // Высота куска стены 2×2 вокселя: целая или обломанная
        var pieces = new FixedList512Bytes<byte>();
        for (int i = 0; i < 100; i++)
            pieces.Add((byte)(rnd.Next(3) == 0 ? full : full - 2 - rnd.Next(9)));

        for (int dz = -hz; dz <= hz; dz++)
        {
            for (int dx = -hx; dx <= hx; dx++)
            {
                bool wall = math.abs(dx) >= hx - 1 || math.abs(dz) >= hz - 1;
                if (!wall)
                    continue;
                bool corner = math.abs(dx) >= hx - 1 && math.abs(dz) >= hz - 1;
                int piece = ((dx + hx) / 2 * 7 + (dz + hz) / 2 * 13) % pieces.Length;
                int h = corner ? full : pieces[piece];
                for (int y = 0; y < h; y++)
                {
                    bool door = dz <= -hz + 1 && dx >= -3 && dx <= 2 && y < 10;
                    bool window = math.abs(dx) >= hx - 1 && math.abs(dz) <= 3 && y >= 5 && y < 9
                                  || dz >= hz - 1 && (dx >= 5 && dx <= 8 || dx <= -6 && dx >= -9) && y >= 5 && y < 9;
                    if (!door && !window)
                        w.Set(root + new int3(dx, y, dz), Wall);
                }
                if (math.abs(dx) == hx && math.abs(dz) == hz)
                {
                    int rebar = 3 + rnd.Next(4);
                    for (int y = full; y < full + rebar; y++)
                        w.Set(root + new int3(dx, y, dz), Rust);
                }
            }
        }

        // Перегородка с проходом
        for (int dz = -hz + 2; dz <= 2; dz++)
            for (int y = 0; y < full - 2; y++)
                if (!(dz >= -3 && dz <= 0 && y < 10))
                    for (int dx = 3; dx <= 4; dx++)
                        w.Set(root + new int3(dx, y, dz), Wall);

        // Остаток плиты крыши с дырами
        for (int dz = -1; dz <= hz; dz++)
            for (int dx = -hx; dx <= 2; dx++)
                if (rnd.Next(5) != 0)
                    w.Set(root + new int3(dx, full, dz), Wall);

        // Терминал у дальней стены, ржавый ящик, труба вдоль стены
        for (int y = 0; y < 5; y++)
            for (int dx = 6; dx <= 9; dx++)
                for (int dz = 5; dz <= 6; dz++)
                    w.Set(root + new int3(dx, y, dz), Tech);
        for (int y = 0; y < 3; y++)
            for (int dx = -10; dx <= -7; dx++)
                for (int dz = 3; dz <= 6; dz++)
                    w.Set(root + new int3(dx, y, dz), Rust);
        for (int dx = -hx + 2; dx <= 2; dx++)
            w.Set(root + new int3(dx, 11, hz - 2), Metal);
    }

    // Ржавый остов катера 11×4 м (51×17 вокселей) на мелководье: днище в песке, борта 2,2 м с дырами,
    // нос сужается, на корме рубка 4 м с окнами, дверью и рацией (электроника)
    static void Wreck(ref Writer w, int3 root, int3 facing, ref DotNetRandom rnd)
    {
        var across = new int3(-facing.z, 0, facing.x);
        for (int t = -25; t <= 25; t++)
        {
            int half = t > 12 ? math.max(1, 8 - (t - 12) * 7 / 13) : 8;
            for (int a = -half; a <= half; a++)
            {
                var p = root + facing * t + across * a;
                w.Set(p + new int3(0, -2, 0), Rust);
                w.Set(p + new int3(0, -1, 0), Rust);
                bool hull = math.abs(a) == half || t == -25;
                if (!hull)
                    continue;
                for (int y = 0; y < 10; y++)
                    if (rnd.Next(9) != 0)
                        w.Set(p + new int3(0, y, 0), y >= 8 ? Metal : Rust);
            }
        }

        for (int t = -22; t <= -12; t++)
            for (int a = -5; a <= 5; a++)
                for (int y = 0; y < 19; y++)
                {
                    bool wall = t == -22 || t == -12 || math.abs(a) == 5;
                    bool roof = y == 18;
                    bool window = y >= 12 && y < 16 && (math.abs(a) <= 2 || t > -20 && t < -14);
                    bool door = t == -12 && math.abs(a) <= 1 && y < 9;
                    if (roof || wall && !window && !door)
                        w.Set(root + facing * t + across * a + new int3(0, y, 0), y >= 17 ? Metal : Rust);
                }
        for (int y = 0; y < 3; y++)
            for (int a = -1; a <= 1; a++)
                w.Set(root + facing * -20 + across * a + new int3(0, y, 0), Tech);
    }

    // Причал: настил 2 м шириной (9 вокселей) от берега на 10 м в воду, вровень с песком, сваи со столбиками,
    // у настила лодка 4×1,6 м (18×7 вокселей) с ржавым мотором
    static void Landing(ref Writer w, int3 root, int3 facing)
    {
        var across = new int3(-facing.z, 0, facing.x);
        var deck = root + new int3(0, -1, 0) - across * 4;
        for (int t = -4; t <= 44; t++)
        {
            for (int a = 0; a <= 8; a++)
                w.Set(deck + facing * t + across * a, Wood);
            if (t % 8 != 4)
                continue;
            for (int a = 0; a <= 8; a += 8)
                for (int y = -6; y <= 4; y++)
                    w.Set(deck + facing * t + across * a + new int3(0, y, 0), Wood);
        }

        var boat = deck + facing * 24 + across * 11 + new int3(0, -1, 0);
        for (int t = 0; t < 18; t++)
            for (int a = 0; a < 7; a++)
            {
                var p = boat + facing * t + across * a;
                w.Set(p, Wood);
                bool side = a == 0 || a == 6 || t == 0 || t == 17;
                if (side)
                    for (int y = 1; y <= 2; y++)
                        w.Set(p + new int3(0, y, 0), Wood);
            }
        for (int y = 0; y < 4; y++)
            for (int a = 2; a <= 3; a++)
                w.Set(boat + facing * 18 + across * a + new int3(0, y, 0), Rust);
    }

    // Принимает клетки раскладки, пишет в воксели: клетка p — куб от p * scale размером scale
    struct Writer
    {
        NativeArray<byte> voxels;
        readonly int3 dims;
        readonly int scale;

        public Writer(NativeArray<byte> voxels, int3 dims, int scale)
        {
            this.voxels = voxels;
            this.dims = dims;
            this.scale = scale;
        }

        bool Inside(int3 v) => math.all(v >= new int3(0, 1, 0)) && math.all(v < dims);

        int Index(int3 v) => (v.y * dims.z + v.z) * dims.x + v.x;

        public byte Get(int3 p) => Inside(p * scale) ? voxels[Index(p * scale)] : Air;

        public void Set(int3 p, byte block) => Fill(p, block, false);

        public void SetIfAir(int3 p, byte block) => Fill(p, block, true);

        void Fill(int3 p, byte block, bool onlyAir)
        {
            var origin = p * scale;
            for (int y = 0; y < scale; y++)
                for (int z = 0; z < scale; z++)
                    for (int x = 0; x < scale; x++)
                    {
                        var v = origin + new int3(x, y, z);
                        if (Inside(v) && (!onlyAir || voxels[Index(v)] == Air))
                            voxels[Index(v)] = block;
                    }
        }
    }
}
