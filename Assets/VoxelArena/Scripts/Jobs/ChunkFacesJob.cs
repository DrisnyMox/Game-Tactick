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

        // Кайма по x за краем арены — воздух
        if (origin.x == 0 || origin.x + chunkSize >= dims.x)
            hasAir = true;
        int x0 = math.max(origin.x - 1, 0);
        int x1 = math.min(origin.x + chunkSize, dims.x - 1);

        for (int ly = -1; ly <= chunkSize; ly++)
        {
            int y = origin.y + ly;
            // Ниже арены — порода, не суша чанка; выше — воздух
            if (y < 0)
                continue;
            if (y >= dims.y)
            {
                hasAir = true;
                continue;
            }

            for (int lz = -1; lz <= chunkSize; lz++)
            {
                int z = origin.z + lz;
                if (z < 0 || z >= dims.z)
                {
                    hasAir = true;
                    continue;
                }

                bool insideRow = ly >= 0 && ly < chunkSize && lz >= 0 && lz < chunkSize;
                int row = (y * dims.z + z) * dims.x;
                for (int x = x0; x <= x1; x++)
                {
                    byte block = voxels[row + x];
                    bool inside = insideRow && x >= origin.x && x < origin.x + chunkSize;

                    if (block == VoxelBlocks.Air)
                        hasAir = true;
                    else if (block == VoxelBlocks.Water)
                    {
                        hasWaterAround = true;
                        hasWater |= inside;
                    }
                    else
                        hasLand |= inside;
                }

                // Оба меша уже точно нужны: дальше смотреть незачем
                if (hasLand && hasWater && hasAir)
                    break;
            }
            if (hasLand && hasWater && hasAir)
                break;
        }

        byte result = 0;
        // Суша видна сквозь воду, вода — только на границе с воздухом
        if (hasLand && (hasAir || hasWaterAround))
            result |= LandFaces;
        if (hasWater && hasAir)
            result |= WaterFaces;
        faces[index] = result;
    }
}
