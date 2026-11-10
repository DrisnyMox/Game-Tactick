using System.Linq;
using UnityEngine;

// Ставит в Play mode камеру ProbeCam, нацеленную на игрока, чтобы снять его с нужной стороны
// (capture_game_view --camera ProbeCam). side = "front" | "right" | "left" | "front-right" | "remove".
// Запуск через Unity CLI (из корня проекта):
//     unity command run_script --file Tools/Unity/ProbeCamera.cs --project-path "D:\Game Tactick" -- --args "[\"front-right\", 2.2]"
public static class ProbeCamera
{
    public static string Main(string side, float distance)
    {
        var existing = GameObject.Find("ProbeCam");
        if (existing != null)
            Object.Destroy(existing);
        if (side == "remove")
            return "removed";

        var player = Object.FindObjectsByType<SurvivorShooter>(FindObjectsSortMode.None).Single().transform;
        var local = side switch
        {
            "front" => new Vector3(0, 0, 1),
            "right" => new Vector3(1, 0, 0),
            "left" => new Vector3(-1, 0, 0),
            _ => new Vector3(0.8f, 0, 1),
        };
        var go = new GameObject("ProbeCam");
        var cam = go.AddComponent<Camera>();
        cam.enabled = false;
        cam.fieldOfView = 40f;
        var target = player.position + Vector3.up * 1.25f;
        go.transform.position = target + player.TransformDirection(local.normalized) * distance + Vector3.up * 0.15f;
        go.transform.LookAt(target);
        return $"ProbeCam at {go.transform.position:F2}";
    }
}
