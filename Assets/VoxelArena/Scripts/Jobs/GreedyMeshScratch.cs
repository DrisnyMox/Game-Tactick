using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Рабочие списки GreedyMesher одного потока. Живут всё время арены и переиспользуются задачами:
/// без выделения памяти на каждую задачу
/// </summary>
public struct GreedyMeshScratch
{
    public UnsafeList<byte> blocks;
    public UnsafeList<int> mask;
    public UnsafeList<float3> positions;
    public UnsafeList<float3> normals;
    public UnsafeList<Color32> colors;
    public UnsafeList<uint> indices;

    public static NativeArray<GreedyMeshScratch> Create(Allocator allocator) =>
        new(Unity.Jobs.LowLevel.Unsafe.JobsUtility.ThreadIndexCount, allocator);

    public static void Dispose(NativeArray<GreedyMeshScratch> scratch)
    {
        foreach (var s in scratch)
        {
            // Списки потока создаются при первой его задаче
            if (!s.blocks.IsCreated)
                continue;
            s.blocks.Dispose();
            s.mask.Dispose();
            s.positions.Dispose();
            s.normals.Dispose();
            s.colors.Dispose();
            s.indices.Dispose();
        }
        scratch.Dispose();
    }
}
