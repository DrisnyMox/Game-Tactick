using System.Diagnostics;
using System.Text;

// Хэш и время раскладки арены для нескольких сидов: по нему сверяется, что правка генератора
// не меняет карту. Размер — как в SampleScene (800×96×800 вокселей, клетка 2 вокселя).
// Запуск: unity command run_script --file Tools/Unity/ProbeLayoutHash.cs
public static class ProbeLayoutHash
{
    public static string Main()
    {
        var sb = new StringBuilder();
        foreach (int seed in new[] { 1, 2, 3, 42 })
        {
            var settings = ArenaGenSettings.Default;
            settings.seed = seed;
            var biome = ArenaBiomes.Resolve(settings.biome, settings.seed);

            var timer = Stopwatch.StartNew();
            var layout = ArenaLayoutGenerator.Generate(settings, biome, 400, 48, 400);
            long ms = timer.ElapsedMilliseconds;

            ulong h = 1469598103934665603UL;
            void Mix(long v)
            {
                h ^= (ulong)v;
                h *= 1099511628211UL;
            }

            for (int i = 0; i < layout.sizeX * layout.sizeZ; i++)
            {
                Mix(layout.height[i]);
                Mix(layout.waterTop[i]);
                Mix(layout.flags[i]);
                Mix(layout.baseZone[i]);
                Mix(layout.baseArea[i]);
            }
            foreach (var t in layout.trees) { Mix(t.x); Mix(t.y); }
            foreach (var t in layout.rocks) { Mix(t.x + 7); Mix(t.y); }
            foreach (var w in layout.walls) { Mix(w.cell.x); Mix(w.cell.y); Mix(w.outer ? 1 : 0); }
            foreach (var r in layout.resources) { Mix(r.cell.x); Mix(r.cell.y); Mix(r.team); }
            foreach (var g in layout.gates) { Mix(g.x); Mix(g.y); }
            Mix(layout.baseOne.x);
            Mix(layout.baseOne.y);
            Mix(layout.gateSide);
            Mix(layout.regionCount);
            Mix(layout.rampCount);
            Mix(layout.flattenedRegions);

            sb.AppendLine($"seed {seed}: {ms} ms, hash {h:X16}, trees {layout.trees.Length}, rocks {layout.rocks.Length}, " +
                          $"ramps {layout.rampCount}, regions {layout.regionCount}, flattened {layout.flattenedRegions}, " +
                          $"connected {layout.basesConnected}, captures {layout.capturePointsReachable}");
            layout.Dispose();
        }
        return sb.ToString();
    }
}
