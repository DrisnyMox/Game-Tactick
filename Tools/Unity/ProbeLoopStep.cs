using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

// Шаг цикла в Play mode без ввода: на базе — переход в вылазку (сцена Raid), на итоге вылазки — «На базу», на вылазке —
// игрок переносится в ближайшую точку эвакуации (через 10 с он эвакуирован).
// Запуск: unity command run_script --file Tools/Unity/ProbeLoopStep.cs --project-path "<путь к проекту>"
public static class ProbeLoopStep
{
    public static string Main()
    {
        Application.runInBackground = true;
        EditorApplication.QueuePlayerLoopUpdate();
        var scene = SceneManager.GetActiveScene().name;
        if (scene == BaseHub.SceneName)
        {
            SceneManager.LoadScene(RaidDirector.SceneName);
            return "loading Raid";
        }
        if (RaidDirector.Current == null || ExtractionZone.All.Count == 0)
            return "raid not ready";
        // Итог вылазки на экране: как кнопка «На базу»
        if (LoopGui.ModalOpen)
        {
            typeof(RaidDirector).GetMethod("ReturnToBase", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Invoke(RaidDirector.Current, null);
            return "returning to Base";
        }
        var player = Object.FindObjectsByType<SurvivorShooter>(FindObjectsSortMode.None);
        foreach (var shooter in player)
        {
            if (shooter.BotControlled)
                continue;
            var zone = RaidDirector.Current.NearestExtraction(shooter.transform.position);
            var controller = shooter.GetComponent<CharacterController>();
            controller.enabled = false;
            shooter.transform.position = zone + Vector3.up * 0.5f;
            controller.enabled = true;
            return $"player moved to {zone}";
        }
        return "no player";
    }
}
