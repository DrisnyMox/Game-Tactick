using UnityEngine;

/// <summary>
/// HUD стрелка-игрока: крест прицела, отметка попадания, стрелка урона, красная вспышка при попадании,
/// патроны и здоровье. Рисует из SurvivorShooter.OnGUI
/// </summary>
public sealed class ShooterHud
{
    // Попали в игрока: экран краснеет на столько секунд
    const float HitFlashTime = 0.35f;
    // Стрелка урона гаснет за столько секунд
    const float DamageArrowTime = 1f;
    // Размеры прицела и отметки заданы для экрана высотой столько пикселей
    const float ReferenceScreenHeight = 1080f;

    readonly SurvivorShooter shooter;
    readonly CharacterHealth health;

    // Отметка попадания: когда была, в голову ли, убила ли
    float markerStart = float.NegativeInfinity;
    bool markerHead;
    bool markerKill;

    // Стили и строки HUD. OnGUI зовётся по нескольку раз за кадр, и новый GUIStyle или новая строка в каждом вызове
    // выделяли бы память. Стили заводятся в первом OnGUI игрока (GUI.skin есть только внутри OnGUI), строка патронов
    // и строка здоровья собираются заново, только когда меняется число
    GUIStyle hudStyle, arrowStyle;
    string ammoText, healthText;
    int shownAmmo = -1, shownReserve = -1, shownHealth = -1;

    public ShooterHud(SurvivorShooter shooter, CharacterHealth health)
    {
        this.shooter = shooter;
        this.health = health;
    }

    public void ShowHitMarker(bool head, bool kill)
    {
        markerStart = Time.time;
        markerHead = head;
        markerKill = kill;
    }

    public void Draw(Transform cam)
    {
        if (hudStyle == null)
        {
            hudStyle = new GUIStyle(GUI.skin.label);
            arrowStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter };
        }

        float hitFlash = 1f - (Time.time - health.LastHitTime) / HitFlashTime;
        if (hitFlash > 0f)
        {
            GUI.color = new Color(0.8f, 0f, 0f, 0.35f * hitFlash);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = Color.white;
        }

        if (shooter.AimWeight > 0.5f)
            DrawCrosshair();

        DrawHitMarker();
        DrawDamageArrow(cam);

        // Справа от магазина — запас в рюкзаке, а без запаса — размер магазина
        int ammo = shooter.Ammo;
        int reserve = shooter.Reserve == null ? shooter.MagazineSize : shooter.Reserve.Reserve;
        if (ammo != shownAmmo || reserve != shownReserve)
        {
            shownAmmo = ammo;
            shownReserve = reserve;
            ammoText = $"{ammo} / {reserve}";
        }
        int hp = Mathf.CeilToInt(health.Health);
        if (hp != shownHealth)
        {
            shownHealth = hp;
            healthText = $"здоровье {hp}";
        }

        hudStyle.fontSize = Mathf.Clamp(Screen.height / 30, 12, 32);
        hudStyle.alignment = TextAnchor.LowerRight;
        GUI.Label(new Rect(0, 0, Screen.width - 20, Screen.height - 20), shooter.Reloading ? "перезарядка..." : ammoText, hudStyle);

        hudStyle.alignment = TextAnchor.LowerLeft;
        GUI.Label(new Rect(20, 0, Screen.width, Screen.height - 20), healthText, hudStyle);
    }

    // Крест прицела: размер растёт с высотой экрана (crosshairSize задан для 1080 пикселей), тёмная обводка
    // видна на светлом песке. Под прицелом враг - красный, союзник - синий, иначе белый
    void DrawCrosshair()
    {
        var center = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        float scale = Screen.height / ReferenceScreenHeight;
        float gap = shooter.CrosshairGap * scale, len = shooter.CrosshairSize * scale, thick = Mathf.Max(2f, shooter.CrosshairThickness * scale);
        var aimed = shooter.AimedCharacter;
        var color = aimed == null ? Color.white
            : aimed.Team == health.Team ? new Color(0.45f, 0.7f, 1f) : new Color(1f, 0.25f, 0.2f);

        for (int pass = 0; pass < 2; pass++)
        {
            // Первый проход - обводка на пиксель шире со всех сторон, второй - сам крест
            float o = pass == 0 ? 1f : 0f;
            GUI.color = pass == 0 ? new Color(0f, 0f, 0f, 0.6f) : color;
            GUI.DrawTexture(new Rect(center.x - gap - len - o, center.y - thick * 0.5f - o, len + 2 * o, thick + 2 * o), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(center.x + gap - o, center.y - thick * 0.5f - o, len + 2 * o, thick + 2 * o), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(center.x - thick * 0.5f - o, center.y - gap - len - o, thick + 2 * o, len + 2 * o), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(center.x - thick * 0.5f - o, center.y + gap - o, thick + 2 * o, len + 2 * o), Texture2D.whiteTexture);
        }
        GUI.color = Color.white;
    }

    // Косой крестик вокруг прицела после попадания во врага: белый, в голову - оранжевый,
    // убийство - красный и крупнее. Гаснет за markerTime
    void DrawHitMarker()
    {
        float t = (Time.time - markerStart) / shooter.MarkerTime;
        if (t >= 1f)
            return;

        // Отметка стоит снаружи обычного прицела (у него 4 + 9 пикселей от центра), иначе с ним сливается
        var center = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        float scale = Mathf.Max(1f, Screen.height / ReferenceScreenHeight) * (markerKill ? 1.4f : 1f);
        float gap = 16f * scale, len = 14f * scale, thick = 3f * scale;
        var color = markerKill ? new Color(1f, 0.2f, 0.15f) : markerHead ? new Color(1f, 0.6f, 0.1f) : Color.white;
        color.a = 1f - t * t;

        var matrix = GUI.matrix;
        GUI.color = color;
        GUIUtility.RotateAroundPivot(45f, center);
        GUI.DrawTexture(new Rect(center.x - gap - len, center.y - thick * 0.5f, len, thick), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(center.x + gap, center.y - thick * 0.5f, len, thick), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(center.x - thick * 0.5f, center.y - gap - len, thick, len), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(center.x - thick * 0.5f, center.y + gap, thick, len), Texture2D.whiteTexture);
        GUI.matrix = matrix;
        GUI.color = Color.white;
    }

    // Красная стрелка на кольце вокруг прицела: показывает, откуда прилетела последняя пуля
    void DrawDamageArrow(Transform cam)
    {
        float t = Time.time - health.LastHitTime;
        if (t >= DamageArrowTime)
            return;

        var forward = cam.forward;
        forward.y = 0f;
        var toShooter = health.LastHitFrom - shooter.transform.position;
        toShooter.y = 0f;
        float angle = Vector3.SignedAngle(forward, toShooter, Vector3.up);

        var center = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        float size = Screen.height * 0.06f, radius = Screen.height * 0.16f;
        arrowStyle.fontSize = Mathf.RoundToInt(size);
        arrowStyle.normal.textColor = new Color(1f, 0.15f, 0.1f, 1f - t);

        var matrix = GUI.matrix;
        GUIUtility.RotateAroundPivot(angle, center);
        GUI.Label(new Rect(center.x - size, center.y - radius - size, size * 2f, size * 2f), "▲", arrowStyle);
        GUI.matrix = matrix;
    }
}
