using System.IO;
using UnityEditor;
using UnityEngine;

// Снимки арены сверху в Play mode: временная камера над серединой карты (вид сверху и под углом),
// PNG в Temp/Shots проекта (вне Assets). Камера игрока не трогается.
// Запуск: unity command run_script --file Tools/Unity/ProbeIslandShot.cs --project-path "<путь к проекту>"
public static class ProbeIslandShot
{
    public static string Main()
    {
        Application.runInBackground = true;
        var arena = Object.FindAnyObjectByType<VoxelArena>();
        var bounds = arena.WorldBounds;
        Directory.CreateDirectory("Temp/Shots");
        Shot("Temp/Shots/island_top.png", bounds.center + Vector3.up * 230f, Quaternion.Euler(90f, 0f, 0f));
        Shot("Temp/Shots/island_angle.png", bounds.center + new Vector3(0f, 70f, -bounds.extents.z - 40f), Quaternion.Euler(28f, 0f, 0f));
        return "Temp/Shots/island_top.png, Temp/Shots/island_angle.png";
    }

    static void Shot(string path, Vector3 position, Quaternion rotation)
    {
        var go = new GameObject("Probe Camera");
        var cam = go.AddComponent<Camera>();
        cam.transform.SetPositionAndRotation(position, rotation);
        cam.fieldOfView = 50f;
        cam.farClipPlane = 2000f;
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
