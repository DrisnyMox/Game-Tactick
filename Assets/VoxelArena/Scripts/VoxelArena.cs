using System;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Воксельная арена фиксированного размера. Все воксели лежат в одном массиве,
/// чанки 16³ нужны для мешей и коллайдеров, рисуются склеенные узлы из NodeChunks³ чанков. Арена не должна быть повёрнута.
/// У каждого чанка три меша: суша и вода для отрисовки и форма суши (без цвета и затенения,
/// грани склеены крупно) — для MeshCollider и NavMesh.
/// </summary>
public class VoxelArena : MonoBehaviour
{
    public const int ChunkSize = 16;
    // Узел отрисовки — куб NodeChunks³ чанков с одним мешем суши и одним мешем воды на всех.
    // После правки вокселей перестраивается чанк 16³ (дёшево), а узел склеивается из готовых мешей
    // чанков. Рисуются узлы, а не чанки: отрисовок на кадр в NodeChunks³ раз меньше
    public const int NodeChunks = 2;

    static readonly int PaletteTexId = Shader.PropertyToID("_VoxelPaletteTex");
    static readonly int VoxelSizeId = Shader.PropertyToID("_VoxelSize");

    [SerializeField] Material material;
    [SerializeField] int sizeX = 400;
    [SerializeField] int sizeY = 48;
    [SerializeField] int sizeZ = 400;
    [SerializeField] float voxelSize = 0.5f;
    [Tooltip("Сколько вокселей по каждой оси приходится на клетку раскладки. Размеры арены (sizeX/Y/Z) " +
             "задаются в вокселях, раскладка строится на сетке в cellVoxels раз крупнее")]
    [SerializeField] int cellVoxels = 1;
    [SerializeField] ArenaGenSettings generation = ArenaGenSettings.Default;

    /// <summary>Меши чанка изменились: форма суши, вода (null — пусто), матрица чанка.</summary>
    public event Action<int, Mesh, Mesh, Matrix4x4> ChunkMeshChanged;
    public event Action Generated;

    /// <summary>Выбитый воксель: клетка и блок, который в ней был</summary>
    public struct RemovedVoxel
    {
        public int3 cell;
        public byte block;
    }

    /// <summary>
    /// Воксели выбиты (Explode, ClearVoxels): мировая точка, от которой их выбило, и сами воксели.
    /// Список живёт только на время вызова: арена переиспользует его
    /// </summary>
    public event Action<Vector3, List<RemovedVoxel>> VoxelsRemoved;
    readonly List<RemovedVoxel> removedVoxels = new();

    public int3 Dims => dims;
    public Material Material => material;
    public int ChunkTotal => chunks.Length;
    public float VoxelSize => voxelSize;
    public int CellVoxels => cellVoxels;
    public NativeArray<byte> Voxels => voxels;
    public ArenaLayout Layout { get; private set; }
    public ArenaBiome Biome { get; private set; }
    public Vector3 BaseOne { get; private set; }
    public Vector3 BaseTwo { get; private set; }
    /// <summary>Мировые точки сразу снаружи ворот базы один и базы два</summary>
    public Vector3 GateOne { get; private set; }
    public Vector3 GateTwo { get; private set; }

    public int Seed
    {
        get => generation.seed;
        set => generation.seed = value;
    }

    /// <summary>Мировая коробка без гор по краю: камере дальше смотреть незачем</summary>
    public Bounds PlayableBounds
    {
        get
        {
            var bounds = WorldBounds;
            float inset = generation.borderMinWidth * voxelSize;
            bounds.Expand(new Vector3(-inset * 2f, 0f, -inset * 2f));
            return bounds;
        }
    }

    public Bounds WorldBounds
    {
        get
        {
            var size = (Vector3)(float3)dims * voxelSize;
            return new Bounds(transform.position + size * 0.5f, size);
        }
    }

    // Замеры
    public double LastGenerateMs { get; private set; }
    public int LastRebuiltChunks { get; private set; }
    public double LastMeshMs { get; private set; }
    public double LastApplyMs { get; private set; }
    /// <summary>Склейка узлов отрисовки из мешей чанков</summary>
    public double LastCombineMs { get; private set; }
    public int TotalTriangles { get; private set; }
    /// <summary>Треугольников в мешах формы (коллайдер и NavMesh)</summary>
    public int TotalShapeTriangles { get; private set; }

