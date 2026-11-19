using System.Text;
using UnityEngine;

// Ширина уступов рельефа острова в Play mode: вдоль строк столбцов ищет перепады высоты на песке
// и считает, через сколько вокселей от предыдущего перепада идёт следующий (трава не в счёт — она слой над песком).
// Запуск: unity command run_script --file Tools/Unity/ProbeShoreSteps.cs --project-path "<путь к проекту>"
public static class ProbeShoreSteps
{
    public static string Main()
    {
        var arena = Object.FindAnyObjectByType<VoxelArena>();
        var layout = arena.Layout;
        var dims = arena.Dims;
        int k = arena.CellVoxels;
        var widths = new int[12];
        int steps = 0, bigSteps = 0;
        for (int z = 60; z < dims.z - 60; z += 3)
        {
            int last = -1;
            for (int x = 60; x < dims.x - 61; x++)
            {
                int a = (z / k) * layout.sizeX + x / k, b = (z / k) * layout.sizeX + (x + 1) / k;
                bool sand = (layout.flags[a] & ArenaLayout.FlagSand) != 0 && (layout.flags[b] & ArenaLayout.FlagSand) != 0;
                int d = arena.GroundY(x + 1, z) - arena.GroundY(x, z);
                if (d == 0)
                    continue;
                if (!sand)
                {
                    last = -1;
                    continue;
                }
                steps++;
                if (System.Math.Abs(d) > 1)
                    bigSteps++;
                if (last >= 0)
                    widths[System.Math.Min(x - last, widths.Length - 1)]++;
                last = x;
            }
        }
        var s = new StringBuilder($"перепадов {steps}, больше вокселя {bigSteps}; ширина уступа (вокселей: сколько раз):");
        for (int w = 1; w < widths.Length; w++)
            s.Append($" {w}{(w == widths.Length - 1 ? "+" : "")}:{widths[w]}");
        return s.ToString();
    }
}
