using System.Collections;
using System.IO;
using System.Reflection;
using UnityEngine;

// Облёт камеры окна снаряжения в Play (сцена Base): открывает окно, снимает экран по ходу облёта к лицу,
// закрывает и снимает облёт обратно за спину. Снимки — в Temp/EquipmentShots/blend/<open|close>_<ход в %>.png:
// игрок должен быть в кадре на всех. Профиль не меняется.
// Перед запуском: editor_play, editor_focus, set_autotick --enable true (CLAUDE.md, «Проверка после правки кода»).
// Запуск: unity command run_script --file Tools/Unity/ProbeEquipmentBlend.cs
public static class ProbeEquipmentBlend
{
    const string Folder = "Temp/EquipmentShots/blend";
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    // Ход облёта, на котором делается снимок
    static readonly float[] Marks = { 0f, 0.2f, 0.4f, 0.6f, 0.8f, 1f };

    public static string Main()
    {
        Application.runInBackground = true;
        if (!Application.isPlaying)
            return "нужен Play mode (Base)";
        Directory.CreateDirectory(Folder);
        foreach (var file in Directory.GetFiles(Folder, "*.png"))
            File.Delete(file);
        var screen = Object.FindAnyObjectByType<EquipmentScreen>();
        screen.StartCoroutine(Run(screen, Object.FindAnyObjectByType<OutfitPreview>()));
        return $"снимки облёта — в {Folder} через пару секунд";
    }

    static IEnumerator Run(EquipmentScreen screen, OutfitPreview preview)
    {
        typeof(EquipmentScreen).GetMethod("Open", Private).Invoke(screen, null);
        yield return Shoot(preview, "open", true);
        yield return new WaitForSecondsRealtime(0.5f);
        typeof(EquipmentScreen).GetMethod("Close", Private).Invoke(screen, null);
        yield return Shoot(preview, "close", false);
    }

    // Снимок в первом кадре, где ход облёта дошёл до очередной отметки
    static IEnumerator Shoot(OutfitPreview preview, string name, bool opening)
    {
        int next = opening ? 0 : Marks.Length - 1;
        while (opening ? next < Marks.Length : next >= 0)
        {
            float t = preview.Progress;
            if (opening ? t >= Marks[next] : t <= Marks[next])
            {
                ScreenCapture.CaptureScreenshot($"{Folder}/{name}_{Mathf.RoundToInt(t * 100):000}.png", 1);
                next += opening ? 1 : -1;
            }
            yield return null;
        }
    }
}
