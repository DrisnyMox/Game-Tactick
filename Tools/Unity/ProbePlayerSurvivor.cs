using System.Linq;
using System.Text;
using StarterAssets;
using UnityEngine;

// Проверка игрока в Play mode: задаёт ввод StarterAssetsInputs и отдаёт состояние игрока.
// move = "x,y" (как WASD: 0,1 - вперёд), sprint = true/false.
// Запуск через Unity CLI в Play mode (из корня проекта):
//     unity command run_script --file Tools/Unity/ProbePlayerSurvivor.cs --project-path "D:\Game Tactick" -- --args "[\"0,1\", false]"
public static class ProbePlayerSurvivor
{
    public static string Main(string move, bool sprint)
    {
        var player = Object.FindObjectsByType<ThirdPersonController>(FindObjectsSortMode.None).Single();
        var input = player.GetComponent<StarterAssetsInputs>();
        var parts = move.Split(',');
        input.move = new Vector2(float.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
                                 float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
        input.sprint = sprint;

        var animator = player.GetComponent<Animator>();
        var t = player.transform;
        var state = animator.GetCurrentAnimatorStateInfo(0);
        Vector3 Local(HumanBodyBones b) => t.InverseTransformPoint(animator.GetBoneTransform(b).position);
        var cam = Camera.main.transform;

        var s = new StringBuilder();
        s.AppendLine($"time={Time.time:0.00} frame={Time.frameCount}");
        s.AppendLine($"pos={t.position:F2} yaw={t.eulerAngles.y:0} grounded={player.Grounded}");
        s.AppendLine($"anim Speed={animator.GetFloat("Speed"):0.00} Grounded={animator.GetBool("Grounded")} " +
                     $"state={(state.IsName("Idle Walk Run Blend") ? "Idle Walk Run Blend" : state.fullPathHash.ToString())} t={state.normalizedTime:0.00}");
        s.AppendLine($"toes-foot forward L={Local(HumanBodyBones.LeftToes).z - Local(HumanBodyBones.LeftFoot).z:+0.00;-0.00} " +
                     $"R={Local(HumanBodyBones.RightToes).z - Local(HumanBodyBones.RightFoot).z:+0.00;-0.00} " +
                     $"hips y={Local(HumanBodyBones.Hips).y:0.00} head y={Local(HumanBodyBones.Head).y:0.00} " +
                     $"hands y L={Local(HumanBodyBones.LeftHand).y:0.00} R={Local(HumanBodyBones.RightHand).y:0.00}");
        s.AppendLine($"camera pos={cam.position:F2} looks={cam.forward:F2} dist={Vector3.Distance(cam.position, t.position):0.00}");
        return s.ToString();
    }
}
