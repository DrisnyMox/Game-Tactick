using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// NavMesh арены по областям, как в Blocks World (NPCNavigationSystem): у каждой области в regionChunks × regionChunks
/// столбцов чанков своя NavMeshData. Изменённый чанк пересобирает только свою область и соседние, чьи края он задевает.
/// Одна NavMeshData на всю арену так не может: UpdateNavMeshDataAsync перед сборкой сверяет контрольные суммы всех
/// источников в своей коробке (на арене 800×800 вокселей ~300 мс рабочих потоков, физика кадра ждёт их), а с коробкой
/// меньше арены выкидывает плитки за коробкой.
/// Соседние NavMeshData Unity не соединяет. Поэтому на каждом шве между областями стоят связки (NavMesh.AddLink):
/// пол столбца по одну сторону шва и пол столбца по другую на той же высоте или на воксель выше/ниже. Связки соседних
/// столбцов с одинаковыми высотами склеены в одну широкую.
/// Источники области - меши формы своих чанков и чанков на один вокруг: по ним Recast видит геометрию за краем
/// области, и NavMesh доходит до шва без отступа на радиус агента.
/// После генерации все области собираются разом (рабочие потоки заняты, но это загрузка), потом связки всех швов.
/// Дальше изменённые области встают в очередь и собираются по одной: сборка области стоит единицы миллисекунд.
/// Два NavMesh из одних источников: пехота (тип агента из настроек) и техника (свой тип с большим радиусом,
/// создаётся в коде). У каждого свои связки.
/// У каждого чанка два источника: суша (ходить можно) и вода (Not Walkable) -
/// непроходимая поверхность воды закрывает и дно под ней.
/// </summary>
[RequireComponent(typeof(VoxelArena))]
public class ArenaNavMesh : MonoBehaviour
{
    [SerializeField] int agentTypeID = 0;
    [Tooltip("Сколько клеток навмеша приходится на один воксель по ширине")]
    [SerializeField] int navCellsPerVoxel = 3;
    [Tooltip("Высота уступа, на который агент заходит без линков, в вокселях")]
    [SerializeField] float stepHeightInVoxels = 1.05f;
    [Tooltip("Сторона области NavMesh в чанках. Область меньше - пересборка дешевле, но больше швов и связок")]
    [SerializeField, Min(1)] int regionChunks = 2;

    [Tooltip("Радиус агента техники, м (танк ~2 м шириной): технике — свой NavMesh, без узких мест")]
    [SerializeField] float vehicleRadius = 1.1f;
    [Tooltip("Высота агента техники, м")]
    [SerializeField] float vehicleHeight = 1.8f;

    VoxelArena arena;

    /// <summary>Тип агента: пехота (и строители) или техника</summary>
    class Layer
    {
        public NavMeshBuildSettings settings;
        /// <summary>Тип агента создан этим компонентом (NavMesh.CreateSettings) — удалить при выходе</summary>
        public bool created;
        /// <summary>Сколько вокселей воздуха нужно над полом, чтобы агент прошёл: по ним ставятся связки</summary>
        public int clearance;
        /// <summary>Через сколько столбцов от шва стоят концы связки: Recast срезает NavMesh области на радиус
        /// агента от края её коробки, конец связки должен лечь за этот срез</summary>
        public int reach;
    }

    // [0] — пехота, [1] — техника
    Layer[] layers;
    int vehicleAgentTypeID;

    /// <summary>Область: по NavMeshData на тип агента. queued - стоит в очереди пересборки</summary>
    class Region
    {
        public NavMeshData[] data;
        public NavMeshDataInstance[] instances;
        public bool queued;
    }

    Region[] regions;
    int regionsX, regionsZ;
    int chunksX, chunksY, chunksZ;
    const int NotWalkableArea = 1;

    // Меши формы суши и воды каждого чанка (null - пусто) и матрица чанка: из них собираются источники областей
    Mesh[] landMeshes;
    Mesh[] waterMeshes;
    Matrix4x4[] chunkMatrices;

