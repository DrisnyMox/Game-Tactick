using System;
using System.Text;
using Unity.Profiling;

// Время кадра по профайлеру. Первый запуск включает запись счётчиков, второй (через несколько секунд)
// печатает среднее за последние кадры и выключает запись. Записи между запусками хранятся в AppDomain.
// Запуск: unity command run_script --file Tools/Unity/ProbeFrameCost.cs --project-path "D:\Game Tactick"
public static class ProbeFrameCost
{
    const string Key = "ProbeFrameCost.recorders";

    static readonly (ProfilerCategory category, string name)[] Counters =
    {
        (ProfilerCategory.Internal, "PlayerLoop"),
        (ProfilerCategory.Internal, "Main Thread"),
        (ProfilerCategory.Render, "Render Thread"),
        (ProfilerCategory.Internal, "Gfx.WaitForPresentOnGfxThread"),
        (ProfilerCategory.Physics, "Physics.Processing"),
        (ProfilerCategory.Render, "Batches Count"),
        (ProfilerCategory.Render, "SetPass Calls Count"),
        (ProfilerCategory.Render, "Shadow Casters Count"),
    };

    public static string Main()
    {
        if (AppDomain.CurrentDomain.GetData(Key) is not object[] boxed)
        {
            var started = new object[Counters.Length];
            for (int i = 0; i < Counters.Length; i++)
                started[i] = ProfilerRecorder.StartNew(Counters[i].category, Counters[i].name, 120);
            AppDomain.CurrentDomain.SetData(Key, started);
            return "recording started, run again in a few seconds";
        }

        var log = new StringBuilder();
        for (int i = 0; i < Counters.Length; i++)
        {
            var recorder = (ProfilerRecorder)boxed[i];
            double sum = 0;
            int count = recorder.Count;
            for (int s = 0; s < count; s++)
                sum += recorder.GetSample(s).Value;
            double avg = count > 0 ? sum / count : 0;
            bool time = recorder.UnitType == ProfilerMarkerDataUnit.TimeNanoseconds;
            log.AppendLine(time
                ? $"{Counters[i].name}: {avg / 1e6:0.00} ms (valid {recorder.Valid}, samples {count})"
                : $"{Counters[i].name}: {avg:0} (valid {recorder.Valid}, samples {count})");
            recorder.Dispose();
        }
        AppDomain.CurrentDomain.SetData(Key, null);
        return log.ToString();
    }
}
