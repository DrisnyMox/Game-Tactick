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

// Генерация карты: раскладка, перенос её в воксели, меши всех чанков
public partial class VoxelArena
{
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
}
