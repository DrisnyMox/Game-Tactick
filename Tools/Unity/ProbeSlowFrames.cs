using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEditorInternal;

// Ловит долгие кадры профайлером редактора. "start" включает запись. "scan" смотрит кадры, записанные после
// прошлого scan, и для каждого кадра, где PlayerLoop дольше порога (второй аргумент, мс, по умолчанию 50), печатает
// дерево меток дольше 2 мс. Профайлер хранит последние ~300 кадров, поэтому scan надо звать каждые несколько секунд.
// "stop" выключает запись.
// Запуск в Play: unity command run_script --file Tools/Unity/ProbeSlowFrames.cs --project-path "D:\Game Tactick" -- --args "[\"scan\",\"50\"]"
public static class ProbeSlowFrames
{
    const string LastKey = "ProbeSlowFrames.last";

    public static string Main(string mode, string threshold = "50")
    {
        if (mode == "start")
        {
            ProfilerDriver.ClearAllFrames();
            ProfilerDriver.profileEditor = false;
            ProfilerDriver.enabled = true;
            SessionState.SetInt(LastKey, -1);
            return "recording";
        }
        if (mode == "stop")
        {
            ProfilerDriver.enabled = false;
            return "stopped";
        }

        float limit = float.Parse(threshold, System.Globalization.CultureInfo.InvariantCulture);
        int from = System.Math.Max(ProfilerDriver.firstFrameIndex, SessionState.GetInt(LastKey, -1) + 1);
        int to = ProfilerDriver.lastFrameIndex;
        SessionState.SetInt(LastKey, to);
        var log = new StringBuilder($"кадры {from}..{to}\n");
        var kids = new List<int>();

        for (int frame = from; frame <= to; frame++)
        {
            using var view = ProfilerDriver.GetHierarchyFrameDataView(frame, 0,
                HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName, HierarchyFrameDataView.columnTotalTime, false);
            if (!view.valid)
                continue;
            int loop = Find(view, view.GetRootItemID(), "PlayerLoop");
            if (loop < 0 || view.GetItemColumnDataAsFloat(loop, HierarchyFrameDataView.columnTotalTime) < limit)
                continue;
            log.AppendLine($"кадр {frame}: всего {view.frameTimeMs:0.0} мс");
            Print(view, loop, 1);

            // Поток отрисовки этого кадра: главный поток бывает занят ожиданием его
            for (int thread = 1; thread < 64; thread++)
            {
                using var raw = ProfilerDriver.GetRawFrameDataView(frame, thread);
                if (!raw.valid)
                    break;
                if (raw.threadName != "Render Thread")
                    continue;
                using var render = ProfilerDriver.GetHierarchyFrameDataView(frame, thread,
                    HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName, HierarchyFrameDataView.columnTotalTime, false);
                log.AppendLine("  Render Thread:");
                var top = new List<int>();
                render.GetItemChildren(render.GetRootItemID(), top);
                foreach (var item in top)
                    if (render.GetItemColumnDataAsFloat(item, HierarchyFrameDataView.columnTotalTime) >= 5f)
                        Print(render, item, 2);
                break;
            }
        }
        return log.ToString();

        int Find(HierarchyFrameDataView view, int id, string name)
        {
            if (view.GetItemName(id) == name)
                return id;
            var children = new List<int>();
            view.GetItemChildren(id, children);
            foreach (var child in children)
            {
                int found = Find(view, child, name);
                if (found >= 0)
                    return found;
            }
            return -1;
        }

        void Print(HierarchyFrameDataView view, int id, int depth)
        {
            float ms = view.GetItemColumnDataAsFloat(id, HierarchyFrameDataView.columnTotalTime);
            float gc = view.GetItemColumnDataAsFloat(id, HierarchyFrameDataView.columnGcMemory);
            log.AppendLine($"{new string(' ', depth * 2)}{view.GetItemName(id)} {ms:0.0} мс{(gc > 1024 ? $", GC {gc / 1024:0} КБ" : "")}");
            if (depth > 12)
                return;
            var children = new List<int>();
            view.GetItemChildren(id, children);
            foreach (var child in children)
                if (view.GetItemColumnDataAsFloat(child, HierarchyFrameDataView.columnTotalTime) >= 2f)
                    Print(view, child, depth + 1);
        }
    }
}
