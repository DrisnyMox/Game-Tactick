using System.Reflection;
using UnityEngine;

// Проверка отклика стрельбы в Play.
//  "place" - ставит живого врага в 6 м перед игроком лицом к нему (его бот выключается) и наводит камеру
//            игрока на его грудь (или голову, вторым аргументом "head");
//            (игрок держит прицел, союзные боты выключаются, чтобы не убили врага первыми);
//  "aim"   - снова навести камеру (отдача сбивает её вверх);
//  "shot"  - один выстрел игрока (SurvivorShooter.Shoot);
//  "stop"  - отпускает прицел и включает ботов обратно;
//  "capture" - только снимок.
//  Третий аргумент - имя снимка окна игры вместе с интерфейсом: Assets/Screenshots/<имя>.png, снимается в конце кадра;
//  без аргумента и после "shot" - печатает отметку попадания (давно ли, в голову ли, убийство ли),
//  здоровье врага, ленту убийств, во что смотрит камера.
// Запуск: unity command run_script --file Tools/Unity/ProbeHitFeedback.cs --project-path "D:\Game Tactick" -- --args "[\"place\"]"
public static class ProbeHitFeedback
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static CharacterHealth target;

    public static string Main(string mode = "", string part = "chest", string capture = "")
    {
        // Снимок окна игры вместе с интерфейсом (OnGUI, Canvas): Unity пишет его в конце этого кадра
        if (capture != "")
            ScreenCapture.CaptureScreenshot($"Assets/Screenshots/{capture}.png");
        if (mode == "capture")
            return $"capture {capture}";
        var player = GameObject.Find("PlayerSurvivor");
        var shooter = player.GetComponent<SurvivorShooter>();
        var tpc = player.GetComponent<StarterAssets.ThirdPersonController>();
        var enemy = target != null && !target.IsDead ? target : FindLivingEnemy();
        target = enemy;

        if (mode == "place")
        {
            var forward = player.transform.forward;
            var point = player.transform.position + forward * 6f;
            enemy.Revive(point, Quaternion.LookRotation(-forward));
            // Все боты стоят: союзники убили бы врага раньше игрока, враги - самого игрока
            foreach (var c in CharacterHealth.All)
                if (c.GetComponent<ShooterBot>() != null)
                    c.GetComponent<ShooterBot>().enabled = false;
            shooter.Refill();
            // С прицелом камера стоит у плеча, как у живого игрока
            shooter.AimInput = true;
            Aim(tpc, enemy, part);
            return $"{enemy.name} placed at {point}";
        }
        if (mode == "ally")
        {
            // Союзник встаёт в 6 м перед игроком, камера смотрит ему в грудь: прицел должен стать синим
            CharacterHealth ally = null;
            foreach (var c in CharacterHealth.All)
                if (c.Team == 0 && c.gameObject != player && c.GetComponent<ShooterBot>() != null)
                    ally = c;
            enemy.Revive(player.transform.position + player.transform.right * 15f, Quaternion.identity);
            var forward = player.transform.forward;
            ally.Revive(player.transform.position + forward * 6f, Quaternion.LookRotation(-forward));
            ally.GetComponent<ShooterBot>().enabled = false;
            Aim(tpc, ally, part);
            return $"{ally.name} placed";
        }
        if (mode == "aim")
        {
            Aim(tpc, enemy, part);
            return Report(shooter, enemy);
        }
        if (mode == "shot")
        {
            typeof(SurvivorShooter).GetMethod("Shoot", Private).Invoke(shooter, null);
            return Report(shooter, enemy);
        }
        if (mode == "hurt")
        {
            // Один выстрел врага в грудь игрока (его бот выключен): стрелка урона появляется в этом же кадре
            var playerHealth = player.GetComponent<CharacterHealth>();
            var enemyShooter = enemy.GetComponent<SurvivorShooter>();
            enemy.GetComponent<ShooterBot>().enabled = false;
            enemyShooter.AimTarget = playerHealth.Chest.position;
            typeof(SurvivorShooter).GetMethod("Shoot", Private).Invoke(enemyShooter, null);
            return $"{enemy.name} at {enemy.transform.position} shot, player hit {Time.time - playerHealth.LastHitTime:0.00} s ago, hp {playerHealth.Health:0}";
        }
        if (mode == "attack")
        {
            // Враг снова бот: стреляет в игрока (проверка стрелки урона)
            enemy.GetComponent<ShooterBot>().enabled = true;
            return $"{enemy.name} attacks, player hit {Time.time - player.GetComponent<CharacterHealth>().LastHitTime:0.00} s ago, hp {player.GetComponent<CharacterHealth>().Health:0}";
        }
        if (mode == "stop")
        {
            shooter.AimInput = false;
            foreach (var c in CharacterHealth.All)
                if (!c.IsDead && c.GetComponent<ShooterBot>() != null)
                    c.GetComponent<ShooterBot>().enabled = true;
            return "bots enabled";
        }
        return Report(shooter, enemy);
    }

    static CharacterHealth FindLivingEnemy()
    {
        foreach (var c in CharacterHealth.All)
            if (c.Team == 1 && !c.IsDead)
                return c;
        return null;
    }

    // Камера игрока смотрит на грудь или голову врага (отдача сбивает её вверх)
    static void Aim(StarterAssets.ThirdPersonController tpc, CharacterHealth enemy, string part)
    {
        var cam = Camera.main.transform;
        // Кость головы начинается у шеи, середина шара головы на 0,1 м выше
        var point = part == "head"
            ? enemy.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.Head).position + Vector3.up * 0.1f
            : enemy.Chest.position;
        var look = Quaternion.LookRotation(point - cam.position).eulerAngles;
        tpc.SetCameraYaw(look.y);
        tpc.AddCameraRotation(Mathf.DeltaAngle(0f, look.x), 0f);
    }

    static string Report(SurvivorShooter shooter, CharacterHealth enemy)
    {
        var cam = Camera.main.transform;
        string ray = "camera looks at:";
        if (Physics.Raycast(cam.position, cam.forward, out var h, 100f))
            ray += $" {h.collider.name} ({h.distance:0.0} m, root {h.collider.transform.root.name})";

        float markerStart = (float)typeof(SurvivorShooter).GetField("markerStart", Private).GetValue(shooter);
        bool head = (bool)typeof(SurvivorShooter).GetField("markerHead", Private).GetValue(shooter);
        bool kill = (bool)typeof(SurvivorShooter).GetField("markerKill", Private).GetValue(shooter);
        var feed = Object.FindFirstObjectByType<KillFeed>();
        var lines = (System.Collections.IList)typeof(KillFeed).GetField("lines", Private).GetValue(feed);
        string text = "";
        foreach (var line in lines)
            text += "\n  " + line.GetType().GetField("text").GetValue(line);
        var aimed = (CharacterHealth)typeof(SurvivorShooter).GetField("aimedCharacter", Private).GetValue(shooter);
        ray += $"\ncrosshair on: {(aimed != null ? aimed.name + " team " + aimed.Team : "nobody")}";
        return $"{ray}\nmarker {Time.time - markerStart:0.00} s ago, head {head}, kill {kill}; " +
               $"{enemy.name} hp {enemy.Health:0} dead {enemy.IsDead}; kill feed {lines.Count}:{text}";
    }
}
