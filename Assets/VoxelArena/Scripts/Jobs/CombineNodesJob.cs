using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Склеивает узлы отрисовки из мешей отрисовки их чанков и пишет прямо в Mesh.MeshDataArray.
/// У меша чанка и узла одни и те же потоки вершин (позиция, нормаль, цвет) и два сабмеша:
/// 0 — суша, 1 — вода. Сабмеши склеиваются по отдельности: в узле сначала индексы суши всех чанков,
/// потом индексы воды, поэтому у узла тоже два сабмеша, каждый под своим материалом.
/// Узел n берёт части parts[nodeParts[n].x .. + nodeParts[n].y] и пишет в nodeMeshes[n].
/// </summary>
[BurstCompile]
public struct CombineNodesJob : IJobParallelFor
{
    [ReadOnly] public Mesh.MeshDataArray chunkMeshes;
    [ReadOnly] public NativeArray<NodePart> parts;
    // x — первая часть узла в parts, y — сколько их
    [ReadOnly] public NativeArray<int2> nodeParts;
    [ReadOnly] public NativeArray<VertexAttributeDescriptor> colorLayout;
    public float nodeSize;
    public Mesh.MeshDataArray nodeMeshes;

    public void Execute(int node)
    {
        int first = nodeParts[node].x, count = nodeParts[node].y;

        int vertexCount = 0, landCount = 0, waterCount = 0;
        for (int p = first; p < first + count; p++)
        {
            var chunk = chunkMeshes[parts[p].mesh];
            vertexCount += chunk.vertexCount;
            landCount += chunk.GetSubMesh(0).indexCount;
            waterCount += chunk.GetSubMesh(1).indexCount;
        }

        var data = nodeMeshes[node];
        data.SetVertexBufferParams(vertexCount, colorLayout);
        data.SetIndexBufferParams(landCount + waterCount, IndexFormat.UInt32);
        var positions = data.GetVertexData<float3>(0);
        var normals = data.GetVertexData<float3>(1);
        var colors = data.GetVertexData<Color32>(2);
        var indices = data.GetIndexData<uint>();

        int vertex = 0, land = 0, water = landCount;
        for (int p = first; p < first + count; p++)
        {
            var chunk = chunkMeshes[parts[p].mesh];
            float3 offset = parts[p].offset;
            int chunkVertices = chunk.vertexCount;

            var chunkPositions = chunk.GetVertexData<float3>(0);
            for (int i = 0; i < chunkVertices; i++)
                positions[vertex + i] = chunkPositions[i] + offset;
            NativeArray<float3>.Copy(chunk.GetVertexData<float3>(1), 0, normals, vertex, chunkVertices);
            NativeArray<Color32>.Copy(chunk.GetVertexData<Color32>(2), 0, colors, vertex, chunkVertices);

            // Индексы в сабмешах чанка — от начала его вершин (baseVertex 0)
            var chunkIndices = chunk.GetIndexData<uint>();
            var landSub = chunk.GetSubMesh(0);
            for (int i = 0; i < landSub.indexCount; i++)
                indices[land++] = chunkIndices[landSub.indexStart + i] + (uint)vertex;
            var waterSub = chunk.GetSubMesh(1);
            for (int i = 0; i < waterSub.indexCount; i++)
                indices[water++] = chunkIndices[waterSub.indexStart + i] + (uint)vertex;

            vertex += chunkVertices;
        }

        // Границы — весь узел: по ним Unity решает, виден ли узел камере
        var bounds = new Bounds(Vector3.one * (nodeSize * 0.5f), Vector3.one * nodeSize);
        const MeshUpdateFlags flags = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices;
        data.subMeshCount = 2;
        data.SetSubMesh(0, new SubMeshDescriptor(0, landCount)
        {
            bounds = bounds,
            firstVertex = 0,
            vertexCount = vertexCount,
        }, flags);
        data.SetSubMesh(1, new SubMeshDescriptor(landCount, waterCount)
        {
            bounds = bounds,
            firstVertex = 0,
            vertexCount = vertexCount,
        }, flags);
    }
}
