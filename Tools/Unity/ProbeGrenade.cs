using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

// Проверка гранаты в Play mode (SampleScene или Raid). Запускать несколько раз подряд с паузой в 1–3 с:
//  1-й запуск: взрыв гранаты игрока в 2,5 м сбоку от ближайшего врага (сразу, без запала);
//     с этого момента считаются выбитые воксели (воронка и удары костей) и взрывы чужих гранат (шум Noise.BlastRadius);
//  следующие запуски: здоровье врага, сбит ли с ног, скорость таза, где корень; сколько вокселей выбито и где;
//     сколько гранат бросили боты. Встал — печатает «встал» и сбрасывает счёт (следующий запуск — снова 1-й).
// Запуск: unity command run_script --file Tools/Unity/ProbeGrenade.cs --project-path "<путь к проекту>"
public static class ProbeGrenade
{
    const string Stage = "ProbeGrenade.stage", Victim = "ProbeGrenade.victim", Log = "ProbeGrenade.log";
    const string Blasts = "ProbeGrenade.blasts", StartX = "ProbeGrenade.x", StartZ = "ProbeGrenade.z";

    public static string Main()
    {
        Application.runInBackground = true;
        CharacterHealth player = null;
        foreach (var shooter in Object.FindObjectsByType<SurvivorShooter>())
            if (!shooter.BotControlled)
                player = shooter.GetComponent<CharacterHealth>();
        var s = new StringBuilder();

        if (PlayerPrefs.GetInt(Stage, 0) == 0)
        {
            CharacterHealth victim = null;
            foreach (var other in CharacterHealth.All)
                if (other != player && other.Team != player.Team && !other.IsDead && other.gameObject.activeInHierarchy &&
                    (victim == null || Vector3.Distance(other.transform.position, player.transform.position) <
                                       Vector3.Distance(victim.transform.position, player.transform.position)))
                    victim = other;
            PlayerPrefs.SetInt(Stage, 1);
            PlayerPrefs.SetString(Victim, victim.name);
            PlayerPrefs.SetString(Log, "");
            PlayerPrefs.SetInt(Blasts, 0);
            PlayerPrefs.SetFloat(StartX, victim.transform.position.x);
            PlayerPrefs.SetFloat(StartZ, victim.transform.position.z);

            var arena = Object.FindAnyObjectByType<VoxelArena>();
            float start = Time.time;
            arena.VoxelsRemoved += (center, removed) =>
                PlayerPrefs.SetString(Log, PlayerPrefs.GetString(Log, "") +
                    $"  {Time.time - start:F2} с: {removed.Count} вокс. у ({center.x:F1}, {center.y:F1}, {center.z:F1})\n");
            Noise.Made += (position, radius, source) =>
            {
                if (radius == Noise.BlastRadius && source != player)
                    PlayerPrefs.SetInt(Blasts, PlayerPrefs.GetInt(Blasts, 0) + 1);
            };

            var prefab = AssetDatabase.LoadAssetAtPath<Grenade>("Assets/Weapons/Grenade.prefab");
            var side = Vector3.Cross(Vector3.up, (victim.transform.position - player.transform.position).normalized);
            var point = victim.transform.position + side * 2.5f + Vector3.up * 0.3f;
            var grenade = Object.Instantiate(prefab, point, Quaternion.identity);
            grenade.Launch(player, Vector3.zero, player.GetComponentsInChildren<Collider>(true));
            typeof(Grenade).GetMethod("Explode", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(grenade, null);
            s.Append($"сцена {UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}; взрыв у {victim.name} ({point.x:F1}, {point.y:F1}, {point.z:F1}); ");
        }

        CharacterHealth target = null;
        foreach (var other in CharacterHealth.All)
            if (other.name == PlayerPrefs.GetString(Victim))
                target = other;
        var hips = target.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.Hips);
        var moved = new Vector2(target.transform.position.x - PlayerPrefs.GetFloat(StartX), target.transform.position.z - PlayerPrefs.GetFloat(StartZ));
        s.Append($"{target.name}: здоровье {target.Health:F0}/{target.MaxHealth:F0}, уничтожен {target.IsDead}, сбит {target.KnockedDown}, " +
                 $"скорость таза {hips.GetComponent<Rigidbody>().linearVelocity.magnitude:F1} м/с, таз на высоте {hips.position.y:F2}, " +
                 $"корень сдвинулся на {moved.magnitude:F1} м, CharacterController {target.GetComponent<CharacterController>().enabled}, " +
                 $"бот {target.GetComponent<ShooterBot>().enabled}, аниматор {target.GetComponent<Animator>().enabled}\n");
        s.Append($"выбито вокселей:\n{PlayerPrefs.GetString(Log, "")}");
        s.Append($"взрывов чужих гранат: {PlayerPrefs.GetInt(Blasts, 0)}, гранат в полёте: {Object.FindObjectsByType<Grenade>().Length}\n");
        if (target.IsDead || (!target.KnockedDown && PlayerPrefs.GetInt(Stage) > 1))
        {
            s.Append(target.IsDead ? "уничтожен, счёт сброшен" : "встал, счёт сброшен");
            PlayerPrefs.SetInt(Stage, 0);
        }
        else
            PlayerPrefs.SetInt(Stage, PlayerPrefs.GetInt(Stage) + 1);
        return s.ToString();
    }
}