    class Chunk
    {
        public GameObject root;
        public Mesh land;
        public Mesh water;
        public Mesh shape;
        public bool hasLand;
        public bool hasWater;
        public MeshCollider collider;
        public int triangles;
        public int shapeTriangles;
        public bool dirty;
    }

    class Node
    {
        public int3 firstChunk;
        public Mesh land;
        public Mesh water;
        public MeshRenderer landRenderer;
        public MeshRenderer waterRenderer;
        public bool dirty;
    }

    class MeshBuffers
    {
        public NativeList<float3> positions;
        public NativeList<float3> normals;
        public NativeList<Color32> colors;
        public NativeList<uint> indices;

        public MeshBuffers()
        {
            positions = new NativeList<float3>(1024, Allocator.TempJob);
            normals = new NativeList<float3>(1024, Allocator.TempJob);
            colors = new NativeList<Color32>(1024, Allocator.TempJob);
            indices = new NativeList<uint>(1536, Allocator.TempJob);
        }

        public void Dispose()
        {
            positions.Dispose();
            normals.Dispose();
            colors.Dispose();
            indices.Dispose();
        }
    }

    int3 dims;
    int3 chunkCounts;
    // Высота поверхности каждого столбца вокселей после генерации (индекс z * dims.x + x)
    int[] columnHeight;

    NativeArray<byte> voxels;
    NativeArray<byte> faceColors;
    Texture2D paletteTexture;

    Chunk[] chunks;
    readonly List<int> dirtyChunks = new();
    int3 nodeCounts;
    Node[] nodes;
    readonly List<Node> dirtyNodes = new();
    readonly List<CombineInstance> combineLand = new();
    readonly List<CombineInstance> combineWater = new();

    readonly Stopwatch timer = new();
    readonly Stopwatch generateTimer = new();

    void Awake()
    {
        dims = new int3(RoundToChunk(sizeX), RoundToChunk(sizeY), RoundToChunk(sizeZ));
        chunkCounts = dims / ChunkSize;

        voxels = new NativeArray<byte>(dims.x * dims.y * dims.z, Allocator.Persistent);
        faceColors = VoxelBlocks.CreateFaceTable(Allocator.Persistent);

        paletteTexture = VoxelBlocks.CreatePaletteTexture();
        Shader.SetGlobalTexture(PaletteTexId, paletteTexture);
        Shader.SetGlobalFloat(VoxelSizeId, voxelSize);

        // Сцены, сохранённые со старой версией настроек, не знают новых полей
        if (generation.version != ArenaGenSettings.CurrentVersion)
        {
            int seed = generation.seed;
            generation = ArenaGenSettings.Default;
            generation.seed = seed;
            Debug.Log("[Arena] Настройки генерации из старой версии, взяты значения по умолчанию");
        }

        CreateChunkObjects();
    }

    void Start()
    {
        Generate();
    }

    void LateUpdate()
    {
        // Все правки за кадр перестраиваются одной пачкой
        if (dirtyChunks.Count > 0)
            RebuildDirtyChunks();
    }

    void OnDestroy()
    {
        voxels.Dispose();
        faceColors.Dispose();

        foreach (var chunk in chunks)
        {
            Destroy(chunk.land);
            Destroy(chunk.water);
            Destroy(chunk.shape);
        }
        foreach (var node in nodes)
        {
            Destroy(node.land);
            Destroy(node.water);
        }
        Destroy(paletteTexture);
    }

