using System.IO;
using System.Text;
using Unity.Mathematics;
using UnityEngine;

// Проверка масштаба построек острова в Play mode: ставит игрока рядом с пальмой на пляже, руинами, остовом
// и причалом и снимает сбоку временной камерой. PNG в Temp/Shots проекта (вне Assets).
// Запуск: unity command run_script --file Tools/Unity/ProbeIslandScale.cs --project-path "<путь к проекту>"
public static class ProbeIslandScale
{
    public static string Main()
    {
        Application.runInBackground = true;
        var arena = Object.FindAnyObjectByType<VoxelArena>();
        var layout = arena.Layout;
        int k = arena.CellVoxels;
        SurvivorShooter player = null;
        foreach (var shooter in Object.FindObjectsByType<SurvivorShooter>())
            if (!shooter.BotControlled)
                player = shooter;

        Directory.CreateDirectory("Temp/Shots");
        var s = new StringBuilder();

        Vector3 Root(int2 cell) => arena.VoxelToWorld(new float3(cell.x * k + k / 2, arena.GroundY(cell.x * k + k / 2, cell.y * k + k / 2), cell.y * k + k / 2));

        // Край травы: клетка травы, справа от которой песок той же высоты (уступ травы над песком)
        for (int z = 10; z < layout.sizeZ - 10 && !s.ToString().Contains("grass"); z++)
            for (int x = 10; x < layout.sizeX - 10; x++)
            {
                int i = z * layout.sizeX + x;
                if (!ArenaLayout.IsRaisedGrass(layout.flags[i], layout.grassRaiseVoxels) || (layout.flags[i + 1] & ArenaLayout.FlagSand) == 0
                    || layout.waterTop[i + 1] > layout.height[i + 1])
                    continue;
                Shoot(player, Root(new int2(x, z)), 1.5f, 7f, "Temp/Shots/scale_grass.png");
                s.Append("grass ");
                break;
            }

        foreach (var cell in layout.trees)
        {
            if ((layout.flags[cell.y * layout.sizeX + cell.x] & ArenaLayout.FlagSand) == 0)
                continue;
            Shoot(player, Root(cell), 3f, 14f, "Temp/Shots/scale_palm.png");
            s.Append("palm ");
            break;
        }

        bool ruin = false, wreck = false, landing = false;
        foreach (var resource in layout.resources)
        {
            var root = Root(resource.cell);
            if (resource.kind == ResourceKind.Ruin && !ruin)
            {
                ruin = true;
                Shoot(player, root, 9f, 22f, "Temp/Shots/scale_ruin.png");
            }
            else if (resource.kind == ResourceKind.Wreck && !wreck)
            {
                wreck = true;
                Shoot(player, root, 10f, 28f, "Temp/Shots/scale_wreck.png");
            }
            else if (resource.kind == ResourceKind.Landing && !landing)
            {
                landing = true;
                var facing = new Vector3(resource.facing.x, 0f, resource.facing.y);
                Shoot(player, root + facing * 5f, 3f, 18f, "Temp/Shots/scale_landing.png");
            }
        }
        s.Append($"ruin {ruin} wreck {wreck} landing {landing}");
        return s.ToString();
    }

    // Игрок встаёт в offset метрах сбоку от постройки, камера смотрит на обоих с distance метров
    static void Shoot(SurvivorShooter player, Vector3 site, float offset, float distance, string path)
    {
        var controller = player.GetComponent<CharacterController>();
        controller.enabled = false;
        player.transform.position = site + Vector3.right * offset + Vector3.up * 0.1f;
        controller.enabled = true;

        var focus = site + Vector3.right * offset * 0.5f + Vector3.up * 2.5f;
        var go = new GameObject("Probe Camera");
        var cam = go.AddComponent<Camera>();
        cam.transform.position = focus + new Vector3(0.3f, 0.35f, -1f).normalized * distance;
        cam.transform.LookAt(focus);
        cam.fieldOfView = 55f;
        cam.farClipPlane = 1000f;
        var rt = new RenderTexture(1280, 720, 24);
        cam.targetTexture = rt;
        cam.Render();
        RenderTexture.active = rt;
        var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
        tex.Apply();
        File.WriteAllBytes(path, tex.EncodeToPNG());
        RenderTexture.active = null;
        cam.targetTexture = null;
        Object.DestroyImmediate(rt);
        Object.DestroyImmediate(tex);
        Object.DestroyImmediate(go);
    }
}
