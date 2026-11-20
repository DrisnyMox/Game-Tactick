using System.Text;
using UnityEngine;

// Проверка шума, защищённого кармана и бесплатного набора в Play mode сцены Raid. Запускать дважды с паузой в пару секунд:
//  1-й запуск: сохраняет профиль в запасной ключ, шумит выстрелом игрока, показывает, какие рейдеры услышали;
//  2-й запуск: идут ли рейдеры к шуму; кладёт игроку чертёж и материалы, уничтожает игрока и показывает, что сохранил
//  карман; проверяет бесплатный набор; возвращает профиль из запасного ключа.
// Запуск: unity command run_script --file Tools/Unity/ProbeRaidRules.cs --project-path "<путь к проекту>"
public static class ProbeRaidRules
{
    const string Stage = "ProbeRaidRules.stage", Backup = "ProbeRaidRules.profile";

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
            PlayerPrefs.SetString(Backup, PlayerPrefs.GetString("Profile.v1", ""));
            PlayerPrefs.SetInt(Stage, 1);
            // Игрок встаёт в 30 м от ближайшего рейдера и стреляет
            ShooterBot nearest = null;
            foreach (var raider in Object.FindObjectsByType<ShooterBot>())
                if (nearest == null || Vector3.Distance(raider.transform.position, player.transform.position) < Vector3.Distance(nearest.transform.position, player.transform.position))
                    nearest = raider;
            var controller = player.GetComponent<CharacterController>();
            var start = player.transform.position;
            controller.enabled = false;
            player.transform.position = nearest.transform.position + Vector3.right * 30f + Vector3.up * 3f;
            controller.enabled = true;
            Noise.Make(player.transform.position, Noise.ShotRadius, player);
            // Игрок уходит обратно: рейдеры идут к месту шума, а не к нему, и 2-й запуск застаёт его целым
            controller.enabled = false;
            player.transform.position = start;
            controller.enabled = true;
            foreach (var raider in Object.FindObjectsByType<ShooterBot>())
            {
                float d = Vector3.Distance(raider.transform.position, player.transform.position);
                s.Append($"{raider.name}: {d:F0} м, услышал {raider.Investigating}; ");
            }
            return s.ToString();
        }

        PlayerPrefs.DeleteKey(Stage);
        foreach (var raider in Object.FindObjectsByType<ShooterBot>())
            s.Append($"{raider.name}: {Vector3.Distance(raider.transform.position, player.transform.position):F0} м, проверяет {raider.Investigating}; ");

        var backpack = player.GetComponent<Backpack>();
        backpack.Add(ItemId.BigMagazineBlueprint, 1);
        backpack.Add(ItemId.Tech, 10);
        backpack.Add(ItemId.Scrap, 5);
        bool hadBlueprint = Profile.HasBlueprint(ItemId.BigMagazineBlueprint);
        int stashTech = Profile.Current.stash[ItemId.Tech];
        CharacterHealth killer = null;
        foreach (var other in CharacterHealth.All)
            if (other.Team != player.Team)
                killer = other;
        s.Append($"\nигрок цел {!player.IsDead}");
        // Настоящее попадание: луч в грудь игрока по его коллайдерам
        var from = player.Chest.position + player.transform.forward * 1.5f;
        RaycastHit bodyHit = default;
        foreach (var hit in Physics.RaycastAll(from, player.Chest.position - from, 3f, ~0, QueryTriggerInteraction.Ignore))
            if (hit.collider.GetComponentInParent<CharacterHealth>() == player)
                bodyHit = hit;
        player.OnShot(bodyHit, -player.transform.forward, 10000f, killer);
        var result = Profile.LastRaid;
        s.Append($"\nуничтожен: потеряно [{result.items.Describe()}], карман [{result.kept.Describe()}], " +
                 $"чертёж магазина был {hadBlueprint} стал {Profile.HasBlueprint(ItemId.BigMagazineBlueprint)}, " +
                 $"электроника на складе {stashTech} -> {Profile.Current.stash[ItemId.Tech]}");

        Profile.Current.carried[ItemId.Ammo] = 0;
        Profile.Current.stash[ItemId.Ammo] = 0;
        bool can = Profile.CanTakeFreeKit;
        Profile.TakeFreeKit();
        s.Append($"\nнабор: можно {can}, взят -> патронов {Profile.Current.carried[ItemId.Ammo]}, можно ещё {Profile.CanTakeFreeKit}");

        // Профиль как был до проверки
        PlayerPrefs.SetString("Profile.v1", PlayerPrefs.GetString(Backup));
        PlayerPrefs.DeleteKey(Backup);
        PlayerPrefs.Save();
        typeof(Profile).GetField("data", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).SetValue(null, null);
        s.Append($"\nпрофиль возвращён: чертёж магазина {Profile.HasBlueprint(ItemId.BigMagazineBlueprint)}, электроника {Profile.Current.stash[ItemId.Tech]}");
        return s.ToString();
    }
}
