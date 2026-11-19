using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

/// <summary>
/// Воксельная арена фиксированного размера. Все воксели лежат в одном массиве,
/// чанки 16³ нужны для мешей и коллайдеров, рисуются склеенные узлы из NodeChunks³ чанков. Арена не должна быть повёрнута.
/// У каждого чанка три меша: меш отрисовки с двумя сабмешами (0 — суша, материал material; 1 — вода,
/// материал waterMaterial), форма суши (без цвета и затенения, грани склеены крупно) — для MeshCollider
/// и NavMesh, и форма воды — по ней NavMesh отмечает воду непроходимой.
/// </summary>
public class VoxelArena : MonoBehaviour
{
    public const int ChunkSize = 16;
    // Узел отрисовки — куб NodeChunks³ чанков с одним мешем на всех (два сабмеша: суша и вода).
    // После правки вокселей перестраивается чанк 16³ (дёшево), а узел склеивается из готовых мешей
    // чанков. Рисуются узлы, а не чанки: отрисовок на кадр в NodeChunks³ раз меньше
    public const int NodeChunks = 2;

    static readonly int PaletteTexId = Shader.PropertyToID("_VoxelPaletteTex");
    static readonly int VoxelSizeId = Shader.PropertyToID("_VoxelSize");

    [SerializeField] Material material;
    [Tooltip("Материал воды: второй сабмеш мешей чанков и узлов")]
    [SerializeField] Material waterMaterial;
    [SerializeField] int sizeX = 400;
    [SerializeField] int sizeY = 48;
    [SerializeField] int sizeZ = 400;
    [SerializeField] float voxelSize = 0.5f;
    [Tooltip("Сколько вокселей по каждой оси приходится на клетку раскладки. Размеры арены (sizeX/Y/Z) " +
             "задаются в вокселях, раскладка строится на сетке в cellVoxels раз крупнее")]
    [SerializeField] int cellVoxels = 1;
    [SerializeField] ArenaGenSettings generation = ArenaGenSettings.Default;

    /// <summary>Меши чанка изменились: форма суши, форма воды (null — пусто), матрица чанка.</summary>
    public event Action<int, Mesh, Mesh, Matrix4x4> ChunkMeshChanged;
    public event Action Generated;
    /// <summary>Началась генерация новой карты: до Generated воксели и меши чанков заняты ею</summary>
    public event Action GenerationStarted;