    // Изменённые области: собираются по одной, следующая - когда закончилась предыдущая
    readonly Queue<int> regionQueue = new();
    // Идущие сборки: после генерации их много сразу, потом одна. Область и тип агента каждой
    readonly List<AsyncOperation> running = new();
    readonly List<int> runningRegions = new();
    // Сборка после генерации: все области разом, потом связки всех швов
    bool fullBuild;

    // Источники одной сборки. Каждая сборка получает свой список: сборки после генерации идут одновременно
    readonly List<List<NavMeshBuildSource>> sourceLists = new();

    /// <summary>Связка на шве: где стоит, повёрнута ли вдоль X, перепад высот и ширина. По ним сверяются старые
    /// связки шва с новыми: одинаковые остаются на месте, вызовов навигации у них нет</summary>
    struct SeamLink
    {
        public Vector3 position;
        public bool crossX;
        public float rise;
        public float width;
        public NavMeshLinkInstance instance;

        public bool SameAs(SeamLink other) =>
            position == other.position && crossX == other.crossX && rise == other.rise && width == other.width;
    }

    // Связки швов по типу агента: ключ - шов (область * 2 + 0 левый шов / 1 задний)
    Dictionary<int, List<SeamLink>>[] seamLinks;
    readonly List<SeamLink> desiredLinks = new();
    // Склейка связок на шве. openRuns - открытые полосы: (высота пола у первого конца, перепад до второго) -> начало
    // полосы вдоль шва; seamPairs - такие пары на текущем месте шва; closedRuns - полосы, которые на нём кончились
    readonly Dictionary<(int y, int rise), int> openRuns = new();
    readonly List<(int y, int rise)> seamPairs = new();
    readonly List<(int y, int rise)> closedRuns = new();

    static readonly Unity.Profiling.ProfilerMarker StartMarker = new("ArenaNavMesh.StartUpdate");
    static readonly Unity.Profiling.ProfilerMarker LinksMarker = new("ArenaNavMesh.UpdateSeams");

    readonly Stopwatch timer = new();
    readonly Stopwatch fullTimer = new();
    readonly Stopwatch seamTimer = new();
    int startFrame;

    /// <summary>NavMesh собран для всех типов агентов</summary>
    public bool HasNavMesh { get; private set; }
    public bool IsBusy => running.Count > 0 || regionQueue.Count > 0;
    /// <summary>Последняя сборка одной области (оба типа агента)</summary>
    public double LastBuildMs { get; private set; }
    public int LastBuildFrames { get; private set; }
    /// <summary>Сборка всей арены после генерации, со связками</summary>
    public double FullBuildMs { get; private set; }
    public int QueuedRegions => regionQueue.Count;
    public int LinkCount { get; private set; }
    public double LastSeamsMs { get; private set; }
    public int TileSize => layers != null ? layers[0].settings.tileSize : 0;
    public int AgentTypeID => agentTypeID;
    /// <summary>Тип агента техники (создаётся при первой сборке; HasNavMesh — собран и его NavMesh). Идентификаторы
    /// типов агентов в Unity бывают и отрицательными, «нет типа» по знаку не определить</summary>
    public int VehicleAgentTypeID => vehicleAgentTypeID;

    void Awake()
    {
        arena = GetComponent<VoxelArena>();
        arena.ChunkMeshChanged += Chunk_MeshChanged;
        arena.Generated += Arena_Generated;
    }

    void OnDestroy()
    {
        arena.ChunkMeshChanged -= Chunk_MeshChanged;
        arena.Generated -= Arena_Generated;

        if (layers == null)
            return;
        foreach (var region in regions)
            foreach (var instance in region.instances)
                instance.Remove();
        foreach (var layerLinks in seamLinks)
            foreach (var links in layerLinks.Values)
                foreach (var link in links)
                    NavMesh.RemoveLink(link.instance);
        foreach (var layer in layers)
            if (layer.created)
                NavMesh.RemoveSettings(layer.settings.agentTypeID);
    }