    public void Generate()
    {
        generateTimer.Restart();

        if (ChunkSize % cellVoxels != 0)
            throw new InvalidOperationException($"cellVoxels = {cellVoxels}: размер чанка {ChunkSize} должен делиться на него");

        Biome = ArenaBiomes.Resolve(generation.biome, generation.seed);
        int k = cellVoxels;
        Layout = ArenaLayoutGenerator.Generate(generation, Biome, dims.x / k, dims.y / k, dims.z / k);
        double layoutMs = generateTimer.Elapsed.TotalMilliseconds;

        var height = new NativeArray<int>(dims.x * dims.z, Allocator.TempJob);
        var waterTop = new NativeArray<int>(dims.x * dims.z, Allocator.TempJob);
        var flags = new NativeArray<byte>(dims.x * dims.z, Allocator.TempJob);
        ExpandLayout(height, waterTop, flags);
        columnHeight = height.ToArray();

        new ArenaGenerateJob
        {
            dims = dims,
            snowLine = ArenaBiomes.SnowLine(Biome, generation.highHeight) * k,
            height = height,
            waterTop = waterTop,
            flags = flags,
            voxels = voxels,
        }
        .Schedule(dims.x * dims.z, dims.x)
        .Complete();

        height.Dispose();
        waterTop.Dispose();
        flags.Dispose();

        ArenaDecorator.Decorate(voxels, dims, cellVoxels, Layout, Biome, generation.seed, generation.wallHeight);
        VoxelBlocks.WritePalette(paletteTexture, ArenaBiomes.Palette(Biome));

        BaseOne = CellTop(Layout.baseOne);
        BaseTwo = CellTop(Layout.baseTwo);
        GateOne = CellTop(Layout.gates[0]);
        GateTwo = CellTop(Layout.gates[1]);

        double fillMs = generateTimer.Elapsed.TotalMilliseconds - layoutMs;

        for (int c = 0; c < chunks.Length; c++)
            MarkChunkDirty(c);

        RebuildDirtyChunks();
        LastGenerateMs = generateTimer.Elapsed.TotalMilliseconds;

        Debug.Log($"[Arena] Сид {generation.seed}, биом {ArenaBiomes.DisplayName(Biome)}: " +
                  $"участков {Layout.regionCount}, рамп {Layout.rampCount}, выровнено {Layout.flattenedRegions}, " +
                  $"по сетке: базы связаны {Layout.basesConnected}, точки захвата достижимы {Layout.capturePointsReachable}, " +
                  $"деревьев {Layout.trees.Count}, камней {Layout.rocks.Count}, " +
                  $"раскладка {layoutMs:0} мс, воксели {fillMs:0} мс, меши {LastMeshMs:0} мс, " +
                  $"меши в Unity и коллайдеры {LastApplyMs:0} мс, склейка узлов {LastCombineMs:0} мс, " +
                  $"всего {LastGenerateMs:0} мс");

        Generated?.Invoke();
    }

    /// <summary>
    /// Переносит раскладку в столбцы вокселей: клетка раскладки занимает cellVoxels×cellVoxels столбцов,
    /// высоты умножаются на cellVoxels. Перепад между соседними клетками в один блок раскладки
    /// (рампы, склоны гор) сглаживается до шага в один воксель, иначе рампа стала бы лестницей
    /// со ступенями в cellVoxels вокселей. Обрывы (перепад больше одного блока) остаются обрывами.
    /// </summary>
    void ExpandLayout(NativeArray<int> height, NativeArray<int> waterTop, NativeArray<byte> flags)
    {
        int k = cellVoxels;
        int lx = Layout.sizeX, lz = Layout.sizeZ;
        int[] cellHeight = Layout.height;

        for (int z = 0; z < dims.z; z++)
        {
            int cz = z / k;
            // Смещение середины столбца от середины клетки, в долях клетки: от -0.5 до 0.5
            float oz = ((z % k) + 0.5f) / k - 0.5f;
            int nz = oz < 0 ? cz - 1 : cz + 1;

            for (int x = 0; x < dims.x; x++)
            {
                int cx = x / k;
                float ox = ((x % k) + 0.5f) / k - 0.5f;
                int nx = ox < 0 ? cx - 1 : cx + 1;

                int cell = cz * lx + cx;
                int h = cellHeight[cell];
                float smooth = h;
                if (nx >= 0 && nx < lx)
                {
                    int d = cellHeight[cz * lx + nx] - h;
                    if (math.abs(d) == 1)
                        smooth += math.abs(ox) * d;
                }
                if (nz >= 0 && nz < lz)
                {
                    int d = cellHeight[nz * lx + cx] - h;
                    if (math.abs(d) == 1)
                        smooth += math.abs(oz) * d;
                }

                int column = z * dims.x + x;
                height[column] = (int)math.floor(smooth * k + 0.5f);
                waterTop[column] = Layout.waterTop[cell] * k;
                flags[column] = Layout.flags[cell];
            }
        }
    }

    /// <summary>Мировая точка на поверхности посередине клетки раскладки</summary>
    public Vector3 CellTop(int2 cell)
    {
        int k = cellVoxels;
        return ColumnTop(cell.x * k + k / 2, cell.y * k + k / 2);
    }