    /// <summary>Идёт генерация карты (StartGeneration растягивает её на несколько кадров)</summary>
    public bool IsGenerating { get; private set; }
    /// <summary>Готовность идущей генерации, от 0 до 1</summary>
    public float GenerationProgress { get; private set; }
    /// <summary>Шаг идущей генерации для экрана загрузки</summary>
    public string GenerationStage { get; private set; }

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
    public Material WaterMaterial => waterMaterial;
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
        // Меш отрисовки: сабмеш 0 — суша, 1 — вода. Чанк его не рисует: его склеивает в себя узел
        public Mesh mesh;
        public Mesh shape;
        public Mesh waterShape;
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
        // Сабмеш 0 — суша, 1 — вода
        public Mesh mesh;
        public MeshRenderer renderer;
        public bool dirty;
    }

    /// <summary>
    /// Меши одной пачки чанков: задачи GreedyMeshJob и Mesh.MeshDataArray, в которую джоба их пишет.
    /// colorTask / shapeTask / waterShapeTask — номер задачи меша чанка в пачке, -1 — у чанка нет граней этого меша
    /// </summary>
    sealed class MeshBatch
    {
        public readonly int[] colorTask = new int[ChunkBatch];
        public readonly int[] shapeTask = new int[ChunkBatch];
        public readonly int[] waterShapeTask = new int[ChunkBatch];
        // Была ли у чанка суша до перестройки: от этого зависит, трогать ли его коллайдер
        public readonly bool[] hadLand = new bool[ChunkBatch];
        // Меши задач по порядку: в них Mesh.ApplyAndDisposeWritableMeshData кладёт данные
        public readonly List<Mesh> meshes = new(ChunkBatch * 3);
        public NativeList<ChunkMeshTask> tasks;
        public Mesh.MeshDataArray data;
        public JobHandle handle;
        public int start, count;
        // Джоба запущена, данные ещё не загружены в меши: tasks и data заняты
        public bool scheduled;
    }

    // Две пачки: пока одна загружается в Unity, рабочие потоки строят меши другой
    readonly MeshBatch[] meshBatches = { new(), new() };
    // Раскладка вершин: меши отрисовки чанков и узлов — позиция, нормаль, цвет (r — индекс палитры,
    // g — затенение); формы — только позиция. Каждый атрибут в своём потоке: джобы копируют их отдельно
    NativeArray<VertexAttributeDescriptor> colorLayout;
    NativeArray<VertexAttributeDescriptor> shapeLayout;
    // Рабочие списки GreedyMeshJob по потокам
    NativeArray<GreedyMeshScratch> meshScratch;

    int3 dims;
    int3 chunkCounts;
    // Высота поверхности каждого столбца вокселей после генерации (индекс z * dims.x + x)
    NativeArray<int> columnHeight;

    NativeArray<byte> voxels;
    NativeArray<byte> faceColors;
    Texture2D paletteTexture;

    Chunk[] chunks;
    readonly List<int> dirtyChunks = new();
    int3 nodeCounts;
    Node[] nodes;
    readonly List<Node> dirtyNodes = new();
    // Материалы сабмешей узла: суша, вода
    Material[] nodeMaterials;

    // Меши чанков и узлов пачки склейки (CombineNodesJob)
    readonly List<Mesh> combineChunkMeshes = new();
    readonly List<Mesh> combineNodeMeshes = new();

    readonly Stopwatch timer = new();
    readonly Stopwatch generateTimer = new();
    // Время с последнего отпущенного кадра при склейке узлов по кадрам
    readonly Stopwatch frameTimer = new();

    // Генерация по кадрам: её джобы и их память. Если арену уничтожат посреди генерации (выход из Play Mode
    // на экране загрузки), OnDestroy дожидается джоб и освобождает память
    ArenaLayoutGenerator.Pending pendingLayout;
    JobHandle generationJobs;
    NativeArray<int> genWaterTop;
    NativeArray<byte> genFlags;
    NativeArray<int3> rebuildOrigins;
    NativeArray<byte> rebuildFaces;

    void Awake()
    {
        dims = new int3(RoundToChunk(sizeX), RoundToChunk(sizeY), RoundToChunk(sizeZ));
        chunkCounts = dims / ChunkSize;

        voxels = new NativeArray<byte>(dims.x * dims.y * dims.z, Allocator.Persistent);
        columnHeight = new NativeArray<int>(dims.x * dims.z, Allocator.Persistent);
        faceColors = VoxelBlocks.CreateFaceTable(Allocator.Persistent);
        colorLayout = new NativeArray<VertexAttributeDescriptor>(new[]
        {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0),
            new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3, 1),
            new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4, 2),
        }, Allocator.Persistent);
        shapeLayout = new NativeArray<VertexAttributeDescriptor>(new[]
        {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0),
        }, Allocator.Persistent);
        meshScratch = GreedyMeshScratch.Create(Allocator.Persistent);

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
        StartGeneration();
    }

    void LateUpdate()
    {
        // Все правки за кадр перестраиваются одной пачкой. Во время генерации чанки перестраивает она
        if (dirtyChunks.Count > 0 && !IsGenerating)
            RebuildDirtyChunks();
    }

    void OnDestroy()
    {
        if (IsGenerating)
            AbortGeneration();
        voxels.Dispose();
        columnHeight.Dispose();
        faceColors.Dispose();
        colorLayout.Dispose();
        shapeLayout.Dispose();
        GreedyMeshScratch.Dispose(meshScratch);
        // null — объект уничтожен до Start, раскладки ещё не было
        Layout?.Dispose();

        foreach (var chunk in chunks)
        {
            Destroy(chunk.mesh);
            Destroy(chunk.shape);
            Destroy(chunk.waterShape);
        }
        foreach (var node in nodes)
            Destroy(node.mesh);
        Destroy(paletteTexture);
    }

    /// <summary>
    /// Генерирует карту за несколько кадров: пока джобы считают, кадры идут, и экран загрузки показывает
    /// GenerationStage и GenerationProgress. По готовности — событие Generated
    /// </summary>
    public void StartGeneration()
    {
        if (IsGenerating)
            throw new InvalidOperationException("Карта уже генерируется");
        StartCoroutine(GenerateSteps(true));
    }

    /// <summary>Генерирует карту сразу, в этом же вызове</summary>
    // Джобы генерации читают и пишут воксели: сначала они должны закончиться
    void AbortGeneration()
    {
        pendingLayout?.Finish().Dispose();
        generationJobs.Complete();
        foreach (var batch in meshBatches)
        {
            if (!batch.scheduled)
                continue;
            batch.handle.Complete();
            batch.tasks.Dispose();
            batch.data.Dispose();
        }
        if (genWaterTop.IsCreated) genWaterTop.Dispose();
        if (genFlags.IsCreated) genFlags.Dispose();
        if (rebuildOrigins.IsCreated) rebuildOrigins.Dispose();
        if (rebuildFaces.IsCreated) rebuildFaces.Dispose();
    }

    public void Generate()
    {
        if (IsGenerating)
            throw new InvalidOperationException("Карта уже генерируется");
        var steps = GenerateSteps(false);
        while (steps.MoveNext()) { }
    }

    // Доли шагов в GenerationProgress: раскладка, воксели, меши чанков, склейка узлов
    const float VoxelsFrom = 0.15f, MeshesFrom = 0.25f, CombineFrom = 0.9f;

    // spread — ждать джобы, отпуская кадры (yield), а не останавливая главный поток
    IEnumerator GenerateSteps(bool spread)
    {
        IsGenerating = true;
        GenerationProgress = 0f;
        GenerationStage = "Раскладка карты";
        GenerationStarted?.Invoke();
        generateTimer.Restart();

        if (ChunkSize % cellVoxels != 0)
            throw new InvalidOperationException($"cellVoxels = {cellVoxels}: размер чанка {ChunkSize} должен делиться на него");

        Biome = ArenaBiomes.Resolve(generation.biome, generation.seed);
        int k = cellVoxels;
        pendingLayout = ArenaLayoutGenerator.Schedule(generation, Biome, dims.x / k, dims.y / k, dims.z / k, k);
        if (spread)
            while (!pendingLayout.IsCompleted)
                yield return null;
        // Раскладка прошлой карты (null — карта первая) нужна до готовности новой
        var layout = pendingLayout.Finish();
        pendingLayout = null;
        Layout?.Dispose();
        Layout = layout;
        double layoutMs = generateTimer.Elapsed.TotalMilliseconds;

        GenerationStage = "Воксели";
        GenerationProgress = VoxelsFrom;
        int columns = dims.x * dims.z;
        // Persistent: джобы могут идти дольше четырёх кадров, которые живёт TempJob
        genWaterTop = new NativeArray<int>(columns, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        genFlags = new NativeArray<byte>(columns, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

        var expand = new ExpandLayoutJob
        {
            dims = dims,
            cellVoxels = k,
            layoutSizeX = Layout.sizeX,
            layoutSizeZ = Layout.sizeZ,
            cellHeight = Layout.height,
            cellWaterTop = Layout.waterTop,
            cellFlags = Layout.flags,
            waterDrop = Layout.waterDropVoxels,
            grassRaise = Layout.grassRaiseVoxels,
            fineSurface = Layout.fineSurface,
            cellSurface = Layout.surface,
            seaTop = Layout.seaLevel * k - Layout.waterDropVoxels,
            height = columnHeight,
            waterTop = genWaterTop,
            flags = genFlags,
        }.Schedule(columns, dims.x);

        var fill = new ArenaGenerateJob
        {
            dims = dims,
            snowLine = ArenaBiomes.SnowLine(Biome, generation.highHeight) * k,
            height = columnHeight,
            waterTop = genWaterTop,
            flags = genFlags,
            voxels = voxels,
        }.Schedule(columns, dims.x, expand);

        generationJobs = ArenaDecorator.Schedule(voxels, dims, cellVoxels, Layout, columnHeight, Biome, generation.seed, generation.wallHeight, fill);
        JobHandle.ScheduleBatchedJobs();
        if (spread)
            while (!generationJobs.IsCompleted)
                yield return null;
        generationJobs.Complete();

        genWaterTop.Dispose();
        genFlags.Dispose();

        VoxelBlocks.WritePalette(paletteTexture, ArenaBiomes.Palette(Biome));

        BaseOne = CellTop(Layout.baseOne);
        BaseTwo = CellTop(Layout.baseTwo);
        GateOne = CellTop(Layout.gates[0]);
        GateTwo = CellTop(Layout.gates[1]);

        double fillMs = generateTimer.Elapsed.TotalMilliseconds - layoutMs;

        GenerationStage = "Меши чанков";
        GenerationProgress = MeshesFrom;
        for (int c = 0; c < chunks.Length; c++)
            MarkChunkDirty(c);

        var rebuild = RebuildSteps(spread);
        while (rebuild.MoveNext())
            yield return null;
        LastGenerateMs = generateTimer.Elapsed.TotalMilliseconds;

        Debug.Log($"[Arena] Сид {generation.seed}, биом {ArenaBiomes.DisplayName(Biome)}: " +
                  $"участков {Layout.regionCount}, рамп {Layout.rampCount}, выровнено {Layout.flattenedRegions}, " +
                  $"по сетке: базы связаны {Layout.basesConnected}, точки захвата достижимы {Layout.capturePointsReachable}, " +
                  $"деревьев {Layout.trees.Length}, камней {Layout.rocks.Length}, " +
                  $"раскладка {layoutMs:0} мс, воксели {fillMs:0} мс, меши {LastMeshMs:0} мс, " +
                  $"меши в Unity и коллайдеры {LastApplyMs:0} мс, склейка узлов {LastCombineMs:0} мс, " +
                  $"всего {LastGenerateMs:0} мс" + (spread ? " (по кадрам, с ожиданием кадров)" : ""));

        IsGenerating = false;
        GenerationProgress = 1f;
        GenerationStage = null;
        Generated?.Invoke();
    }

    /// <summary>
    /// Переносит раскладку в столбцы вокселей: клетка раскладки занимает cellVoxels×cellVoxels столбцов,
    /// высоты умножаются на cellVoxels. Перепад между соседними клетками в один блок раскладки
    /// (рампы, склоны гор) сглаживается до шага в один воксель, иначе рампа стала бы лестницей
    /// со ступенями в cellVoxels вокселей. Обрывы (перепад больше одного блока) остаются обрывами.
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Strict)]
    struct ExpandLayoutJob : IJobParallelFor
    {
        public int3 dims;
        public int cellVoxels;
        public int layoutSizeX, layoutSizeZ;
        public int waterDrop;
        public int grassRaise;
        // Остров: высоты столбцов — из дробной высоты клеток в вокселях (ArenaLayout.surface), вода везде до seaTop
        public bool fineSurface;
        public int seaTop;
        [ReadOnly] public NativeArray<float> cellSurface;
        [ReadOnly] public NativeArray<int> cellHeight;
        [ReadOnly] public NativeArray<int> cellWaterTop;
        [ReadOnly] public NativeArray<byte> cellFlags;
        [WriteOnly] public NativeArray<int> height;
        [WriteOnly] public NativeArray<int> waterTop;
        [WriteOnly] public NativeArray<byte> flags;

        public void Execute(int column)
        {
            int k = cellVoxels;
            int lx = layoutSizeX, lz = layoutSizeZ;
            int x = column % dims.x, z = column / dims.x;

            int cz = z / k;
            // Смещение середины столбца от середины клетки, в долях клетки: от -0.5 до 0.5
            float oz = ((z % k) + 0.5f) / k - 0.5f;
            int nz = oz < 0 ? cz - 1 : cz + 1;

            int cx = x / k;
            float ox = ((x % k) + 0.5f) / k - 0.5f;
            int nx = ox < 0 ? cx - 1 : cx + 1;

            int cell = cz * lx + cx;
            int raise = ArenaLayout.IsRaisedGrass(cellFlags[cell], grassRaise) ? grassRaise : 0;
            if (fineSurface)
            {
                // Высота между серединами четырёх соседних клеток — плавно: берег поднимается на воксель
                // через несколько вокселей, а не на каждом
                float fx = (x + 0.5f) / k - 0.5f, fz = (z + 0.5f) / k - 0.5f;
                int x0 = math.clamp((int)math.floor(fx), 0, lx - 2), z0 = math.clamp((int)math.floor(fz), 0, lz - 2);
                float tx = math.saturate(fx - x0), tz = math.saturate(fz - z0);
                int c00 = z0 * lx + x0;
                float near = math.lerp(cellSurface[c00], cellSurface[c00 + 1], tx);
                float far = math.lerp(cellSurface[c00 + lx], cellSurface[c00 + lx + 1], tx);
                height[column] = (int)math.floor(math.lerp(near, far, tz) + 0.5f) + raise;
                waterTop[column] = seaTop;
                flags[column] = cellFlags[cell];
                return;
            }

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

            // Трава на острове — слой над песком: уступ в grassRaise вокселей по краю пятна
            height[column] = (int)math.floor(smooth * k + 0.5f) + raise;
            waterTop[column] = cellWaterTop[cell] > 0 ? cellWaterTop[cell] * k - waterDrop : 0;
            flags[column] = cellFlags[cell];
        }
    }

    /// <summary>Мировая точка на поверхности посередине клетки раскладки</summary>
    public Vector3 CellTop(int2 cell)
    {
        int k = cellVoxels;
        return ColumnTop(cell.x * k + k / 2, cell.y * k + k / 2);
    }

    /// <summary>
    /// Выбивает воксели шаром (во время генерации — ничего). Возвращает число выбитых. maxDepth — на сколько вокселей ниже исходной
    /// поверхности столбца можно копать (воронки в бою — на 1: агент перешагивает уступ в один воксель,
    /// глубже — поле воронок становится непроходимым и отрезает базу)
    /// </summary>
    public int Explode(Vector3 worldCenter, float worldRadius, int maxDepth = int.MaxValue)
    {
        // Пока идёт генерация, воксели пишут её джобы: выстрел в старую карту ничего не выбивает
        if (IsGenerating)
            return 0;
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
        // Пока идёт генерация, воксели пишут её джобы
        if (cells.Count == 0 || IsGenerating)
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

    /// <summary>Поверхность рельефа столбца, как её построила генерация: без деревьев и построек, без воронок</summary>
    public int GroundY(int x, int z) => columnHeight[z * dims.x + x];

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
    /// Меши отрисовки чанков (сабмеш 0 — суша), в которых стоят стены баз, с матрицами — чтобы
    /// при установке здания нарисовать стены отдельно полупрозрачными.
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
                result.Add((chunks[index].mesh, chunks[index].root.transform.localToWorldMatrix));
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
            collider.cookingOptions = ColliderCooking;
            collider.sharedMesh = null;
            collider.enabled = false;

            chunks[c] = new Chunk
            {
                root = root,
                mesh = new Mesh { name = root.name },
                shape = new Mesh { name = root.name + " Shape" },
                waterShape = new Mesh { name = root.name + " Water Shape" },
                collider = collider,
            };
        }

        nodeMaterials = new[] { material, waterMaterial };
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

            var mesh = new Mesh { name = root.name };
            mesh.MarkDynamic();
            root.AddComponent<MeshFilter>().sharedMesh = mesh;
            // Тени отбрасывает только суша: у шейдера воды нет прохода ShadowCaster
            var renderer = root.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = nodeMaterials;
            renderer.enabled = false;

            nodes[n] = new Node
            {
                firstChunk = firstChunk,
                mesh = mesh,
                renderer = renderer,
            };
        }
    }

    Node NodeOfChunk(int chunkIndex)
    {
        int3 n = ChunkCoord(chunkIndex) / NodeChunks;
        return nodes[(n.y * nodeCounts.z + n.z) * nodeCounts.x + n.x];
    }

    // Склеивает узлы dirtyNodes[from .. from + count] (CombineNodesJob) и загружает их в меши узлов.
    // В каждый узел склеиваются меши отрисовки его чанков, вершины чанков сдвигаются на их место
    // внутри узла (в вокселях: масштаб у узла тот же, что у чанка)
    void CombineNodes(int from, int count, bool inWorkers)
    {
        combineChunkMeshes.Clear();
        combineNodeMeshes.Clear();
        var nodeParts = new NativeArray<int2>(count, Allocator.TempJob);
        var parts = new NativeArray<NodePart>(count * NodeChunks * NodeChunks * NodeChunks, Allocator.TempJob);
        int partCount = 0;
        for (int i = 0; i < count; i++)
        {
            var node = dirtyNodes[from + i];
            int start = partCount;
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
                        if (!chunk.hasLand && !chunk.hasWater)
                            continue;
                        parts[partCount++] = new NodePart
                        {
                            mesh = combineChunkMeshes.Count,
                            offset = new float3(x, y, z) * ChunkSize,
                        };
                        combineChunkMeshes.Add(chunk.mesh);
                    }
                }
            }
            nodeParts[i] = new int2(start, partCount - start);
            combineNodeMeshes.Add(node.mesh);
        }

        var chunkData = Mesh.AcquireReadOnlyMeshData(combineChunkMeshes);
        var nodeData = Mesh.AllocateWritableMeshData(count);
        var job = new CombineNodesJob
        {
            chunkMeshes = chunkData,
            parts = parts,
            nodeParts = nodeParts,
            colorLayout = colorLayout,
            nodeSize = NodeChunks * ChunkSize,
            nodeMeshes = nodeData,
        };
        if (inWorkers)
            job.Schedule(count, 1).Complete();
        else
            job.Run(count);
        chunkData.Dispose();
        parts.Dispose();

        Mesh.ApplyAndDisposeWritableMeshData(nodeData, combineNodeMeshes,
            MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
        // Границы задаём явно: по ним Unity решает, виден ли узел камере
        float size = NodeChunks * ChunkSize;
        var bounds = new Bounds(Vector3.one * (size * 0.5f), Vector3.one * size);
        for (int i = 0; i < count; i++)
        {
            var node = dirtyNodes[from + i];
            node.mesh.bounds = bounds;
            node.renderer.enabled = nodeParts[i].y > 0;
            node.dirty = false;
        }
        nodeParts.Dispose();
    }

    void RebuildDirtyChunks()
    {
        var steps = RebuildSteps(false);
        while (steps.MoveNext()) { }
    }

    // Перестройка чанков из dirtyChunks. spread — ждать джобы, отпуская кадры, и отпускать кадр после каждой
    // пачки (генерация под экраном загрузки); иначе всё в этом же вызове. Замеры при spread — с ожиданием кадров
    IEnumerator RebuildSteps(bool spread)
    {
        int count = dirtyChunks.Count;
        // Много чанков (генерация) — во всех рабочих потоках; несколько (воронка, скол стены) — прямо
        // в главном: рабочие потоки бывают надолго заняты NavMesh, и ожидание их стоило бы десятки мс
        bool inWorkers = count > MainThreadChunkLimit;

        // 0. Какие чанки вообще имеют видимые грани: пустым не нужны ни буферы, ни джобы мешей.
        // Persistent: при spread массивы живут дольше четырёх кадров, которые живёт TempJob
        timer.Restart();
        rebuildOrigins = new NativeArray<int3>(count, Allocator.Persistent);
        for (int k = 0; k < count; k++)
            rebuildOrigins[k] = ChunkCoord(dirtyChunks[k]) * ChunkSize;
        rebuildFaces = new NativeArray<byte>(count, Allocator.Persistent);
        var faces = rebuildFaces;
        var facesJob = new ChunkFacesJob
        {
            voxels = voxels,
            dims = dims,
            chunkSize = ChunkSize,
            chunkOrigins = rebuildOrigins,
            faces = faces,
        };
        if (inWorkers)
        {
            generationJobs = facesJob.Schedule(count, 16);
            JobHandle.ScheduleBatchedJobs();
            if (spread)
                while (!generationJobs.IsCompleted)
                    yield return null;
            generationJobs.Complete();
        }
        else
        {
            facesJob.Run(count);
        }
        rebuildOrigins.Dispose();
        double meshMs = timer.Elapsed.TotalMilliseconds, applyMs = 0;

        // Чанки строятся пачками: данные мешей сразу всех 15 000 чанков арены при генерации заняли бы
        // сотни МБ, а куча WebGL не больше 2 ГБ. Пока главный поток загружает пачку в Unity, рабочие
        // потоки уже строят следующую. Без рабочих потоков (WebGL) вторая пачка только заняла бы память
        bool overlap = inWorkers && JobsUtility.JobWorkerCount > 0;
        var batch = meshBatches[0];
        ScheduleBatch(batch, 0, faces, inWorkers);
        while (true)
        {
            // 1. Меши: отрисовки, форма суши и форма воды — отдельные задачи одной джобы
            timer.Restart();
            if (spread)
                while (!batch.handle.IsCompleted)
                    yield return null;
            batch.handle.Complete();
            meshMs += timer.Elapsed.TotalMilliseconds;

            int nextStart = batch.start + batch.count;
            bool more = nextStart < count;
            var next = overlap ? meshBatches[batch == meshBatches[0] ? 1 : 0] : batch;
            if (more && overlap)
                ScheduleBatch(next, nextStart, faces, inWorkers);

            // 2. Загрузка в Mesh и MeshCollider
            timer.Restart();
            ApplyBatch(batch, inWorkers);
            applyMs += timer.Elapsed.TotalMilliseconds;
            if (IsGenerating)
                GenerationProgress = math.lerp(MeshesFrom, CombineFrom, (float)nextStart / count);

            if (!more)
                break;
            if (!overlap)
                ScheduleBatch(next, nextStart, faces, inWorkers);
            batch = next;
            // Кадр с прогрессом, пока рабочие потоки строят следующую пачку
            if (spread)
                yield return null;
        }
        rebuildFaces.Dispose();
        LastMeshMs = meshMs;
        LastApplyMs = applyMs;

        // 3. Узлы склеиваются из готовых мешей чанков, пачками по CombineNodeBatch
        if (IsGenerating)
            GenerationStage = "Склейка узлов";
        timer.Restart();
        // Пачка склеивается за единицы мс: ждать её кадрами дольше, чем склеить. Кадр отпускается,
        // только когда с прошлого кадра прошло CombineFrameBudgetMs, — экран загрузки не замирает
        frameTimer.Restart();
        int nodeCount = dirtyNodes.Count;
        for (int from = 0; from < nodeCount; from += CombineNodeBatch)
        {
            int batchNodes = Math.Min(CombineNodeBatch, nodeCount - from);
            CombineNodes(from, batchNodes, inWorkers);
            if (spread && from + batchNodes < nodeCount && frameTimer.Elapsed.TotalMilliseconds > CombineFrameBudgetMs)
            {
                GenerationProgress = math.lerp(CombineFrom, 1f, (float)(from + batchNodes) / nodeCount);
                yield return null;
                frameTimer.Restart();
            }
        }
        dirtyNodes.Clear();
        LastCombineMs = timer.Elapsed.TotalMilliseconds;
        LastRebuiltChunks = count;

        foreach (int index in dirtyChunks)
        {
            var chunk = chunks[index];
            chunk.dirty = false;

            ChunkMeshChanged?.Invoke(
                index,
                chunk.hasLand ? chunk.shape : null,
                chunk.hasWater ? chunk.waterShape : null,
                chunk.root.transform.localToWorldMatrix);
        }

        dirtyChunks.Clear();
    }

    // Запускает GreedyMeshJob пачки dirtyChunks[start ..]. Чанку без граней суши не нужна форма суши,
    // без граней воды — форма воды, без граней вообще — и меш отрисовки: задач для них нет
    void ScheduleBatch(MeshBatch batch, int start, NativeArray<byte> faces, bool inWorkers)
    {
        batch.start = start;
        batch.count = Math.Min(ChunkBatch, faces.Length - start);
        // Persistent: при генерации по кадрам задачи пачки живут через кадр
        batch.tasks = new NativeList<ChunkMeshTask>(batch.count * 3, Allocator.Persistent);
        batch.meshes.Clear();

        for (int k = 0; k < batch.count; k++)
        {
            var chunk = chunks[dirtyChunks[start + k]];
            int3 origin = ChunkCoord(dirtyChunks[start + k]) * ChunkSize;
            byte chunkFaces = faces[start + k];

            batch.colorTask[k] = batch.shapeTask[k] = batch.waterShapeTask[k] = -1;
            bool landFaces = (chunkFaces & ChunkFacesJob.LandFaces) != 0;
            bool waterFaces = (chunkFaces & ChunkFacesJob.WaterFaces) != 0;
            if (landFaces || waterFaces)
                batch.colorTask[k] = AddTask(batch, origin, ChunkMeshKind.Color, chunk.mesh);
            if (landFaces)
                batch.shapeTask[k] = AddTask(batch, origin, ChunkMeshKind.Shape, chunk.shape);
            if (waterFaces)
                batch.waterShapeTask[k] = AddTask(batch, origin, ChunkMeshKind.WaterShape, chunk.waterShape);
        }

        int taskCount = batch.tasks.Length;
        batch.data = Mesh.AllocateWritableMeshData(taskCount);
        batch.scheduled = true;
        var job = new GreedyMeshJob
        {
            voxels = voxels,
            faceColors = faceColors,
            dims = dims,
            chunkSize = ChunkSize,
            tasks = batch.tasks.AsArray(),
            colorLayout = colorLayout,
            shapeLayout = shapeLayout,
            meshes = batch.data,
            scratch = meshScratch,
        };

        if (inWorkers)
        {
            batch.handle = job.Schedule(taskCount, 1);
            // Schedule только ставит джобу в очередь — рабочие потоки берутся за неё после этого вызова
            JobHandle.ScheduleBatchedJobs();
        }
        else
        {
            job.Run(taskCount);
            batch.handle = default;
        }
    }

    static int AddTask(MeshBatch batch, int3 origin, ChunkMeshKind kind, Mesh mesh)
    {
        batch.tasks.Add(new ChunkMeshTask { origin = origin, kind = kind });
        batch.meshes.Add(mesh);
        return batch.tasks.Length - 1;
    }

    // Загружает готовые меши пачки в Mesh (одним Mesh.ApplyAndDisposeWritableMeshData) и MeshCollider
    void ApplyBatch(MeshBatch batch, bool inWorkers)
    {
        // Коллайдеры заранее готовятся только в рабочих потоках: в главном это сделает сам MeshCollider
        var bakeMeshes = inWorkers ? new NativeArray<EntityId>(batch.count, Allocator.TempJob) : default;
        int bakeCount = 0;

        for (int k = 0; k < batch.count; k++)
        {
            int index = dirtyChunks[batch.start + k];
            var chunk = chunks[index];
            bool hadLand = chunk.hasLand, hadWater = chunk.hasWater;
            batch.hadLand[k] = hadLand;

            // Чанк был пустым и остался пустым (воздух над картой, порода под ней — большая часть арены
            // при генерации): меши уже пусты, коллайдер выключен, узел перестраивать незачем
            if (!hadLand && !hadWater && batch.colorTask[k] < 0)
                continue;

            // Переназначение заставляет коллайдер взять новые данные
            if (hadLand)
                chunk.collider.sharedMesh = null;

            // Меш отрисовки: суша и вода — его сабмеши 0 и 1
            int landIndices = 0, waterIndices = 0;
            int colorTask = batch.colorTask[k];
            if (colorTask < 0)
            {
                chunk.mesh.Clear();
            }
            else
            {
                landIndices = batch.data[colorTask].GetSubMesh(0).indexCount;
                waterIndices = batch.data[colorTask].GetSubMesh(1).indexCount;
            }
            int shapeIndices = TaskIndexCount(batch, batch.shapeTask[k], chunk.shape, hadLand);
            TaskIndexCount(batch, batch.waterShapeTask[k], chunk.waterShape, hadWater);
            chunk.hasLand = landIndices > 0;
            chunk.hasWater = waterIndices > 0;
            if (chunk.hasLand && inWorkers)
                bakeMeshes[bakeCount++] = chunk.shape.GetEntityId();

            var node = NodeOfChunk(index);
            if (!node.dirty)
            {
                node.dirty = true;
                dirtyNodes.Add(node);
            }

            int triangles = (landIndices + waterIndices) / 3;
            int shapeTriangles = shapeIndices / 3;
            TotalTriangles += triangles - chunk.triangles;
            TotalShapeTriangles += shapeTriangles - chunk.shapeTriangles;
            chunk.triangles = triangles;
            chunk.shapeTriangles = shapeTriangles;
        }

        // Границы уже заданы в подмешах джобой; данные остаются читаемыми — их берут коллайдер и NavMesh
        Mesh.ApplyAndDisposeWritableMeshData(batch.data, batch.meshes,
            MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
        batch.tasks.Dispose();
        batch.scheduled = false;
        var bounds = new Bounds(Vector3.one * (ChunkSize * 0.5f), Vector3.one * ChunkSize);
        foreach (var mesh in batch.meshes)
            mesh.bounds = bounds;

        // Данные PhysX для коллайдеров готовятся во всех рабочих потоках: назначенный меш коллайдер
        // берёт уже готовым. Без этого он готовил бы их сам в главном потоке, чанк за чанком
        if (inWorkers)
        {
            if (bakeCount > 0)
            {
                new BakeColliderJob { meshes = bakeMeshes, options = ColliderCooking }
                    .Schedule(bakeCount, 4)
                    .Complete();
            }
            bakeMeshes.Dispose();
        }

        for (int k = 0; k < batch.count; k++)
        {
            var chunk = chunks[dirtyChunks[batch.start + k]];
            // Без суши до и после коллайдер так и остаётся выключенным и без меша
            if (!chunk.hasLand && !batch.hadLand[k])
                continue;
            chunk.collider.enabled = chunk.hasLand;
            if (chunk.hasLand)
                chunk.collider.sharedMesh = chunk.shape;
        }
    }

    // Число индексов меша задачи; task < 0 — задачи нет (у чанка нет граней этого меша),
    // меш очищается, если в нём что-то было
    static int TaskIndexCount(MeshBatch batch, int task, Mesh mesh, bool hadMesh)
    {
        if (task < 0)
        {
            if (hadMesh)
                mesh.Clear();
            return 0;
        }
        return batch.data[task].GetSubMesh(0).indexCount;
    }

    // Узлов в одной пачке склейки
    const int CombineNodeBatch = 128;
    // При генерации по кадрам склейка отпускает кадр не чаще, чем раз в столько мс
    const double CombineFrameBudgetMs = 33;
    // Сколько чанков за раз строится в главном потоке, а не в рабочих
    const int MainThreadChunkLimit = 12;
    // Чанков в одной пачке перестройки
    const int ChunkBatch = 256;
    // Настройки подготовки коллайдеров чанков. Physics.BakeMesh и MeshCollider должны использовать
    // одни и те же, иначе коллайдер заново готовит данные при назначении меша
    const MeshColliderCookingOptions ColliderCooking =
        MeshColliderCookingOptions.CookForFasterSimulation |
        MeshColliderCookingOptions.EnableMeshCleaning |
        MeshColliderCookingOptions.WeldColocatedVertices |
        MeshColliderCookingOptions.UseFastMidphase;
}