    // Ленивая инициализация: первые события приходят из VoxelArena.Start,
    // к этому моменту размеры арены уже известны
    void Init()
    {
        var infantry = NavMesh.GetSettingsByID(agentTypeID);
        Configure(ref infantry);

        // Тип агента техники создаётся в коде — в настройках проекта его заводить не нужно
        var vehicle = NavMesh.CreateSettings();
        vehicle.agentRadius = vehicleRadius;
        vehicle.agentHeight = vehicleHeight;
        vehicle.agentSlope = infantry.agentSlope;
        Configure(ref vehicle);
        vehicleAgentTypeID = vehicle.agentTypeID;

        layers = new[] { CreateLayer(infantry, false), CreateLayer(vehicle, true) };

        var dims = arena.Dims;
        chunksX = dims.x / VoxelArena.ChunkSize;
        chunksY = dims.y / VoxelArena.ChunkSize;
        chunksZ = dims.z / VoxelArena.ChunkSize;
        landMeshes = new Mesh[arena.ChunkTotal];
        waterMeshes = new Mesh[arena.ChunkTotal];
        chunkMatrices = new Matrix4x4[arena.ChunkTotal];

        regionsX = (chunksX + regionChunks - 1) / regionChunks;
        regionsZ = (chunksZ + regionChunks - 1) / regionChunks;
        regions = new Region[regionsX * regionsZ];
        for (int i = 0; i < regions.Length; i++)
        {
            var region = new Region
            {
                data = new NavMeshData[layers.Length],
                instances = new NavMeshDataInstance[layers.Length],
            };
            for (int l = 0; l < layers.Length; l++)
            {
                // Начало у всех областей одно - угол арены: сетка плиток у них общая и совпадает с сеткой чанков
                region.data[l] = new NavMeshData(layers[l].settings.agentTypeID) { position = transform.position };
                region.instances[l] = NavMesh.AddNavMeshData(region.data[l]);
            }
            regions[i] = region;
        }

        seamLinks = new Dictionary<int, List<SeamLink>>[layers.Length];
        for (int l = 0; l < layers.Length; l++)
            seamLinks[l] = new Dictionary<int, List<SeamLink>>();
    }

    void Configure(ref NavMeshBuildSettings settings)
    {
        settings.overrideVoxelSize = true;
        settings.voxelSize = arena.VoxelSize / navCellsPerVoxel;

        // Плитка размером с чанк: края областей совпадают с краями плиток
        settings.overrideTileSize = true;
        settings.tileSize = VoxelArena.ChunkSize * navCellsPerVoxel;

        settings.agentClimb = Mathf.Max(settings.agentClimb, arena.VoxelSize * stepHeightInVoxels);
    }

    Layer CreateLayer(NavMeshBuildSettings settings, bool created) => new()
    {
        settings = settings,
        created = created,
        clearance = Mathf.CeilToInt(settings.agentHeight / arena.VoxelSize - 0.01f),
        reach = Mathf.CeilToInt((settings.agentRadius + 0.1f) / arena.VoxelSize),
    };

    void Chunk_MeshChanged(int chunk, Mesh land, Mesh water, Matrix4x4 localToWorld)
    {
        if (layers == null)
            Init();

        landMeshes[chunk] = land;
        waterMeshes[chunk] = water;
        chunkMatrices[chunk] = localToWorld;

        // Пока идёт сборка после генерации, её всё равно соберут целиком
        if (fullBuild || !HasNavMesh)
            return;

        // Чанк - источник своей области и соседних, у которых он в кольце на чанк вокруг
        int cx = chunk % chunksX;
        int cz = chunk / chunksX % chunksZ;
        for (int rz = Mathf.Max(0, (cz - 1) / regionChunks); rz <= Mathf.Min(regionsZ - 1, (cz + 1) / regionChunks); rz++)
            for (int rx = Mathf.Max(0, (cx - 1) / regionChunks); rx <= Mathf.Min(regionsX - 1, (cx + 1) / regionChunks); rx++)
                Enqueue(rz * regionsX + rx);
    }

    void Enqueue(int region)
    {
        if (regions[region].queued)
            return;
        regions[region].queued = true;
        regionQueue.Enqueue(region);
    }

    // Новая карта: все области разом, а очередь с прошлой карты не нужна
    void Arena_Generated()
    {
        if (layers == null)
            Init();

        HasNavMesh = false;
        fullBuild = true;
        regionQueue.Clear();
        foreach (var region in regions)
            region.queued = false;
    }

