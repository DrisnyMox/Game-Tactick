using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

/// <summary>
/// Проверяет, есть ли в чанке видимые грани суши и воды. Чанки без граней (воздух над картой,
/// сплошная порода под землёй) — большая часть арены: им не нужны ни буферы, ни GreedyMeshJob.
/// Один обход куба чанка с каймой в один воксель на оба меша.
/// </summary>
[BurstCompile]
public struct ChunkFacesJob : IJobParallelFor
{
    public const byte LandFaces = 1;
    public const byte WaterFaces = 2;

    [ReadOnly] public NativeArray<byte> voxels;
    public int3 dims;
    public int chunkSize;
    [ReadOnly] public NativeArray<int3> chunkOrigins;
    // LandFaces | WaterFaces на каждый чанк
    [WriteOnly] public NativeArray<byte> faces;

    public void Execute(int index)
    {
        int3 origin = chunkOrigins[index];
        bool hasLand = false, hasWater = false, hasAir = false, hasWaterAround = false;

        for (int y = -1; y <= chunkSize; y++)
        {
            for (int z = -1; z <= chunkSize; z++)
            {
                for (int x = -1; x <= chunkSize; x++)
                {
                    byte block = GetBlock(origin + new int3(x, y, z));
                    bool inside = x >= 0 && y >= 0 && z >= 0 && x < chunkSize && y < chunkSize && z < chunkSize;

                    if (block == VoxelBlocks.Air)
                        hasAir = true;
                    else if (block == VoxelBlocks.Water)
                    {
                        hasWaterAround = true;
                        if (inside)
                            hasWater = true;
                    }
                    else if (inside)
                        hasLand = true;
                }
            }
        }

        byte result = 0;
        // Суша видна сквозь воду, вода — только на границе с воздухом
        if (hasLand && (hasAir || hasWaterAround))
            result |= LandFaces;
        if (hasWater && hasAir)
            result |= WaterFaces;
        faces[index] = result;
    }

    byte GetBlock(int3 p)
    {
        if (p.y < 0)
            return VoxelBlocks.Bedrock;

        if (p.x < 0 || p.z < 0 || p.x >= dims.x || p.y >= dims.y || p.z >= dims.z)
            return VoxelBlocks.Air;

        return voxels[(p.y * dims.z + p.z) * dims.x + p.x];
    }
}
