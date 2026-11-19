using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

// Срез в Play mode: держит игру в фоне (runInBackground), печатает сцену, игрока, профиль, рюкзак,
// точки эвакуации, добычу и рейдеров.
// Запуск: unity command run_script --file Tools/Unity/ProbeLoop.cs --project-path "<путь к проекту>"
public static class ProbeLoop
{
    public static string Main()
    {
        Application.runInBackground = true;
        EditorApplication.QueuePlayerLoopUpdate();
        var s = new StringBuilder();
        s.AppendLine($"frame {Time.frameCount} time {Time.time:F1} timeScale {Time.timeScale} scene {SceneManager.GetActiveScene().name} modal {LoopGui.ModalOpen}");
        if (!Application.isPlaying)
            return s.ToString();
        var p = Profile.Current;
        s.AppendLine($"profile: credits {p.credits} raids {p.raids} extractions {p.extractions}; stash [{p.stash.Describe()}]; carried [{p.carried.Describe()}]");
        foreach (var c in CharacterHealth.All)
        {
            var bag = c.GetComponent<Backpack>();
            var bot = c.GetComponent<ShooterBot>();
            s.AppendLine($"  {c.name} team {c.Team} hp {c.Health:F0}/{c.MaxHealth:F0} pos {c.transform.position} active {c.gameObject.activeSelf}" +
                         (bag != null ? $" bag {bag.Weight}/{bag.Capacity} [{bag.Contents.Describe()}]" : "") +
                         (bot != null && bot.enabled ? $" wantsExtract {bot.WantsExtract}" : ""));
        }
        foreach (var z in ExtractionZone.All)
            s.AppendLine($"  zone {z.name} {z.transform.position}");
        foreach (var pk in Pickup.All)
            s.AppendLine($"  pickup {pk.Label} {pk.transform.position} [{pk.Contents.Describe()}]");
        return s.ToString();
    }
}
