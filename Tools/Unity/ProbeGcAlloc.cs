using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEditorInternal;

// Кто выделяет память в кадре. "start" включает запись профайлера редактора, "report" через несколько секунд
// проходит по записанным кадрам и печатает, где выделено больше всего: собственные выделения каждой метки
// (без детей), суммарно за кадры и в среднем на кадр, с путём от корня. Запись выключается.
// Запуск в Play: unity command run_script --file Tools/Unity/ProbeGcAlloc.cs --project-path "D:\Game Tactick" -- --args "[\"start\"]"
public static class ProbeGcAlloc
{
    public static string Main(string mode)
    {
        if (mode == "start")
        {
            ProfilerDriver.ClearAllFrames();
            ProfilerDriver.profileEditor = false;
            ProfilerDriver.enabled = true;
            return "profiler recording";
        }

        ProfilerDriver.enabled = false;
        int first = ProfilerDriver.firstFrameIndex, last = ProfilerDriver.lastFrameIndex;
        var self = new Dictionary<string, long>();
        var calls = new Dictionary<string, int>();
        long total = 0;
        int frames = 0;
        var children = new List<int>();

        for (int frame = first; frame <= last; frame++)
        {
            using var view = ProfilerDriver.GetHierarchyFrameDataView(frame, 0,
                HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName, HierarchyFrameDataView.columnGcMemory, false);
            if (!view.valid)
                continue;
            frames++;
            Walk(view, view.GetRootItemID(), "");
        }

        void Walk(HierarchyFrameDataView view, int id, string path)
        {
            var kids = new List<int>();
            view.GetItemChildren(id, kids);
            long own = (long)view.GetItemColumnDataAsFloat(id, HierarchyFrameDataView.columnGcMemory);
            string name = view.GetItemName(id);
            string here = path == "" ? name : path + " / " + name;
            foreach (var kid in kids)
            {
                own -= (long)view.GetItemColumnDataAsFloat(kid, HierarchyFrameDataView.columnGcMemory);
                Walk(view, kid, here);
            }
            if (own <= 0)
                return;
            total += own;
            self[here] = self.TryGetValue(here, out var s) ? s + own : own;
            calls[here] = calls.TryGetValue(here, out var c) ? c + 1 : 1;
        }

        var log = new StringBuilder($"кадров {frames}, выделено всего {total / 1024} КБ, в среднем {(frames > 0 ? total / frames / 1024.0 : 0):0.0} КБ на кадр\n");
        foreach (var pair in self.OrderByDescending(p => p.Value).Take(30))
            log.AppendLine($"{pair.Value / (double)frames / 1024:0.00} КБ/кадр ({pair.Value / 1024} КБ, в {calls[pair.Key]} кадрах): {pair.Key}");
        return log.ToString();
    }
}
