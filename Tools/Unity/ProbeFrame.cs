using System.Linq;
using StarterAssets;
using UnityEngine;

// Одна строка на кадр для поиска дрожи камеры: углы камеры, точки PlayerCameraRoot и персонажа.
// Вызывать по разу после каждого EditorApplication.Step() на паузе.
// Запуск через Unity CLI в Play mode (из корня проекта):
//     unity command run_script --file Tools/Unity/ProbeFrame.cs --project-path "D:\Game Tactick"
public static class ProbeFrame
{
    public static string Main()
    {
        var player = Object.FindObjectsByType<ThirdPersonController>(FindObjectsSortMode.None).Single();
        var root = player.CinemachineCameraTarget.transform;
        var cam = Camera.main.transform;
        var weapon = player.GetComponentInChildren<ShooterWeapon>();
        return $"f={Time.frameCount} cam yaw={cam.eulerAngles.y:0.00} pitch={cam.eulerAngles.x:0.00} pos={cam.position:F3} | " +
               $"root yaw={root.eulerAngles.y:0.00} | player yaw={player.transform.eulerAngles.y:0.00} | " +
               $"muzzle={weapon.Muzzle.forward:F3}";
    }
}
