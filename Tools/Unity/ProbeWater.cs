using System.Text;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;

// Вода архипелага в Play mode: сколько столбцов какой глубины (в вокселях), есть ли NavMesh на дне мелководья.
// Переносит игрока на мелководье глубиной 3 вокселя в середине карты: следующий запуск через пару секунд
// покажет, остался ли он там (глубже 3 вокселей RaidDirector возвращает на сушу).
// Запуск: unity command run_script --file Tools/Unity/ProbeWater.cs --project-path "<путь к проекту>"
public static class ProbeWater
{
    public static string Main()
    {
        Application.runInBackground = true;
        EditorApplication.QueuePlayerLoopUpdate();
        var arena = Object.FindAnyObjectByType<VoxelArena>();
        var dims = arena.Dims;
        var histogram = new int[12];
        int3 shallow3 = default;
        bool found = false;
        for (int z = 0; z < dims.z; z += 2)
            for (int x = 0; x < dims.x; x += 2)
            {
                int y = arena.SurfaceY(x, z), depth = 0;
                while (arena.GetBlock(new int3(x, y + depth, z)) == VoxelBlocks.Water)
                    depth++;
                histogram[math.min(depth, 11)]++;
                if (depth == 3 && !found && math.abs(x - dims.x / 2) < 60 && math.abs(z - dims.z / 2) < 60)
                {
                    shallow3 = new int3(x, y, z);
                    found = true;
                }
            }
        var s = new StringBuilder("depth(voxels):count ");
        for (int d = 0; d < histogram.Length; d++)
            if (histogram[d] > 0)
                s.Append($"{d}:{histogram[d]} ");

        SurvivorShooter player = null;
        foreach (var shooter in Object.FindObjectsByType<SurvivorShooter>(FindObjectsSortMode.None))
            if (!shooter.BotControlled)
                player = shooter;
        s.Append($"\nplayer now {player.transform.position}");
        if (found)
        {
            var point = arena.VoxelToWorld(new float3(shallow3.x + 0.5f, shallow3.y, shallow3.z + 0.5f));
            bool nav = NavMesh.SamplePosition(point, out var hit, 0.6f, NavMesh.AllAreas);
            s.Append($"\nshallow 3 at {point}, navmesh {nav} {(nav ? hit.position.ToString() : "")}");
            var controller = player.GetComponent<CharacterController>();
            controller.enabled = false;
            player.transform.position = point + Vector3.up * 0.05f;
            controller.enabled = true;
        }
        return s.ToString();
    }
}
