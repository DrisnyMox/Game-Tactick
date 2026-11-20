using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

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
