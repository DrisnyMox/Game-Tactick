using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

// Снимки надетого снаряжения вблизи в Play (сцена SurvivorPlayground): числам замера одним верить нельзя,
// вид проверяется глазами. Временные камеры ставятся вокруг кистей игрока (спереди, сзади, сбоку, сверху,
// снизу), вокруг всего игрока, вокруг головы (головной убор), плюс кадр камеры игры. Снимки - в
// Temp/GearShots/<режим>_*.png. Первый запуск снимает игрока как он стоит и включает прицел
// (SurvivorShooter.AimInput), второй (через секунду-две) - в прицеле, с винтовкой в руках, и выключает прицел.
// Перед запуском: editor_play, editor_focus, set_autotick --enable true (CLAUDE.md, «Проверка после правки кода»).
// Запуск: unity command run_script --file Tools/Unity/ProbeGearShots.cs
public static class ProbeGearShots
{
    const string Folder = "Temp/GearShots";
    const string AimKey = "ProbeGearShots.aim";
    const int Size = 640;

    public static string Main()
    {
        Application.runInBackground = true;
        if (!Application.isPlaying)
            return "нужен Play mode (SurvivorPlayground)";
        var shooter = Object.FindObjectsByType<SurvivorShooter>().Single(s => !s.BotControlled);
        var root = shooter.transform;
        bool aim = SessionState.GetBool(AimKey, false);
        string mode = aim ? "aim" : "idle";
        Directory.CreateDirectory(Folder);
        var bones = root.GetComponentsInChildren<Transform>(true).GroupBy(t => t.name).ToDictionary(g => g.Key, g => g.First());
        int count = 0;
        foreach (var side in new[] { "Left", "Right" })
        {
            var hand = bones[side + "Hand"];
            var middle = bones[side + "MiddleProximal"];
            var target = Vector3.Lerp(hand.position, middle.position, 0.5f);
            foreach (var (name, dir) in Directions(root))
                Shoot($"{mode}_{side}_{name}", target, dir, 0.8f, 30f, ref count);
        }
        var chest = bones["Chest"].position;
        foreach (var (name, dir) in Directions(root).Take(4))
            Shoot($"{mode}_body_{name}", chest, dir, 2.2f, 40f, ref count);
        // голова вблизи: головной убор (SunHead) - кожа и уши у края, клапан на затылке
        var head = bones["Head"].position + Vector3.up * 0.08f;
        foreach (var (name, dir) in Directions(root))
            Shoot($"{mode}_head_{name}", head, dir, 0.9f, 25f, ref count);
        Shoot(Camera.main, $"{mode}_game", ref count);
        shooter.AimInput = !aim;
        SessionState.SetBool(AimKey, !aim);
        return $"{mode}: {count} снимков в {Folder}; прицел {(aim ? "выключен" : "включён, запусти ещё раз через пару секунд")}";
    }

    static (string, Vector3)[] Directions(Transform root) => new[]
    {
        ("front", root.forward), ("back", -root.forward), ("left", -root.right), ("right", root.right),
        ("top", Vector3.up + root.forward * 0.15f), ("bottom", -Vector3.up + root.forward * 0.15f),
    };

    static void Shoot(string name, Vector3 target, Vector3 from, float distance, float fov, ref int count)
    {
        var go = new GameObject("ProbeGearShotsCamera");
        var cam = go.AddComponent<Camera>();
        cam.transform.SetPositionAndRotation(target + from.normalized * distance,
            Quaternion.LookRotation(-from.normalized, Mathf.Abs(from.normalized.y) > 0.9f ? Vector3.forward : Vector3.up));
        cam.fieldOfView = fov;
        cam.nearClipPlane = 0.02f;
        Shoot(cam, name, ref count);
        Object.DestroyImmediate(go);
    }

    static void Shoot(Camera cam, string name, ref int count)
    {
        var rt = new RenderTexture(Size, Size, 24);
        var old = cam.targetTexture;
        cam.targetTexture = rt;
        cam.Render();
        RenderTexture.active = rt;
        var tex = new Texture2D(Size, Size, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
        tex.Apply();
        File.WriteAllBytes(Path.Combine(Folder, name + ".png"), tex.EncodeToPNG());
        RenderTexture.active = null;
        cam.targetTexture = old;
        Object.DestroyImmediate(rt);
        Object.DestroyImmediate(tex);
        count++;
    }
}
