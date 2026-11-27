using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

// Окно снаряжения в Play (сцена Base): каждый запуск снимает экран (что сделал прошлый шаг) в
// Temp/EquipmentShots/<номер>_<шаг>.png и через кадр делает следующий шаг. Шаги: открыть окно (в профиль
// добавляются зелёная туника, синие и оранжевые кеды и оранжевый колпак Культа Солнца, надеваются зелёные штаны и фиолетовые перчатки
// базового набора), выбрать нагрудник, надеть тунику, выбрать
// оранжевые кеды, надеть их, выбрать шлем (камера к лицу) и колпак, надеть его, закрыть окно. Последний запуск снимает игрока в мире и возвращает профиль, каким он был
// до первого шага (ключ Profile.v1 копируется в PlayerPrefs и обратно).
// Перед запуском: editor_play, editor_focus, set_autotick --enable true (CLAUDE.md, «Проверка после правки кода»).
// Запускать с паузой в секунду-две между шагами: окно проявляется, камера персонажа доезжает.
// Запуск: unity command run_script --file Tools/Unity/ProbeEquipment.cs
public static class ProbeEquipment
{
    const string Folder = "Temp/EquipmentShots";
    const string StepKey = "ProbeEquipment.step";
    const string Backup = "ProbeEquipment.backup";
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    static readonly string[] Steps = { "start", "open", "chest", "wear_tunic", "feet", "wear_boots", "head", "wear_head", "closed" };

    public static string Main()
    {
        Application.runInBackground = true;
        if (!Application.isPlaying)
            return "нужен Play mode (Base)";
        var screen = Object.FindAnyObjectByType<EquipmentScreen>();
        int step = SessionState.GetInt(StepKey, 0);
        Directory.CreateDirectory(Folder);
        ScreenCapture.CaptureScreenshot($"{Folder}/{step}_{Steps[step]}.png", 2);

        var worn = Object.FindAnyObjectByType<WornGear>();
        var shown = worn.GetComponentsInChildren<SkinnedMeshRenderer>().Skip(1).Select(r => r.name + (r.gameObject.layer == 7 ? "(Preview)" : ""));
        string state = $"{step} {Steps[step]}: окно {LoopGui.ModalOpen}, надето в профиле [{string.Join(", ", Profile.Worn().Select(Gear.FullName))}], " +
                       $"на игроке [{string.Join(", ", shown)}], частей в профиле {Profile.Current.gear.Count}";

        bool last = step == Steps.Length - 1;
        if (last)
            SessionState.EraseInt(StepKey);
        else
            SessionState.SetInt(StepKey, step + 1);
        // Снимок делается в конце кадра: шаг — после него, иначе в кадр попал бы уже следующий
        screen.StartCoroutine(Next(screen, worn, step + 1));
        return state + (last ? "; профиль возвращён" : "");
    }

    static IEnumerator Next(EquipmentScreen screen, WornGear worn, int step)
    {
        yield return null;
        yield return null;
        if (step == Steps.Length)
        {
            PlayerPrefs.SetString("Profile.v1", PlayerPrefs.GetString(Backup));
            PlayerPrefs.DeleteKey(Backup);
            PlayerPrefs.Save();
            typeof(Profile).GetField("data", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, null);
            Gear.Dress(worn, Profile.Worn());
            yield break;
        }
        switch (Steps[step])
        {
            case "open":
                PlayerPrefs.SetString(Backup, PlayerPrefs.GetString("Profile.v1", ""));
                var gear = Profile.Current.gear;
                gear.Add(new GearPiece { kind = GearKind.NomadChest, quality = GearQuality.Green });
                gear.Add(new GearPiece { kind = GearKind.SunShoes, quality = GearQuality.Blue });
                gear.Add(new GearPiece { kind = GearKind.SunShoes, quality = GearQuality.Orange });
                gear.Add(new GearPiece { kind = GearKind.SunHead, quality = GearQuality.Orange });
                // Скрафченный базовый набор в цвете: ремни штанов и манжета перчаток красятся по маске
                var pants = new GearPiece { kind = GearKind.ClothPantsRope, quality = GearQuality.Green };
                var gloves = new GearPiece { kind = GearKind.ClothGloves, quality = GearQuality.Purple };
                gear.Add(pants);
                gear.Add(gloves);
                Profile.Wear(pants);
                Profile.Wear(gloves);
                Gear.Dress(worn, Profile.Worn());
                Call(screen, "Open");
                break;
            case "chest":
                Call(screen, "SelectSlot", GearSlot.Chest);
                break;
            case "wear_tunic":
                Call(screen, "SelectCard", GearKind.NomadChest, GearQuality.Green);
                Call(screen, "WearSelected");
                break;
            case "feet":
                Call(screen, "SelectSlot", GearSlot.Feet);
                Call(screen, "SelectCard", GearKind.SunShoes, GearQuality.Orange);
                break;
            case "wear_boots":
                Call(screen, "WearSelected");
                break;
            case "head":
                Call(screen, "SelectSlot", GearSlot.Head);
                Call(screen, "SelectCard", GearKind.SunHead, GearQuality.Orange);
                break;
            case "wear_head":
                Call(screen, "WearSelected");
                break;
            case "closed":
                Call(screen, "Close");
                break;
        }
    }

    static void Call(EquipmentScreen screen, string method, params object[] args) =>
        typeof(EquipmentScreen).GetMethod(method, Private).Invoke(screen, args);
}
