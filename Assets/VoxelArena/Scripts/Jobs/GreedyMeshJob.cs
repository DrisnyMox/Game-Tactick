using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Строит меши пачки чанков и пишет их прямо в Mesh.MeshDataArray: в главном потоке остаётся
/// только Mesh.ApplyAndDisposeWritableMeshData. Задача index пишет в meshes[index].
/// Чанки без видимых граней сюда не попадают: их отсеивает ChunkFacesJob.
/// Вершины меша отрисовки — три потока: позиция (float3), нормаль (float3), цвет (4 байта:
/// r — индекс палитры, g — затенение угла). У него два сабмеша на общих вершинах: 0 — суша, 1 — вода
/// (у каждого свой материал). У форм — только позиция и один сабмеш.
/// </summary>
[BurstCompile]
public struct GreedyMeshJob : IJobParallelFor
{
    [ReadOnly] public NativeArray<byte> voxels;
    [ReadOnly] public NativeArray<byte> faceColors;
    public int3 dims;
    public int chunkSize;
    [ReadOnly] public NativeArray<ChunkMeshTask> tasks;
    // Раскладка вершин меша отрисовки (позиция, нормаль, цвет) и форм (позиция)
    [ReadOnly] public NativeArray<VertexAttributeDescriptor> colorLayout;
    [ReadOnly] public NativeArray<VertexAttributeDescriptor> shapeLayout;
    public Mesh.MeshDataArray meshes;

    // Списки по номеру потока: задачи одного потока идут друг за другом
    [NativeDisableParallelForRestriction] public NativeArray<GreedyMeshScratch> scratch;
    [NativeSetThreadIndex] int threadIndex;

    public void Execute(int index)
    {
        var task = tasks[index];
        bool plain = task.kind != ChunkMeshKind.Color;

        var lists = scratch[threadIndex];
        if (!lists.blocks.IsCreated)
        {
            int padded = chunkSize + 2;
            lists.blocks = new UnsafeList<byte>(padded * padded * padded, Allocator.Persistent);
            lists.mask = new UnsafeList<int>(chunkSize * chunkSize, Allocator.Persistent);
            lists.positions = new UnsafeList<float3>(1024, Allocator.Persistent);
            lists.normals = new UnsafeList<float3>(1024, Allocator.Persistent);
            lists.colors = new UnsafeList<Color32>(1024, Allocator.Persistent);
            lists.indices = new UnsafeList<uint>(1536, Allocator.Persistent);
        }
        lists.positions.Clear();
        lists.normals.Clear();
        lists.colors.Clear();
        lists.indices.Clear();

        var mesher = new GreedyMesher
        {
            faceColors = faceColors,
            chunkSize = chunkSize,
            water = task.kind == ChunkMeshKind.WaterShape,
            plain = plain,
            blocks = lists.blocks,
            mask = lists.mask,
            positions = lists.positions,
            normals = lists.normals,
            colors = lists.colors,
            indices = lists.indices,
        };
        mesher.CopyBlocks(voxels, dims, task.origin);
        mesher.Build();
        // Меш отрисовки: вода дописывается после суши в те же списки, её индексы — второй сабмеш
        int landIndices = mesher.indices.Length;
        if (!plain)
        {
            mesher.water = true;
            mesher.Build();
        }
        Write(meshes[index], ref mesher, plain, landIndices);

        // Списки могли вырасти и переехать: поток забирает их обратно
        lists.blocks = mesher.blocks;
        lists.mask = mesher.mask;
        lists.positions = mesher.positions;
        lists.normals = mesher.normals;
        lists.colors = mesher.colors;
        lists.indices = mesher.indices;
        scratch[threadIndex] = lists;
    }

    // landIndices — сколько индексов в начале списка у суши (сабмеш 0), остальные — вода (сабмеш 1).
    // У форм сабмеш один
    void Write(Mesh.MeshData data, ref GreedyMesher mesher, bool plain, int landIndices)
    {
        int vertexCount = mesher.positions.Length;
        int indexCount = mesher.indices.Length;

        data.SetVertexBufferParams(vertexCount, plain ? shapeLayout : colorLayout);
        var positions = data.GetVertexData<float3>(0);
        for (int i = 0; i < vertexCount; i++)
            positions[i] = mesher.positions[i];
        if (!plain)
        {
            var normals = data.GetVertexData<float3>(1);
            var colors = data.GetVertexData<Color32>(2);
            for (int i = 0; i < vertexCount; i++)
            {
                normals[i] = mesher.normals[i];
                colors[i] = mesher.colors[i];
            }
        }

        data.SetIndexBufferParams(indexCount, IndexFormat.UInt32);
        var indices = data.GetIndexData<uint>();
        for (int i = 0; i < indexCount; i++)
            indices[i] = mesher.indices[i];

        // Границы — весь чанк: по ним Unity решает, виден ли чанк камере
        var bounds = new Bounds(Vector3.one * (chunkSize * 0.5f), Vector3.one * chunkSize);
        const MeshUpdateFlags flags = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices;
        if (plain)
        {
            data.subMeshCount = 1;
            data.SetSubMesh(0, new SubMeshDescriptor(0, indexCount)
            {
                bounds = bounds,
                firstVertex = 0,
                vertexCount = vertexCount,
            }, flags);
            return;
        }

        data.subMeshCount = 2;
        data.SetSubMesh(0, new SubMeshDescriptor(0, landIndices)
        {
            bounds = bounds,
            firstVertex = 0,
            vertexCount = vertexCount,
        }, flags);
        data.SetSubMesh(1, new SubMeshDescriptor(landIndices, indexCount - landIndices)
        {
            bounds = bounds,
            firstVertex = 0,
            vertexCount = vertexCount,
        }, flags);
    }
}
