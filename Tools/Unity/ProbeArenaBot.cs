using System.Text;
using StarterAssets;
using UnityEngine;

// Проверка боя команд в Play: по каждому персонажу из CharacterHealth.All - команда, здоровье, место, скорость,
// у ботов ввод (движение, прицел, огонь, патроны). Плюс готов ли NavMesh.
// Запуск: unity command run_script --file Tools/Unity/ProbeArenaBot.cs --project-path "D:\Game Tactick"
public static class ProbeArenaBot
{
    public static string Main()
    {
        var nav = Object.FindFirstObjectByType<ArenaNavMesh>();
        var log = new StringBuilder($"time {Time.time:0.0}, nav ready {nav.HasNavMesh} busy {nav.IsBusy} last build {nav.LastBuildMs:0} ms\n");
        int[] alive = new int[2];
        foreach (var character in CharacterHealth.All)
        {
            if (!character.IsDead)
                alive[character.Team]++;
            var shooter = character.GetComponent<SurvivorShooter>();
            var input = character.GetComponent<StarterAssetsInputs>();
            float speed = character.GetComponent<CharacterController>().velocity.magnitude;
            log.Append($"{character.name,-14} team {character.Team} hp {character.Health,3:0} {character.transform.position} speed {speed:0.0}");
            if (shooter.BotControlled)
                log.Append($" move {input.move} aim {shooter.AimInput} fire {shooter.FireInput} ammo {shooter.Ammo}");
            log.AppendLine();
        }
        log.AppendLine($"alive: blue {alive[0]}, red {alive[1]}");
        return log.ToString();
    }
}