    /// <summary>
    /// Выбивает воксели шаром. Возвращает число выбитых. maxDepth — на сколько вокселей ниже исходной
    /// поверхности столбца можно копать (воронки в бою — на 1: агент перешагивает уступ в один воксель,
    /// глубже — поле воронок становится непроходимым и отрезает базу)
    /// </summary>
    public int Explode(Vector3 worldCenter, float worldRadius, int maxDepth = int.MaxValue)
    {
        float3 center = WorldToVoxel(worldCenter);
        float radius = worldRadius / voxelSize;

        // y от 1: нижний слой — неразрушаемое дно
        int3 min = math.max((int3)math.floor(center - radius), new int3(0, 1, 0));
        int3 max = math.min((int3)math.ceil(center + radius), dims - 1);

        int removed = 0;
        float radiusSq = radius * radius;
        removedVoxels.Clear();

        for (int y = min.y; y <= max.y; y++)
        {
            for (int z = min.z; z <= max.z; z++)
            {
                for (int x = min.x; x <= max.x; x++)
                {
                    var cell = new float3(x + 0.5f, y + 0.5f, z + 0.5f);
                    if (math.distancesq(cell, center) > radiusSq)
                        continue;

                    if (maxDepth != int.MaxValue && y < columnHeight[z * dims.x + x] - maxDepth)
                        continue;

                    int index = VoxelIndex(x, y, z);
                    byte block = voxels[index];
                    if (block == VoxelBlocks.Air || VoxelBlocks.IsIndestructible(block))
                        continue;

                    voxels[index] = VoxelBlocks.Air;
                    removedVoxels.Add(new RemovedVoxel { cell = new int3(x, y, z), block = block });
                    removed++;
                }
            }
        }

        if (removed > 0)
        {
            MarkVoxelRangeDirty(min, max);
            VoxelsRemoved?.Invoke(worldCenter, removedVoxels);
        }

        return removed;
    }

    /// <summary>Блок в вокселе; вне арены — Air</summary>
    public byte GetBlock(int3 voxel)
    {
        if (math.any(voxel < 0) || math.any(voxel >= dims))
            return VoxelBlocks.Air;
        return voxels[VoxelIndex(voxel.x, voxel.y, voxel.z)];
    }

    /// <summary>Выбить перечисленные воксели (например, обрушить участок стены)</summary>
    public void ClearVoxels(IReadOnlyList<int3> cells)
    {
        if (cells.Count == 0)
            return;
        int3 min = dims, max = int3.zero;
        removedVoxels.Clear();
        foreach (var c in cells)
        {
            if (math.any(c < 0) || math.any(c >= dims))
                continue;
            int index = VoxelIndex(c.x, c.y, c.z);
            if (voxels[index] != VoxelBlocks.Air)
                removedVoxels.Add(new RemovedVoxel { cell = c, block = voxels[index] });
            voxels[index] = VoxelBlocks.Air;
            min = math.min(min, c);
            max = math.max(max, c);
        }
        if (math.all(min <= max))
        {
            MarkVoxelRangeDirty(min, max);
            // Обвал участка: куски разлетаются от его середины
            if (removedVoxels.Count > 0)
                VoxelsRemoved?.Invoke(VoxelToWorld((float3)(min + max + 1) * 0.5f), removedVoxels);
        }
    }

    public float3 WorldToVoxel(Vector3 world)
    {
        return (float3)((world - transform.position) / voxelSize);
    }

    public Vector3 VoxelToWorld(float3 voxel)
    {
        return transform.position + (Vector3)(voxel * voxelSize);
    }

    /// <summary>Мировая точка на верхней грани самого высокого сплошного вокселя столбца.</summary>
    public Vector3 ColumnTop(int x, int z)
    {
        int y = dims.y - 1;
        while (y > 0)
        {
            byte block = voxels[VoxelIndex(x, y, z)];
            if (block != VoxelBlocks.Air && block != VoxelBlocks.Water)
                break;
            y--;
        }

        return VoxelToWorld(new float3(x + 0.5f, y + 1, z + 0.5f));
    }

    /// <summary>Высота поверхности столбца: y первого пустого вокселя над сплошным (вода — не сплошное)</summary>
    public int SurfaceY(int x, int z)
    {
        int y = dims.y - 1;
        while (y > 0)
        {
            byte block = voxels[VoxelIndex(x, y, z)];
            if (block != VoxelBlocks.Air && block != VoxelBlocks.Water)
                break;
            y--;
        }
        return y + 1;
    }

