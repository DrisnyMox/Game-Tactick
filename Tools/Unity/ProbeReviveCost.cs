using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Unity.Profiling;
using UnityEngine;

// Сколько стоит физике возрождение убитого. Боты стоят, чтобы бой не мешал замеру.
//  "kill"   - Enemy 1 падает рэгдоллом там, где стоит;
//  "far"    - Enemy 1 возрождается на 100 м дальше (как на базе);
//  "near"   - Enemy 1 возрождается там же, где лежит;
//  "nav"    - воронка в 6 м перед игроком: ArenaNavMesh пересобирает области вокруг неё;
//  "report" - Physics.Simulate в 40 кадрах после последнего действия и время последней сборки NavMesh;
//  "stop"   - боты снова ходят.
// Запуск: unity command run_script --file Tools/Unity/ProbeReviveCost.cs --project-path "D:\Game Tactick" -- --args "[\"kill\"]"
public static class ProbeReviveCost
{
    const string Key = "ProbeReviveCost.state";

    public static string Main(string mode)
    {
        var enemy = GameObject.Find("Enemy 1").GetComponent<CharacterHealth>();
        if (mode == "report")
        {
            var stored = (object[])AppDomain.CurrentDomain.GetData(Key);
            Application.onBeforeRender -= (UnityEngine.Events.UnityAction)stored[2];
            ((ProfilerRecorder)stored[0]).Dispose();
            var log = new StringBuilder("Physics.Simulate по кадрам, мс:");
            foreach (var ms in (List<double>)stored[1])
                log.Append($" {ms:0.0}");
            var arenaNav = UnityEngine.Object.FindAnyObjectByType<ArenaNavMesh>();
            log.Append($"; NavMesh: последняя сборка области {arenaNav.LastBuildMs:0} мс за {arenaNav.LastBuildFrames} кадров, " +
                       $"её швы {arenaNav.LastSeamsMs:0.00} мс, в очереди {arenaNav.QueuedRegions}, связок {arenaNav.LinkCount}");
            // Плитки вне коробки пересборки должны остаться: путь от ворот до ворот через всю арену
            var a = UnityEngine.Object.FindAnyObjectByType<VoxelArena>();
            var path = new UnityEngine.AI.NavMeshPath();
            bool found = UnityEngine.AI.NavMesh.SamplePosition(a.GateOne, out var from, 3f, UnityEngine.AI.NavMesh.AllAreas) &
                         UnityEngine.AI.NavMesh.SamplePosition(a.GateTwo, out var to, 3f, UnityEngine.AI.NavMesh.AllAreas);
            if (found)
                UnityEngine.AI.NavMesh.CalculatePath(from.position, to.position, UnityEngine.AI.NavMesh.AllAreas, path);
            log.Append($"; ворота на NavMesh {found}, путь между ними {path.status}, точек {path.corners.Length}");
            return log.ToString();
        }
        if (mode == "stop")
        {
            foreach (var c in CharacterHealth.All)
                if (!c.IsDead && c.GetComponent<ShooterBot>() != null)
                    c.GetComponent<ShooterBot>().enabled = true;
            return "bots enabled";
        }

        foreach (var c in CharacterHealth.All)
            if (c.GetComponent<ShooterBot>() != null)
                c.GetComponent<ShooterBot>().enabled = false;

        if (mode == "kill")
        {
            typeof(CharacterHealth).GetProperty("Health").SetValue(enemy, 0f);
            typeof(CharacterHealth).GetMethod("SetRagdoll", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(enemy, new object[] { true });
        }
        if (mode == "nav")
        {
            // Воронка в 6 м перед игроком: ArenaNavMesh пересоберёт области вокруг неё
            var player = GameObject.Find("PlayerSurvivor").transform;
            var arena = UnityEngine.Object.FindAnyObjectByType<VoxelArena>();
            arena.Explode(player.position + player.forward * 6f, arena.VoxelSize * 3f);

        }
        var pos = enemy.Chest.position;
        if (mode == "far")
            enemy.Revive(new Vector3(pos.x + 100f, pos.y + 2f, pos.z), Quaternion.identity);
        if (mode == "near")
            enemy.Revive(new Vector3(pos.x, pos.y + 1f, pos.z), Quaternion.identity);
        if (mode == "far" || mode == "near")
            enemy.GetComponent<ShooterBot>().enabled = false;

        // Запись Physics.Simulate в следующих кадрах
        var recorder = ProfilerRecorder.StartNew(ProfilerCategory.Physics, "Physics.Simulate", 1);
        var frames = new List<double>();
        UnityEngine.Events.UnityAction tick = () =>
        {
            if (frames.Count < 40)
                frames.Add(recorder.LastValue / 1e6);
        };
        Application.onBeforeRender += tick;
        AppDomain.CurrentDomain.SetData(Key, new object[] { recorder, frames, tick });
        return $"{mode}: {enemy.name} at {enemy.transform.position}, dead {enemy.IsDead}";
    }
}
