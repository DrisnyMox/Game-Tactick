using System.Collections;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

// Окно верстака в Play (сцена Base): каждый запуск снимает экран (что сделал прошлый шаг) в
// Temp/WorkbenchShots/<номер>_<шаг>.png и через кадр делает следующий шаг. Шаги: открыть окно (на склад кладутся
// материалы, кочевники — на уровень 2), выбрать тунику, сделать её (карточка результата, кочевники — уровень 3), закрыть
// результат, вынести зелёные и синие штаны кочевника (Crafting.Learn: опыт, затем уровень 7), выбрать и сделать тканевый
// нагрудник (базовый набор, белое), выбрать патроны, сделать их, выбрать бронежилет (нужен чертёж), закрыть окно. Последний запуск
// возвращает профиль, каким он был до первого шага (ключ Profile.v1 копируется в PlayerPrefs и обратно).
// Перед запуском: editor_play, editor_focus, set_autotick --enable true (CLAUDE.md, «Проверка после правки кода»).
// Запускать с паузой в секунду между шагами: окно и карточка результата проявляются.
// Запуск: unity command run_script --file Tools/Unity/ProbeWorkbench.cs
public static class ProbeWorkbench
{
    const string Folder = "Temp/WorkbenchShots";
    const string StepKey = "ProbeWorkbench.step";
    const string Backup = "ProbeWorkbench.backup";
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    static readonly string[] Steps = { "start", "open", "tunic", "make_tunic", "dismiss", "learn", "basic", "make_basic", "ammo", "make_ammo", "armor", "closed" };

    public static string Main()
    {
        Application.runInBackground = true;
        if (!Application.isPlaying)
            return "нужен Play mode (Base)";
        var screen = Object.FindAnyObjectByType<WorkbenchScreen>();
        int step = SessionState.GetInt(StepKey, 0);
        Directory.CreateDirectory(Folder);
        ScreenCapture.CaptureScreenshot($"{Folder}/{step}_{Steps[step]}.png", 2);

        var profile = Profile.Current;
        string state = $"{step} {Steps[step]}: окно {LoopGui.ModalOpen}, базовый набор уровень {Crafting.Level(GearSet.Basic)}, кочевники уровень {Crafting.Level(GearSet.Nomads)} (опыт {Crafting.Xp(GearSet.Nomads)}), " +
                       $"частей {profile.gear.Count}, патронов на складе {profile.stash[ItemId.Ammo]}, " +
                       $"дерево {profile.stash[ItemId.Wood]}, металлолом {profile.stash[ItemId.Scrap]}";
        if (profile.gear.Count > 0)
        {
            var newest = profile.gear[^1];
            state += $", последняя часть {Gear.FullName(newest)}";
        }

        bool last = step == Steps.Length - 1;
        if (last)
            SessionState.EraseInt(StepKey);
        else
            SessionState.SetInt(StepKey, step + 1);
        // Снимок делается в конце кадра: шаг — после него, иначе в кадр попал бы уже следующий
        screen.StartCoroutine(Next(screen, step + 1));
        return state + (last ? "; профиль возвращён" : "");
    }

    static IEnumerator Next(WorkbenchScreen screen, int step)
    {
        yield return null;
        yield return null;
        if (step == Steps.Length)
        {
            PlayerPrefs.SetString("Profile.v1", PlayerPrefs.GetString(Backup));
            PlayerPrefs.DeleteKey(Backup);
            PlayerPrefs.Save();
            typeof(Profile).GetField("data", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, null);
            yield break;
        }
        switch (Steps[step])
        {
            case "open":
                PlayerPrefs.SetString(Backup, PlayerPrefs.GetString("Profile.v1", ""));
                var profile = Profile.Current;
                profile.stash[ItemId.Wood] += 40;
                profile.stash[ItemId.Scrap] += 40;
                profile.stash[ItemId.Stone] += 20;
                profile.stash[ItemId.Tech] += 4;
                // Кочевники на уровне 2 (обычно серое, белое — с шансом): туника поднимет их до 3, где обычно белое
                profile.setXp[(int)GearSet.Nomads] = Crafting.XpForLevel(3) - 1;
                screen.Open();
                break;
            case "tunic":
                Select(screen, e => e.gear != null && e.gear.result == GearKind.NomadChest);
                break;
            case "make_tunic":
                Call(screen, "Make");
                break;
            case "dismiss":
                Call(screen, "HideResult");
                break;
            case "learn":
                // Вынесенные с вылазки части: зелёная у кочевников на уровне 3 выпадает с шансом — опыт,
                // синяя ещё не выпадает — сразу уровень, где она начинает выпадать
                Crafting.Learn(new GearPiece { kind = GearKind.NomadPants, quality = GearQuality.Green });
                Debug.Log($"ProbeWorkbench: после зелёной кочевники уровень {Crafting.Level(GearSet.Nomads)}, опыт {Crafting.Xp(GearSet.Nomads)}");
                Crafting.Learn(new GearPiece { kind = GearKind.NomadPants, quality = GearQuality.Blue });
                Call(screen, "Refresh");
                break;
            case "basic":
                Select(screen, e => e.gear != null && e.gear.result == GearKind.ClothChest);
                break;
            case "make_basic":
                Call(screen, "Make");
                break;
            case "ammo":
                Call(screen, "HideResult");
                Select(screen, e => e.supply != null && e.supply.result == ItemId.Ammo);
                break;
            case "make_ammo":
                Call(screen, "Make");
                break;
            case "armor":
                Call(screen, "HideResult");
                Select(screen, e => e.supply != null && e.supply.result == ItemId.Armor);
                break;
            case "closed":
                Call(screen, "Close");
                break;
        }
    }

    // Рецепт в окне — закрытый класс Entry: поля gear и supply читаются рефлексией
    sealed class EntryView
    {
        public GearRecipe gear;
        public Recipe supply;
    }

    static void Select(WorkbenchScreen screen, System.Func<EntryView, bool> match)
    {
        var entries = (IList)typeof(WorkbenchScreen).GetField("entries", Private).GetValue(screen);
        foreach (var entry in entries)
        {
            var type = entry.GetType();
            var view = new EntryView
            {
                gear = (GearRecipe)type.GetField("gear").GetValue(entry),
                supply = (Recipe)type.GetField("supply").GetValue(entry),
            };
            if (match(view))
            {
                typeof(WorkbenchScreen).GetMethod("Select", Private).Invoke(screen, new[] { entry });
                return;
            }
        }
        throw new System.InvalidOperationException("рецепт не найден");
    }

    static void Call(WorkbenchScreen screen, string method) =>
        typeof(WorkbenchScreen).GetMethod(method, Private).Invoke(screen, null);
}