    /// <summary>Есть ли вода прямо над поверхностью столбца</summary>
    public bool IsWaterAt(int x, int z)
    {
        int y = SurfaceY(x, z);
        return y < dims.y && voxels[VoxelIndex(x, y, z)] == VoxelBlocks.Water;
    }

    int VoxelIndex(int x, int y, int z) => (y * dims.z + z) * dims.x + x;

    int ChunkIndex(int3 c) => (c.y * chunkCounts.z + c.z) * chunkCounts.x + c.x;

    int3 ChunkCoord(int index)
    {
        int x = index % chunkCounts.x;
        int z = index / chunkCounts.x % chunkCounts.z;
        int y = index / (chunkCounts.x * chunkCounts.z);
        return new int3(x, y, z);
    }

    static int RoundToChunk(int size)
    {
        return math.max(ChunkSize, (size + ChunkSize - 1) / ChunkSize * ChunkSize);
    }

    /// <summary>
    /// Меши суши чанков, в которых стоят стены баз, с матрицами — чтобы при установке здания
    /// нарисовать стены отдельно полупрозрачными.
    /// </summary>
    public void CollectWallChunks(List<(Mesh mesh, Matrix4x4 matrix)> result)
    {
        result.Clear();
        if (Layout == null)
            return;

        var seen = new HashSet<int>();
        int k = cellVoxels;
        foreach (var wall in Layout.walls)
        {
            // Клетка раскладки лежит внутри одного чанка: размер чанка делится на cellVoxels
            int x = wall.cell.x * k, z = wall.cell.y * k;
            int bottom = Layout.height[wall.cell.y * Layout.sizeX + wall.cell.x] * k;
            int top = math.min(bottom + (generation.wallHeight + 1) * k, dims.y - 1);
            for (int cy = bottom / ChunkSize; cy <= top / ChunkSize; cy++)
            {
                int index = ChunkIndex(new int3(x / ChunkSize, cy, z / ChunkSize));
                if (!seen.Add(index) || !chunks[index].hasLand)
                    continue;
                result.Add((chunks[index].land, chunks[index].root.transform.localToWorldMatrix));
            }
        }
    }

    void MarkChunkDirty(int index)
    {
        if (chunks[index].dirty)
            return;

        chunks[index].dirty = true;
        dirtyChunks.Add(index);
    }

    // Грани и затенение зависят от соседей, поэтому захватываем на воксель шире
    void MarkVoxelRangeDirty(int3 min, int3 max)
    {
        int3 from = math.max(min - 1, 0) / ChunkSize;
        int3 to = math.min(max + 1, dims - 1) / ChunkSize;

        for (int y = from.y; y <= to.y; y++)
            for (int z = from.z; z <= to.z; z++)
                for (int x = from.x; x <= to.x; x++)
                    MarkChunkDirty(ChunkIndex(new int3(x, y, z)));
    }

