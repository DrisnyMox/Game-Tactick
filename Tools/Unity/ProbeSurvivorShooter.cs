using System.Linq;
using System.Text;
using StarterAssets;
using UnityEngine;

// Проверка стрельбы в Play mode. Ставит ввод в поля SurvivorShooter и StarterAssetsInputs, как это делает PlayerInput,
// и отдаёт отчёт: куда смотрит ствол, где левая ладонь, сколько патронов, что играет слой Rifle.
// Мышь из скрипта не нажать: Input System сбрасывает устройства, пока окно игры не в фокусе.
// mouse = "none" | "aim" | "fire" | "aimfire"; key = "" | "r" (перезарядка); move = "x,y" для StarterAssetsInputs;
// pitch - наклон камеры в градусах (вниз положительный), "" - не трогать.
// Запуск через Unity CLI в Play mode (из корня проекта):
//     unity command run_script --file Tools/Unity/ProbeSurvivorShooter.cs --project-path "D:\Game Tactick" -- --args "[\"aim\", \"\", \"0,0\", \"\"]"
public static class ProbeSurvivorShooter
{
    public static string Main(string mouse, string key, string move, string pitch)
    {
        var shooter = Object.FindObjectsByType<SurvivorShooter>(FindObjectsSortMode.None).Single();
        shooter.AimInput = mouse.Contains("aim");
        shooter.FireInput = mouse.Contains("fire");
        if (key == "r")
            shooter.ReloadInput = true;
        var input = shooter.GetComponent<StarterAssetsInputs>();
        var parts = move.Split(',');
        input.move = new Vector2(float.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
                                 float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
        if (pitch != "")
        {
            // ThirdPersonController держит углы камеры в закрытых полях, их и надо менять
            var field = typeof(ThirdPersonController).GetField("_cinemachineTargetPitch",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            field.SetValue(shooter.GetComponent<ThirdPersonController>(),
                float.Parse(pitch, System.Globalization.CultureInfo.InvariantCulture));
        }

        var animator = shooter.GetComponent<Animator>();
        var weapon = shooter.GetComponentInChildren<ShooterWeapon>();
        var cam = Camera.main.transform;
        var t = shooter.transform;
        var leftPalm = Vector3.Lerp(animator.GetBoneTransform(HumanBodyBones.LeftHand).position,
                                    animator.GetBoneTransform(HumanBodyBones.LeftMiddleProximal).position, 0.6f);
        var muzzleDir = weapon.Muzzle.forward;
        var rifleLayer = animator.GetCurrentAnimatorStateInfo(1);
        string layerState = new[] { "Hold", "Aim", "Reload" }.FirstOrDefault(n => rifleLayer.IsName(n)) ?? rifleLayer.fullPathHash.ToString();
        if (animator.GetCurrentAnimatorStateInfo(2).IsName("Shot"))
            layerState += "+Shot";
        var baseState = animator.GetCurrentAnimatorStateInfo(0);
        string baseName = new[] { "Idle Walk Run Blend", "Aim Strafe", "JumpStart", "InAir", "JumpLand" }.FirstOrDefault(n => baseState.IsName(n)) ?? baseState.fullPathHash.ToString();

        var s = new StringBuilder();
        s.AppendLine($"time={Time.time:0.00} pos={t.position:F2} yaw={t.eulerAngles.y:0} cam yaw={cam.eulerAngles.y:0} pitch={cam.eulerAngles.x:0}");
        s.AppendLine($"ammo={shooter.Ammo} reloading={shooter.Reloading} base={baseName} rifle={layerState} Aiming={animator.GetBool("Aiming")}");
        var own = shooter.GetComponentsInChildren<Collider>(true);
        var hits = Physics.RaycastAll(cam.position, cam.forward, 200f, ~0, QueryTriggerInteraction.Ignore)
            .Where(h => System.Array.IndexOf(own, h.collider) < 0).OrderBy(h => h.distance).ToArray();
        var aimPoint = hits.Length > 0 ? hits[0].point : cam.position + cam.forward * 200f;
        s.AppendLine($"muzzle dir={muzzleDir:F2} cam fwd={cam.forward:F2} angle to aim point={Vector3.Angle(muzzleDir, aimPoint - weapon.Muzzle.position):0.0} " +
                     $"(aim point {(hits.Length > 0 ? hits[0].collider.name : "sky")} at {Vector3.Distance(cam.position, aimPoint):0.0} m)");
        s.AppendLine($"left palm to grip={Vector3.Distance(leftPalm, weapon.LeftHandGrip.position):0.000} m, muzzle height={weapon.Muzzle.position.y - t.position.y:0.00}");
        return s.ToString();
    }
}
