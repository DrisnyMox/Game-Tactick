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
                var cell = resources[k].cell;
                CapturePoint(ref writer, new int3(cell.x, height[cell.y * sizeX + cell.x], cell.y));
            }
        }
    }

    // Зубцы: два блока через два
    static bool Merlon(int2 cell) => ((cell.x + cell.y) / 2) % 2 == 0;

    // Точка захвата: светлое кольцо в земле радиусом 3 и рудный бугор в центре
    static void CapturePoint(ref Writer w, int3 root)
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

        OreMound(ref w, root, IronOre);
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
