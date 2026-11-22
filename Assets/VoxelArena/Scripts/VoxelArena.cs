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
public partial class VoxelArena : MonoBehaviour
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

    /// <summary>
    /// Сила разлёта осколков у выбивания, о котором сейчас сообщает VoxelsRemoved: 1 — пуля и обвал, больше — взрыв.
    /// Читать внутри обработчика VoxelsRemoved (VoxelDebris), вне его значение от прошлого выбивания
    /// </summary>
    public float DebrisPower { get; private set; } = 1f;

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

    /// <summary>Мировая точка на поверхности посередине клетки раскладки</summary>
    public Vector3 CellTop(int2 cell)
    {
        int k = cellVoxels;
        return ColumnTop(cell.x * k + k / 2, cell.y * k + k / 2);
    }

    /// <summary>
    /// Выбивает воксели шаром (во время генерации — ничего). Возвращает число выбитых. maxDepth — на сколько вокселей ниже исходной
    /// поверхности столбца можно копать (воронки в бою — на 1: агент перешагивает уступ в один воксель,
    /// глубже — поле воронок становится непроходимым и отрезает базу). debrisPower — сила разлёта осколков (DebrisPower)
    /// </summary>
    public int Explode(Vector3 worldCenter, float worldRadius, int maxDepth = int.MaxValue, float debrisPower = 1f)
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
            DebrisPower = debrisPower;
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
            {
                DebrisPower = 1f;
                VoxelsRemoved?.Invoke(VoxelToWorld((float3)(min + max + 1) * 0.5f), removedVoxels);
            }
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
}
