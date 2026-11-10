using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Unity.Profiling;
using UnityEngine;

// Худшие кадры и из чего они состоят. Первый запуск включает запись: перед отрисовкой каждого кадра
// в список попадает время прошлого кадра по каждому счётчику. Второй запуск печатает худшие кадры
// с разбивкой (всё, что дольше 1 мс) и выключает запись. Каждый run_script компилируется в новую
// сборку со своими static, поэтому записи и подписка лежат в AppDomain.
// Запуск: unity command run_script --file Tools/Unity/ProbeFrameSpikes.cs --project-path "D:\Game Tactick"
public static class ProbeFrameSpikes
{
    const string Key = "ProbeFrameSpikes.state";

    static readonly (ProfilerCategory category, string name)[] Counters =
    {
        (ProfilerCategory.Internal, "Main Thread"),
        (ProfilerCategory.Internal, "PlayerLoop"),
        (ProfilerCategory.Internal, "EditorLoop"),
        (ProfilerCategory.Internal, "Update.ScriptRunBehaviourUpdate"),
        (ProfilerCategory.Internal, "PreLateUpdate.ScriptRunBehaviourLateUpdate"),
        (ProfilerCategory.Internal, "FixedUpdate.PhysicsFixedUpdate"),
        (ProfilerCategory.Internal, "FixedUpdate.ScriptRunBehaviourFixedUpdate"),
        (ProfilerCategory.Physics, "Physics.Simulate"),
        (ProfilerCategory.Physics, "Physics.Processing"),
        (ProfilerCategory.Physics, "Physics.BakeCollisionMeshes"),
        (ProfilerCategory.Physics, "Mesh.Bake Scaled Mesh PhysX CollisionData"),
        (ProfilerCategory.Scripts, "ArenaNavMesh.StartUpdate"),
        (ProfilerCategory.Ai, "NavMeshManager"),
        (ProfilerCategory.Internal, "PreUpdate.AIUpdatePostScript"),
        (ProfilerCategory.Animation, "PreLateUpdate.DirectorUpdateAnimationBegin"),
        (ProfilerCategory.Animation, "PreLateUpdate.DirectorUpdateAnimationEnd"),
        (ProfilerCategory.Internal, "PostLateUpdate.UpdateAllSkinnedMeshes"),
        (ProfilerCategory.Internal, "PostLateUpdate.ParticleSystemEndUpdateAll"),
        (ProfilerCategory.Internal, "PostLateUpdate.FinishFrameRendering"),
        (ProfilerCategory.Internal, "PostLateUpdate.UpdateCustomRenderTextures"),
        (ProfilerCategory.Gui, "PostLateUpdate.PlayerUpdateCanvases"),
        (ProfilerCategory.Gui, "GUI.Repaint"),
        (ProfilerCategory.Internal, "Gfx.WaitForPresentOnGfxThread"),
        (ProfilerCategory.Internal, "GC.Collect"),
        (ProfilerCategory.Render, "Shader.CreateGPUProgram"),
        (ProfilerCategory.Render, "RenderPipelineManager.DoRenderLoop_Internal"),
        (ProfilerCategory.Particles, "ParticleSystem.Update"),
        (ProfilerCategory.Particles, "ParticleSystem.WaitForUpdateThreads"),
        (ProfilerCategory.Physics, "Physics.FetchResults"),
        (ProfilerCategory.Physics, "Physics.ProcessReports"),
        (ProfilerCategory.Physics, "Physics.UpdateBodies"),
        (ProfilerCategory.Memory, "GC Allocated In Frame"),
        (ProfilerCategory.Render, "Batches Count"),
        (ProfilerCategory.Render, "Shadow Casters Count"),
    };

    // После счётчиков в записи кадра: номер кадра, сколько раз шёл Physics.Simulate, сколько бойцов мертвы,
    // сколько обломков в воздухе, сколько тел физики не спят, сколько раз арена вынула воксели
    const int Extra = 6;