    void CreateChunkObjects()
    {
        int total = chunkCounts.x * chunkCounts.y * chunkCounts.z;
        chunks = new Chunk[total];

        for (int c = 0; c < total; c++)
        {
            int3 coord = ChunkCoord(c);

            var root = new GameObject($"Chunk {coord.x}_{coord.y}_{coord.z}");
            root.layer = gameObject.layer;
            root.transform.SetParent(transform, false);
            root.transform.localPosition = (Vector3)(float3)(coord * ChunkSize) * voxelSize;
            root.transform.localScale = Vector3.one * voxelSize;

            var collider = root.AddComponent<MeshCollider>();
            collider.sharedMesh = null;
            collider.enabled = false;

            // Меши суши и воды чанк не рисует: их склеивает в себя узел
            chunks[c] = new Chunk
            {
                root = root,
                land = new Mesh { name = root.name },
                water = new Mesh { name = root.name + " Water" },
                shape = new Mesh { name = root.name + " Shape" },
                collider = collider,
            };
        }

        nodeCounts = (chunkCounts + NodeChunks - 1) / NodeChunks;
        nodes = new Node[nodeCounts.x * nodeCounts.y * nodeCounts.z];
        for (int n = 0; n < nodes.Length; n++)
        {
            var coord = new int3(n % nodeCounts.x, n / (nodeCounts.x * nodeCounts.z), n / nodeCounts.x % nodeCounts.z);
            var firstChunk = coord * NodeChunks;

            var root = new GameObject($"Node {coord.x}_{coord.y}_{coord.z}");
            root.layer = gameObject.layer;
            root.transform.SetParent(transform, false);
            root.transform.localPosition = (Vector3)(float3)(firstChunk * ChunkSize) * voxelSize;
            root.transform.localScale = Vector3.one * voxelSize;

            var land = new Mesh { name = root.name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            land.MarkDynamic();
            root.AddComponent<MeshFilter>().sharedMesh = land;
            var landRenderer = root.AddComponent<MeshRenderer>();
            landRenderer.sharedMaterial = material;
            landRenderer.enabled = false;

            var waterObject = new GameObject("Water");
            waterObject.layer = gameObject.layer;
            waterObject.transform.SetParent(root.transform, false);
            var water = new Mesh { name = root.name + " Water", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            water.MarkDynamic();
            waterObject.AddComponent<MeshFilter>().sharedMesh = water;
            var waterRenderer = waterObject.AddComponent<MeshRenderer>();
            waterRenderer.sharedMaterial = material;
            waterRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            waterRenderer.enabled = false;

            nodes[n] = new Node
            {
                firstChunk = firstChunk,
                land = land,
                water = water,
                landRenderer = landRenderer,
                waterRenderer = waterRenderer,
            };
        }
    }

    Node NodeOfChunk(int chunkIndex)
    {
        int3 n = ChunkCoord(chunkIndex) / NodeChunks;
        return nodes[(n.y * nodeCounts.z + n.z) * nodeCounts.x + n.x];
    }

    // Склеивает меши суши и воды чанков узла. Вершины чанков сдвигаются на их место внутри узла
    // (в вокселях: масштаб у узла тот же, что у чанка)
    void CombineNode(Node node)
    {
        combineLand.Clear();
        combineWater.Clear();
        for (int y = 0; y < NodeChunks; y++)
        {
            for (int z = 0; z < NodeChunks; z++)
            {
                for (int x = 0; x < NodeChunks; x++)
                {
                    int3 c = node.firstChunk + new int3(x, y, z);
                    if (math.any(c >= chunkCounts))
                        continue;
                    var chunk = chunks[ChunkIndex(c)];
                    var offset = Matrix4x4.Translate((Vector3)(float3)(new int3(x, y, z) * ChunkSize));
                    if (chunk.hasLand)
                        combineLand.Add(new CombineInstance { mesh = chunk.land, transform = offset });
                    if (chunk.hasWater)
                        combineWater.Add(new CombineInstance { mesh = chunk.water, transform = offset });
                }
            }
        }

        node.landRenderer.enabled = CombineInto(node.land, combineLand);
        node.waterRenderer.enabled = CombineInto(node.water, combineWater);
        node.dirty = false;
    }

    static bool CombineInto(Mesh mesh, List<CombineInstance> parts)
    {
        mesh.Clear();
        if (parts.Count == 0)
            return false;
        mesh.CombineMeshes(parts.ToArray(), true, true);
        // Границы задаём явно: по ним Unity решает, виден ли узел камере
        float size = NodeChunks * ChunkSize;
        mesh.bounds = new Bounds(Vector3.one * (size * 0.5f), Vector3.one * size);
        return true;
    }

    void RebuildDirtyChunks()
    {
        int count = dirtyChunks.Count;
        var landBuffers = new MeshBuffers[count];
        var waterBuffers = new MeshBuffers[count];
        var shapeBuffers = new MeshBuffers[count];
        var handles = new NativeArray<JobHandle>(count * 3, Allocator.Temp);

        // 1. Меши: суша, вода и форма суши отдельными джобами. Много чанков (генерация) — во всех
        // рабочих потоках; несколько (воронка, скол стены) — прямо в главном: рабочие потоки бывают
        // надолго заняты NavMesh, и ожидание их стоило бы десятки мс
        timer.Restart();
        bool inWorkers = count > MainThreadChunkLimit;
        for (int k = 0; k < count; k++)
        {
            int3 origin = ChunkCoord(dirtyChunks[k]) * ChunkSize;
            landBuffers[k] = new MeshBuffers();
            waterBuffers[k] = new MeshBuffers();
            shapeBuffers[k] = new MeshBuffers();

            handles[k * 3] = BuildMesh(origin, false, false, landBuffers[k], inWorkers);
            handles[k * 3 + 1] = BuildMesh(origin, true, false, waterBuffers[k], inWorkers);
            handles[k * 3 + 2] = BuildMesh(origin, false, true, shapeBuffers[k], inWorkers);
        }
        JobHandle.CompleteAll(handles);
        handles.Dispose();
        LastMeshMs = timer.Elapsed.TotalMilliseconds;

        // 2. Загрузка в Mesh и MeshCollider (коллайдер готовит данные при назначении меша)
        timer.Restart();
        for (int k = 0; k < count; k++)
        {
            var chunk = chunks[dirtyChunks[k]];

            // Переназначение заставляет коллайдер взять новые данные
            chunk.collider.sharedMesh = null;

            bool hasLand = ApplyMesh(chunk.land, landBuffers[k]);
            chunk.hasLand = hasLand;
            ApplyMesh(chunk.shape, shapeBuffers[k]);
            chunk.collider.enabled = hasLand;
            if (hasLand)
                chunk.collider.sharedMesh = chunk.shape;

            chunk.hasWater = ApplyMesh(chunk.water, waterBuffers[k]);
            var node = NodeOfChunk(dirtyChunks[k]);
            if (!node.dirty)
            {
                node.dirty = true;
                dirtyNodes.Add(node);
            }
            chunk.triangles = (landBuffers[k].indices.Length + waterBuffers[k].indices.Length) / 3;
            chunk.shapeTriangles = shapeBuffers[k].indices.Length / 3;

            landBuffers[k].Dispose();
            waterBuffers[k].Dispose();
            shapeBuffers[k].Dispose();
        }
        LastApplyMs = timer.Elapsed.TotalMilliseconds;

        // 3. Узлы склеиваются из готовых мешей чанков
        timer.Restart();
        foreach (var node in dirtyNodes)
            CombineNode(node);
        dirtyNodes.Clear();
        LastCombineMs = timer.Elapsed.TotalMilliseconds;

        int total = 0, shapeTotal = 0;
        foreach (var chunk in chunks)
        {
            total += chunk.triangles;
            shapeTotal += chunk.shapeTriangles;
        }
        TotalTriangles = total;
        TotalShapeTriangles = shapeTotal;
        LastRebuiltChunks = count;

        foreach (int index in dirtyChunks)
        {
            var chunk = chunks[index];
            chunk.dirty = false;

            ChunkMeshChanged?.Invoke(
                index,
                chunk.hasLand ? chunk.shape : null,
                chunk.hasWater ? chunk.water : null,
                chunk.root.transform.localToWorldMatrix);
        }

        dirtyChunks.Clear();
    }

    // Сколько чанков за раз строится в главном потоке, а не в рабочих
    const int MainThreadChunkLimit = 12;

    JobHandle BuildMesh(int3 origin, bool water, bool plain, MeshBuffers buffers, bool inWorkers)
    {
        var job = new GreedyMeshJob
        {
            voxels = voxels,
            faceColors = faceColors,
            dims = dims,
            chunkOrigin = origin,
            chunkSize = ChunkSize,
            water = water,
            plain = plain,
            positions = buffers.positions,
            normals = buffers.normals,
            colors = buffers.colors,
            indices = buffers.indices,
        };

        if (inWorkers)
            return job.Schedule();
        job.Run();
        return default;
    }

    // Меш собирается обычными SetVertices/SetNormals/SetColors/SetIndices, как в Voxer
    static bool ApplyMesh(Mesh mesh, MeshBuffers buffers)
    {
        mesh.Clear();

        if (buffers.indices.Length == 0)
            return false;

        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.SetVertices(buffers.positions.AsArray());
        // У меша формы нормалей и цветов нет
        if (buffers.normals.Length > 0)
        {
            mesh.SetNormals(buffers.normals.AsArray());
            mesh.SetColors(buffers.colors.AsArray());
        }
        mesh.SetIndices(buffers.indices.AsArray(), MeshTopology.Triangles, 0);

        // Границы задаём явно: по ним Unity решает, виден ли чанк камере
        mesh.bounds = new Bounds(Vector3.one * (ChunkSize * 0.5f), Vector3.one * ChunkSize);
        return true;
    }
}
