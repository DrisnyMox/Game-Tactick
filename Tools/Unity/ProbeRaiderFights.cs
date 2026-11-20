using System.Text;
using UnityEngine;

// Рейдеры на вылазке в Play mode: у каждого своя команда, кто уничтожен и кто в кого последним попал.
// Запуск: unity command run_script --file Tools/Unity/ProbeRaiderFights.cs --project-path "<путь к проекту>"
public static class ProbeRaiderFights
{
    public static string Main()
    {
        Application.runInBackground = true;
        var s = new StringBuilder();
        foreach (var character in CharacterHealth.All)
            s.Append($"{character.name}: команда {character.Team}, {(character.IsDead ? "уничтожен" : "цел")}, " +
                     $"попал последним {(character.LastAttacker != null ? character.LastAttacker.name : "-")}; ");
        return s.ToString();
    }
}
