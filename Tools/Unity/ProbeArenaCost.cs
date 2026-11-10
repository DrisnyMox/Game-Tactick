using UnityEngine;

// Замер арены в Play: FPS, сколько чанков рисуется, сколько стоит перестройка после выстрела.
// Выбивает 10 воронок перед игроком (радиус как у пули) и после каждой сразу перестраивает чанки.
// Запуск: unity command run_script --file Tools/Unity/ProbeArenaCost.cs --project-path "D:\Game Tactick"
public static class ProbeArenaCost
{
    static readonly System.Reflection.MethodInfo Rebuild = typeof(VoxelArena).GetMethod(
        "RebuildDirtyChunks", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    public static string Main()
    {
        var arena = Object.FindFirstObjectByType<VoxelArena>();
        int renderers = 0;
        foreach (var r in arena.GetComponentsInChildren<MeshRenderer>())
            if (r.enabled)
                renderers++;
        string before = $"fps {1f / Time.smoothDeltaTime:0}, chunk {VoxelArena.ChunkSize}, node {VoxelArena.NodeChunks}³ chunks, chunks {arena.ChunkTotal}, " +
                        $"renderers {renderers}, triangles {arena.TotalTriangles}";

        var player = GameObject.Find("PlayerSurvivor").transform;
        double mesh = 0, apply = 0, combine = 0;
        int chunks = 0;
        for (int i = 0; i < 10; i++)
        {
            var above = player.position + player.forward * 6f + player.right * (i * 0.6f - 3f) + Vector3.up * 5f;
            Physics.Raycast(above, Vector3.down, out var hit, 50f);
            arena.Explode(hit.point - hit.normal * arena.VoxelSize * 0.5f, 1.3f * arena.VoxelSize);
            // Только перестройка арены: SendMessage("LateUpdate") задел бы и выключенный ArenaTestController
            Rebuild.Invoke(arena, null);
            mesh += arena.LastMeshMs;
            apply += arena.LastApplyMs;
            combine += arena.LastCombineMs;
            chunks += arena.LastRebuiltChunks;
        }
        return $"{before}\nper shot: mesh {mesh / 10:0.00} ms, apply {apply / 10:0.00} ms, " +
               $"combine {combine / 10:0.00} ms, chunks {chunks / 10f:0.0}";
    }
}
