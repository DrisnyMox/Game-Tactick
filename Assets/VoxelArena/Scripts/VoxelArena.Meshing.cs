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

// Чанки и узлы отрисовки: перестройка мешей и коллайдеров после правки вокселей
public partial class VoxelArena
{
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