    void Update()
    {
        if (layers == null)
            return;

        for (int i = running.Count - 1; i >= 0; i--)
        {
            if (!running[i].isDone)
                continue;
            running.RemoveAt(i);
            int region = runningRegions[i];
            runningRegions.RemoveAt(i);
            // Область пересобрана целиком (все типы агентов) - её швы заново
            if (!fullBuild && !runningRegions.Contains(region))
            {
                LastBuildMs = timer.Elapsed.TotalMilliseconds;
                LastBuildFrames = Time.frameCount - startFrame;
                UpdateRegionSeams(region);
            }
        }
        if (running.Count > 0)
            return;

        if (fullBuild)
        {
            // Все области собраны: связки всех швов, NavMesh готов
            if (fullTimer.IsRunning)
            {
                var seamsTimer = Stopwatch.StartNew();
                for (int r = 0; r < regions.Length; r++)
                    UpdateSeams(r * 2, r * 2 + 1);
                fullBuild = false;
                HasNavMesh = true;
                fullTimer.Stop();
                FullBuildMs = fullTimer.Elapsed.TotalMilliseconds;
                UnityEngine.Debug.Log($"[Arena] NavMesh: областей {regions.Length} по {regionChunks}×{regionChunks} чанка, " +
                                      $"сборка всех {FullBuildMs:0} мс, из них связки швов {seamsTimer.Elapsed.TotalMilliseconds:0} мс, " +
                                      $"связок {LinkCount}");
                return;
            }
            fullTimer.Restart();
            for (int r = 0; r < regions.Length; r++)
                StartRegion(r);
            return;
        }

        if (regionQueue.Count == 0)
            return;
        int next = regionQueue.Dequeue();
        regions[next].queued = false;
        timer.Restart();
        startFrame = Time.frameCount;
        StartRegion(next);
    }

    // Сборка области для всех типов агентов. Коробка - столбцы чанков области по всей высоте, чуть внутрь от краёв:
    // коробка, коснувшаяся соседней плитки, собрала бы и её
    void StartRegion(int index)
    {
        using var marker = StartMarker.Auto();

        int rx = index % regionsX, rz = index / regionsX;
        int x0 = rx * regionChunks, z0 = rz * regionChunks;
        int x1 = Mathf.Min(chunksX, x0 + regionChunks), z1 = Mathf.Min(chunksZ, z0 + regionChunks);
        float chunkWorld = VoxelArena.ChunkSize * arena.VoxelSize;
        const float gap = 0.01f;
        var bounds = new Bounds();
        bounds.SetMinMax(new Vector3(x0 * chunkWorld + gap, -4f, z0 * chunkWorld + gap),
                         new Vector3(x1 * chunkWorld - gap, chunksY * chunkWorld + 4f, z1 * chunkWorld - gap));

        // Источники: чанки области и на один вокруг, в одном порядке от сборки к сборке (UpdateNavMeshDataAsync
        // сверяет источники по порядку)
        var sources = TakeSourceList();
        for (int cy = 0; cy < chunksY; cy++)
            for (int cz = Mathf.Max(0, z0 - 1); cz < Mathf.Min(chunksZ, z1 + 1); cz++)
                for (int cx = Mathf.Max(0, x0 - 1); cx < Mathf.Min(chunksX, x1 + 1); cx++)
                {
                    int chunk = (cy * chunksZ + cz) * chunksX + cx;
                    if (landMeshes[chunk] != null)
                        sources.Add(Source(landMeshes[chunk], chunkMatrices[chunk], 0));
                    if (waterMeshes[chunk] != null)
                        sources.Add(Source(waterMeshes[chunk], chunkMatrices[chunk], NotWalkableArea));
                }

        for (int l = 0; l < layers.Length; l++)
        {
            running.Add(NavMeshBuilder.UpdateNavMeshDataAsync(regions[index].data[l], layers[l].settings, sources, bounds));
            runningRegions.Add(index);
        }
    }

    // Список источников для новой сборки: списки идущих сборок не трогаются
    List<NavMeshBuildSource> TakeSourceList()
    {
        int busy = running.Count / layers.Length;
        while (sourceLists.Count <= busy)
            sourceLists.Add(new List<NavMeshBuildSource>());
        var list = sourceLists[busy];
        list.Clear();
        return list;
    }

