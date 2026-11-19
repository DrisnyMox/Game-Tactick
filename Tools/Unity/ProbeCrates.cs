using System.IO;
using System.Text;
using Unity.Mathematics;
using UnityEngine;

// Ящики вылазки в Play mode: сколько, где стоят (на песке, в руинах, в остове), висят ли в воздухе.
// Снимок ящика на песке рядом с игроком — Temp/Shots/crate_sand.png
// Запуск: unity command run_script --file Tools/Unity/ProbeCrates.cs --project-path "<путь к проекту>"
public static class ProbeCrates
{
    public static string Main()
    {
        Application.runInBackground = true;
        var arena = Object.FindAnyObjectByType<VoxelArena>();
        var layout = arena.Layout;
        int k = arena.CellVoxels;
        var s = new StringBuilder();
        Pickup sandCrate = null;
        foreach (var pickup in Object.FindObjectsByType<Pickup>())
        {
            var v = (int3)math.floor(arena.WorldToVoxel(pickup.transform.position));
            int cell = (v.z / k) * layout.sizeX + v.x / k;
            bool site = false;
            foreach (var r in layout.resources)
                if ((r.kind == ResourceKind.Ruin || r.kind == ResourceKind.Wreck) && math.distance((float2)r.cell, new float2(v.x / k, v.z / k)) < 2f)
                    site = true;
            bool sand = (layout.flags[cell] & ArenaLayout.FlagSand) != 0;
            int gap = v.y - arena.SurfaceY(v.x, v.z);
            s.Append($"{pickup.Label}: {(site ? "постройка" : sand ? "песок" : "другое")}, над поверхностью {gap}; ");
            if (!site && sand && sandCrate == null)
                sandCrate = pickup;
        }
        if (sandCrate != null)
            Shoot(sandCrate.transform.position);
        return s.ToString();
    }

    static void Shoot(Vector3 crate)
    {
        SurvivorShooter player = null;
        foreach (var shooter in Object.FindObjectsByType<SurvivorShooter>())
            if (!shooter.BotControlled)
                player = shooter;
        var controller = player.GetComponent<CharacterController>();
        controller.enabled = false;
        player.transform.position = crate + Vector3.right * 2.5f + Vector3.up * 0.1f;
        controller.enabled = true;

        var go = new GameObject("Probe Camera");
        var cam = go.AddComponent<Camera>();
        var focus = crate + Vector3.right * 1.2f + Vector3.up * 0.6f;
        cam.transform.position = focus + new Vector3(-0.6f, 0.8f, -0.4f).normalized * 6f;
        cam.transform.LookAt(focus);
        var rt = new RenderTexture(1280, 720, 24);
        cam.targetTexture = rt;
        cam.Render();
        RenderTexture.active = rt;
        var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
        tex.Apply();
        Directory.CreateDirectory("Temp/Shots");
        File.WriteAllBytes("Temp/Shots/crate_sand.png", tex.EncodeToPNG());
        RenderTexture.active = null;
        cam.targetTexture = null;
        Object.DestroyImmediate(rt);
        Object.DestroyImmediate(tex);
        Object.DestroyImmediate(go);
    }
}