    public static string Main()
    {
        // В AppDomain лежат только типы Unity и mscorlib: { счётчики, кадры, подписка }
        if (AppDomain.CurrentDomain.GetData(Key) is not object[] stored)
        {
            var started = new ProfilerRecorder[Counters.Length];
            for (int i = 0; i < Counters.Length; i++)
                started[i] = ProfilerRecorder.StartNew(Counters[i].category, Counters[i].name, 1);
            var recorded = new List<double[]>();
            var bodies = UnityEngine.Object.FindObjectsByType<Rigidbody>(FindObjectsSortMode.None);
            var debris = UnityEngine.Object.FindAnyObjectByType<VoxelDebris>().GetComponent<ParticleSystem>();
            int simulate = Array.FindIndex(Counters, c => c.name == "Physics.Simulate");
            // Сколько раз арена вынула воксели с прошлой записи (попадания в землю и стены)
            int[] explosions = { 0 };
            UnityEngine.Object.FindAnyObjectByType<VoxelArena>().VoxelsRemoved += (_, _) => explosions[0]++;
            UnityEngine.Events.UnityAction tick = () =>
            {
                var values = new double[Counters.Length + Extra];
                for (int i = 0; i < Counters.Length; i++)
                    values[i] = started[i].Valid ? started[i].LastValue : -1;
                values[Counters.Length] = Time.frameCount;
                values[Counters.Length + 1] = started[simulate].Count > 0 ? started[simulate].GetSample(0).Count : 0;
                values[Counters.Length + 2] = CharacterHealth.All.Count(c => c.IsDead);
                values[Counters.Length + 3] = debris.particleCount;
                values[Counters.Length + 4] = bodies.Count(b => !b.isKinematic && !b.IsSleeping());
                values[Counters.Length + 5] = explosions[0];
                explosions[0] = 0;
                recorded.Add(values);
            };
            Application.onBeforeRender += tick;
            AppDomain.CurrentDomain.SetData(Key, new object[] { started, recorded, tick });
            return "recording started, run again later";
        }

        Application.onBeforeRender -= (UnityEngine.Events.UnityAction)stored[2];
        var recorders = (ProfilerRecorder[])stored[0];
        var frames = (List<double[]>)stored[1];
        AppDomain.CurrentDomain.SetData(Key, null);

        var log = new StringBuilder();
        var isTime = new bool[Counters.Length];
        for (int i = 0; i < Counters.Length; i++)
        {
            if (!recorders[i].Valid)
                log.AppendLine($"нет счётчика: {Counters[i].name}");
            else
                isTime[i] = recorders[i].UnitType == ProfilerMarkerDataUnit.TimeNanoseconds;
            recorders[i].Dispose();
        }
        // Первые кадры после включения и кадры паузы редактора не считаются
        var main = frames.Skip(2).ToList();
        double Ms(double ns) => ns / 1e6;
        var sorted = main.Select(f => Ms(f[0])).OrderBy(x => x).ToList();
        log.AppendLine($"кадров {main.Count}, главный поток: среднее {sorted.Average():0.0} мс, " +
                       $"медиана {sorted[sorted.Count / 2]:0.0}, 95% {sorted[(int)(sorted.Count * 0.95)]:0.0}, " +
                       $"худший {sorted[^1]:0.0}; дольше 50 мс: {sorted.Count(x => x > 50)}, дольше 90 мс: {sorted.Count(x => x > 90)}");

        // Средние по счётчикам
        log.Append("средние:");
        for (int i = 1; i < Counters.Length; i++)
        {
            double avg = main.Average(f => Math.Max(0, f[i]));
            bool time = isTime[i];
            if (time && Ms(avg) >= 0.3) log.Append($" {Counters[i].name} {Ms(avg):0.0};");
            if (!time && avg > 0) log.Append($" {Counters[i].name} {avg:0};");
        }
        log.AppendLine();

        // Физика в кадрах, где есть не спящие тела (рэгдоллы упавших), и где их нет
        int n = Counters.Length, sim = Array.FindIndex(Counters, c => c.name == "Physics.Simulate");
        var awake = main.Where(f => f[n + 4] > 0).ToList();
        var calm = main.Where(f => f[n + 4] == 0).ToList();
        if (awake.Count > 0 && calm.Count > 0)
            log.AppendLine($"Physics.Simulate: с не спящими телами {awake.Average(f => Ms(f[sim])):0.0} мс за кадр ({awake.Count} кадров, " +
                           $"худший {awake.Max(f => Ms(f[sim])):0.0}), без них {calm.Average(f => Ms(f[sim])):0.0} мс ({calm.Count} кадров, " +
                           $"худший {calm.Max(f => Ms(f[sim])):0.0})");

        // Сборка мусора: в скольких кадрах шла и сколько стоила
        int gc = Array.FindIndex(Counters, c => c.name == "GC.Collect");
        var gcFrames = main.Where(f => f[gc] > 0).Select(f => Ms(f[gc])).ToList();
        log.AppendLine(gcFrames.Count == 0 ? "GC.Collect: ни разу"
            : $"GC.Collect: в {gcFrames.Count} кадрах из {main.Count}, среднее {gcFrames.Average():0.0} мс, худшее {gcFrames.Max():0.0} мс");

        // Что было в кадрах перед всплесками физики: смерти, взрывы
        var spikes = main.Where(f => Ms(f[sim]) > 20).ToList();
        log.AppendLine($"кадров с Physics.Simulate > 20 мс: {spikes.Count}");
        foreach (var f in spikes.Take(15))
        {
            int at = main.IndexOf(f);
            log.Append($"  кадр {f[n]:0}:");
            for (int k = Math.Max(0, at - 6); k <= at; k++)
                log.Append($" [{Ms(main[k][sim]):0} мс, мёртв {main[k][n + 2]:0}, взрывов {main[k][n + 5]:0}, тел {main[k][n + 4]:0}]");
            log.AppendLine();
        }

        foreach (var f in main.OrderByDescending(f => f[0]).Take(6))
        {
            log.Append($"кадр {f[n]:0}: {Ms(f[0]):0.0} мс [шагов физики {f[n + 1]:0}, мёртвых {f[n + 2]:0}, " +
                       $"обломков {f[n + 3]:0}, тел не спит {f[n + 4]:0}] =");
            for (int i = 1; i < Counters.Length; i++)
            {
                bool time = isTime[i];
                if (time && Ms(f[i]) >= 1) log.Append($" {Counters[i].name} {Ms(f[i]):0.0};");
                if (!time && Counters[i].name == "GC Allocated In Frame" && f[i] > 100000) log.Append($" GC alloc {f[i] / 1024:0} КБ;");
            }
            log.AppendLine();
        }
        return log.ToString();
    }
}