    static NavMeshBuildSource Source(Mesh mesh, Matrix4x4 localToWorld, int area) => new()
    {
        shape = NavMeshBuildSourceShape.Mesh,
        sourceObject = mesh,
        transform = localToWorld,
        area = area,
    };

    // Четыре шва области: свои левый и задний, правый и передний - это левый и задний соседей
    void UpdateRegionSeams(int region)
    {
        int rx = region % regionsX, rz = region / regionsX;
        UpdateSeams(region * 2, region * 2 + 1);
        if (rx + 1 < regionsX)
            UpdateSeams((region + 1) * 2, -1);
        if (rz + 1 < regionsZ)
            UpdateSeams((region + regionsX) * 2 + 1, -1);
    }

    void UpdateSeams(int seamA, int seamB)
    {
        using var marker = LinksMarker.Auto();
        seamTimer.Restart();
        for (int l = 0; l < layers.Length; l++)
        {
            UpdateSeam(l, seamA);
            if (seamB >= 0)
                UpdateSeam(l, seamB);
        }
        LastSeamsMs = seamTimer.Elapsed.TotalMilliseconds;
    }

    // Связки шва заново по вокселям: одинаковые со старыми остаются, лишние снимаются, новые ставятся
    void UpdateSeam(int layer, int seam)
    {
        int region = seam / 2;
        bool left = seam % 2 == 0;
        int rx = region % regionsX, rz = region / regionsX;
        // У крайних областей арены нет шва с той стороны
        if ((left && rx == 0) || (!left && rz == 0))
            return;

        desiredLinks.Clear();
        ComputeSeam(layers[layer], left, rx, rz);

        if (!seamLinks[layer].TryGetValue(seam, out var current))
        {
            current = new List<SeamLink>();
            seamLinks[layer][seam] = current;
        }

        // Старые: есть такая же среди нужных - остаётся (нужная вычёркивается), нет - снимается
        for (int i = current.Count - 1; i >= 0; i--)
        {
            // Циклом, а не FindIndex с лямбдой: лямбда с захватом выделяет память
            int same = -1;
            for (int d = 0; d < desiredLinks.Count && same < 0; d++)
                if (desiredLinks[d].SameAs(current[i]))
                    same = d;
            if (same >= 0)
            {
                desiredLinks.RemoveAt(same);
                continue;
            }
            NavMesh.RemoveLink(current[i].instance);
            current.RemoveAt(i);
            LinkCount--;
        }

        var agent = layers[layer].settings.agentTypeID;
        // Концы связки - на середине столбцов в reach от шва, за полосой, где NavMesh области срезан
        float half = (layers[layer].reach + 0.5f) * arena.VoxelSize;
        foreach (var link in desiredLinks)
        {
            var added = link;
            var data = new NavMeshLinkData
            {
                startPosition = new Vector3(0f, 0f, -half),
                endPosition = new Vector3(0f, link.rise, half),
                width = link.width,
                costModifier = -1,
                bidirectional = true,
                area = 0,
                agentTypeID = agent,
            };
            // Ось связки - её локальная z: поперёк шва. Шов слева области переходят вдоль X
            var rotation = link.crossX ? Quaternion.Euler(0f, 90f, 0f) : Quaternion.identity;
            added.instance = NavMesh.AddLink(data, link.position, rotation);
            current.Add(added);
            LinkCount++;
        }
    }

