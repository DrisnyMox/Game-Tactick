using UnityEngine;

// Проверка осколков в Play. Аргумент "blast" выбивает воксели там, куда смотрит камера
// (радиус как у пули, или радиус в вокселях вторым аргументом). Печатает, сколько частиц выпущено
// последним выбиванием и за сколько мс, сколько частиц живо и FPS.
// Запуск: unity command run_script --file Tools/Unity/ProbeVoxelDebris.cs --project-path "D:\Game Tactick" -- --args "[\"blast\", \"1.3\"]"
public static class ProbeVoxelDebris
{
    public static string Main(string mode = "", string radius = "1.3")
    {
        var debris = Object.FindFirstObjectByType<VoxelDebris>();
        var arena = Object.FindFirstObjectByType<VoxelArena>();
        string blast = "";
        if (mode == "blast")
        {
            var cam = Camera.main.transform;
            if (Physics.Raycast(cam.position, cam.forward, out var hit, 200f, 1))
            {
                int removed = arena.Explode(hit.point - hit.normal * arena.VoxelSize * 0.5f,
                    float.Parse(radius, System.Globalization.CultureInfo.InvariantCulture) * arena.VoxelSize);
                blast = $"blast at {hit.point}, {hit.distance:0.0} m: removed {removed} voxels\n";
            }
            else
                blast = "camera looks at nothing\n";
        }
        var ps = debris.GetComponent<ParticleSystem>();
        return blast + $"last emit {debris.LastEmitted} particles in {debris.LastEmitMs:0.00} ms, alive {ps.particleCount}, " +
               $"fps {1f / Time.smoothDeltaTime:0}";
    }
}
