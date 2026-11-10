using System.Text;
using UnityEngine;

// Проверка рамп арены в Play: на клетках с FlagRamp соседние столбцы вокселей должны отличаться
// по высоте не больше чем на один воксель. Печатает число перепадов в 2+ вокселя и профиль
// высот через первую рампу.
// Запуск: unity command run_script --file Tools/Unity/ProbeArenaRamps.cs --project-path "D:\Game Tactick"
public static class ProbeArenaRamps
{
    public static string Main()
    {
        var arena = Object.FindFirstObjectByType<VoxelArena>();
        var layout = arena.Layout;
        int k = arena.CellVoxels;
        var dims = arena.Dims;

        int pairs = 0, steep = 0, worst = 0;
        for (int z = 0; z + 1 < dims.z; z++)
        {
            for (int x = 0; x + 1 < dims.x; x++)
            {
                int cell = z / k * layout.sizeX + x / k;
                if ((layout.flags[cell] & ArenaLayout.FlagRamp) == 0)
                    continue;
                int h = arena.SurfaceY(x, z);
                foreach (int d in new[] { arena.SurfaceY(x + 1, z) - h, arena.SurfaceY(x, z + 1) - h })
                {
                    pairs++;
                    if (Mathf.Abs(d) >= 2)
                        steep++;
                    worst = Mathf.Max(worst, Mathf.Abs(d));
                }
            }
        }

        var log = new StringBuilder();
        log.AppendLine($"ramp neighbour pairs {pairs}, with step >= 2 voxels: {steep}, worst {worst}");
        for (int i = 0; i < layout.flags.Length; i++)
        {
            if ((layout.flags[i] & ArenaLayout.FlagRamp) == 0)
                continue;
            int cx = i % layout.sizeX * k, cz = i / layout.sizeX * k;
            log.Append("along x:");
            for (int d = -10; d <= 10; d++)
                log.Append(' ').Append(arena.SurfaceY(cx + d, cz));
            log.AppendLine();
            log.Append("along z:");
            for (int d = -10; d <= 10; d++)
                log.Append(' ').Append(arena.SurfaceY(cx, cz + d));
            log.AppendLine();
            break;
        }
        return log.ToString();
    }
}