    // Нужные связки шва в desiredLinks. Шов слева области (left) лежит между столбцами x = X - 1 и X, задний - между
    // z = Z - 1 и Z. Пол столбца - твёрдый воксель (не воздух и не вода), над которым clearance вокселей воздуха.
    // От пары полов у самого шва (по ту сторону на той же высоте или на шаг агента выше/ниже) путь идёт по полам
    // столбцов поперёк шва, на каждом столбце не выше шага, до столбцов в reach от шва с обеих сторон: там концы связки.
    // Соседние вдоль шва места с одинаковыми высотами концов - одна полоса, одна связка
    void ComputeSeam(Layer layer, bool left, int rx, int rz)
    {
        var voxels = arena.Voxels;
        var dims = arena.Dims;
        int clearance = layer.clearance;
        int reach = layer.reach;
        int boundary = (left ? rx : rz) * regionChunks * VoxelArena.ChunkSize;
        int start = (left ? rz : rx) * regionChunks * VoxelArena.ChunkSize;
        int end = Mathf.Min(start + regionChunks * VoxelArena.ChunkSize, left ? dims.z : dims.x);
        int climb = Mathf.Max(1, Mathf.FloorToInt(stepHeightInVoxels));
        // Концы связки за краем арены не встанут
        if (boundary - 1 - reach < 0 || boundary + reach >= (left ? dims.x : dims.z))
            return;

        openRuns.Clear();
        for (int along = start; along <= end; along++)
        {
            // Пары высот концов на этом месте шва
            seamPairs.Clear();
            if (along < end)
            {
                for (int y = 0; y < dims.y - 1; y++)
                {
                    if (!IsFloor(along, -1, y))
                        continue;
                    int after = Step(along, 0, y);
                    if (after < 0)
                        continue;
                    int from = Follow(along, -1, -1, y);
                    int to = Follow(along, 0, 1, after);
                    if (from >= 0 && to >= 0 && !seamPairs.Contains((from, to - from)))
                        seamPairs.Add((from, to - from));
                }
            }

            // Полосы, которые здесь не продолжаются, закрываются; новые пары открывают полосы
            closedRuns.Clear();
            foreach (var run in openRuns.Keys)
                if (!seamPairs.Contains(run))
                    closedRuns.Add(run);
            foreach (var run in closedRuns)
            {
                AddRun(run.y, run.rise, openRuns[run], along, left, boundary);
                openRuns.Remove(run);
            }
            foreach (var pair in seamPairs)
                openRuns.TryAdd(pair, along);
        }

        // Пол на соседнем столбце (offset от шва) у высоты y: та же высота, потом ступенька вверх и вниз до climb. -1 - нет
        int Step(int along, int offset, int y)
        {
            for (int step = 0; step <= climb * 2; step++)
            {
                int rise = step == 0 ? 0 : (step % 2 == 1 ? (step + 1) / 2 : -step / 2);
                if (IsFloor(along, offset, y + rise))
                    return y + rise;
            }
            return -1;
        }

        // От пола y на столбце offset шагами по direction до столбца в reach от шва: высота пола там или -1
        int Follow(int along, int offset, int direction, int y)
        {
            for (int i = 1; i <= reach && y >= 0; i++)
                y = Step(along, offset + direction * i, y);
            return y;
        }

        // offset - столбец поперёк шва: -1 - первый до шва, 0 - первый после
        bool IsFloor(int along, int offset, int y)
        {
            var column = left ? (x: boundary + offset, z: along) : (x: along, z: boundary + offset);
            return IsFloorAt(column, y);
        }

        bool IsFloorAt((int x, int z) column, int y)
        {
            if (y < 0 || y >= dims.y)
                return false;
            if (!Solid(voxels[Index(column.x, y, column.z)]))
                return false;
            for (int k = 1; k <= clearance && y + k < dims.y; k++)
                if (voxels[Index(column.x, y + k, column.z)] != VoxelBlocks.Air)
                    return false;
            return true;
        }

        int Index(int x, int y, int z) => (y * dims.z + z) * dims.x + x;
    }

    static bool Solid(byte block) => block != VoxelBlocks.Air && block != VoxelBlocks.Water;

    // Полоса мест [from, to) вдоль шва с полом конца до шва на высоте y и конца после шва на y + rise: связка
    // посередине полосы, на шве, на высоте верхней грани пола первого конца
    void AddRun(int y, int rise, int from, int to, bool left, int boundary)
    {
        float size = arena.VoxelSize;
        float middle = (from + to) * 0.5f * size;
        var local = left
            ? new Vector3(boundary * size, (y + 1) * size, middle)
            : new Vector3(middle, (y + 1) * size, boundary * size);
        desiredLinks.Add(new SeamLink
        {
            position = transform.position + local,
            crossX = left,
            rise = rise * size,
            width = (to - from) * size,
        });
    }
}
