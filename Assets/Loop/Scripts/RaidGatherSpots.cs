using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Места добычи на карте вылазки: боксы вокселей у деревьев, камней, рудных бугров, руин и остовов.
/// Рейдер (ShooterBot) берёт место рядом с собой и стреляет в верхний воксель-материал, пока место не выбито.
/// </summary>
public sealed class RaidGatherSpots
{
    struct GatherSpot
    {
        public int3 min, max;
    }

    const int NearSpots = 5;

    readonly VoxelArena arena;
    readonly List<GatherSpot> spots = new();
    readonly int[] nearSpot = new int[NearSpots];
    readonly long[] nearDistance = new long[NearSpots];

    public RaidGatherSpots(VoxelArena arena) => this.arena = arena;

    /// <summary>Воксель на поверхности посередине клетки: от него ArenaDecorator строит постройки острова</summary>
    public static int3 SiteRoot(VoxelArena arena, int2 cell)
    {
        int k = arena.CellVoxels;
        int x = cell.x * k + k / 2, z = cell.y * k + k / 2;
        return new int3(x, arena.GroundY(x, z), z);
    }

    static GatherSpot Box(int3 root, int3 min, int3 max) => new GatherSpot { min = root + min, max = root + max };

    // Деревья, камни, рудные бугры, руины и остовы: боксы вокселей, где рейдеры добывают. Причалы не трогают.
    // На острове постройки в вокселях (ArenaDecorator): у деревьев низ ствола до 2,3 м, где пальма ещё не клонится,
    // руины 27×19 со стенами 14, остов до 25 вокселей от центра в любую сторону
    public void Collect(ArenaLayout layout)
    {
        int k = arena.CellVoxels;
        spots.Clear();
        bool island = layout.wadeableWater;
        foreach (var cell in layout.trees)
        {
            int h = layout.height[cell.y * layout.sizeX + cell.x];
            spots.Add(island
                ? Box(SiteRoot(arena, cell), new int3(-2, 0, -2), new int3(4, 10, 4))
                : new GatherSpot { min = new int3((cell.x - 2) * k, h * k, (cell.y - 2) * k), max = new int3((cell.x + 3) * k, (h + 8) * k, (cell.y + 3) * k) });
        }
        foreach (var cell in layout.rocks)
        {
            int h = layout.height[cell.y * layout.sizeX + cell.x];
            spots.Add(new GatherSpot { min = new int3((cell.x - 3) * k, h * k, (cell.y - 3) * k), max = new int3((cell.x + 4) * k, (h + 4) * k, (cell.y + 4) * k) });
        }
        foreach (var resource in layout.resources)
        {
            var cell = resource.cell;
            int h = layout.height[cell.y * layout.sizeX + cell.x];
            switch (resource.kind)
            {
                case ResourceKind.CapturePoint:
                    spots.Add(new GatherSpot { min = new int3((cell.x - 2) * k, (h - 1) * k, (cell.y - 2) * k), max = new int3((cell.x + 3) * k, h * k, (cell.y + 3) * k) });
                    break;
                case ResourceKind.Ruin:
                    spots.Add(Box(SiteRoot(arena, cell), new int3(-13, 0, -9), new int3(14, 15, 10)));
                    break;
                case ResourceKind.Wreck:
                    spots.Add(Box(SiteRoot(arena, cell), new int3(-25, -2, -25), new int3(26, 19, 26)));
                    break;
            }
        }
    }

    /// <summary>Случайное место добычи из 5 ближайших к from; -1 — мест не осталось</summary>
    public int RandomNear(Vector3 from)
    {
        if (spots.Count == 0)
            return -1;
        var voxel = (int3)math.floor(arena.WorldToVoxel(from));
        int found = 0;
        for (int i = 0; i < spots.Count; i++)
        {
            var d3 = (spots[i].min + spots[i].max) / 2 - voxel;
            long d = (long)d3.x * d3.x + (long)d3.z * d3.z;
            // Короткий список ближайших по возрастанию: новое место встаёт на своё место, последнее выпадает
            if (found == NearSpots && d >= nearDistance[NearSpots - 1])
                continue;
            int slot = found < NearSpots ? found++ : NearSpots - 1;
            while (slot > 0 && nearDistance[slot - 1] > d)
            {
                nearSpot[slot] = nearSpot[slot - 1];
                nearDistance[slot] = nearDistance[slot - 1];
                slot--;
            }
            nearSpot[slot] = i;
            nearDistance[slot] = d;
        }
        return nearSpot[UnityEngine.Random.Range(0, found)];
    }

    /// <summary>Верхний воксель-материал в месте добычи (в него стреляет рейдер); false — всё выбито</summary>
    public bool Target(int spot, out Vector3 point)
    {
        var box = spots[spot];
        for (int y = box.max.y - 1; y >= box.min.y; y--)
            for (int z = box.min.z; z < box.max.z; z++)
                for (int x = box.min.x; x < box.max.x; x++)
                {
                    if (!Items.FromBlock(arena.GetBlock(new int3(x, y, z)), out _))
                        continue;
                    point = arena.VoxelToWorld(new float3(x + 0.5f, y + 0.5f, z + 0.5f));
                    return true;
                }
        point = default;
        return false;
    }

    /// <summary>Место выбито: убрать из списка (последнее встаёт на его номер)</summary>
    public void Remove(int spot)
    {
        spots[spot] = spots[^1];
        spots.RemoveAt(spots.Count - 1);
    }
}
