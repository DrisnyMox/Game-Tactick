using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// HUD вылазки: метки эвакуации с расстоянием, отсчёт в круге, подписи добычи, рюкзак и защищённый карман,
/// «шум», всплывающие «+1 Дерево», сообщения и итог вылазки. Рисует из RaidDirector.OnGUI
/// </summary>
public sealed class RaidHud
{
    struct Toast
    {
        public ItemId item;
        public int count;
        public float time;
        public string text;
    }

    const int MaxToasts = 5;
    const int MaxNotices = 4;
    // Подряд идущие «+1 Дерево» не дальше этого времени друг от друга складываются в одну строку, с
    const float ToastMergeTime = 1.5f;
    const float ToastLifetime = 2.5f;
    const float NoticeLifetime = 5f;
    // Сколько «шум» виден после последнего шума игрока, с
    const float NoiseShowTime = 1f;
    // Подписи добычи видны не дальше этого от игрока, м
    const float PickupLabelDistance = 30f;

    static readonly Color ExtractColor = new(0.45f, 1f, 0.55f);
    static readonly Color PickupColor = new(1f, 0.85f, 0.4f);
    static readonly Color PocketColor = new(0.55f, 0.85f, 1f);

    readonly CharacterHealth player;
    readonly Backpack backpack;
    readonly int pocketMaterials;
    readonly List<Toast> toasts = new();
    readonly List<(string text, float time)> notices = new();
    // Игрок шумел (выстрел или бег): HUD показывает «шум» до этого времени
    float noisyUntil;

    public RaidHud(CharacterHealth player, Backpack backpack, int pocketMaterials)
    {
        this.player = player;
        this.backpack = backpack;
        this.pocketMaterials = pocketMaterials;
    }

    public void AddToast(ItemId item, int count)
    {
        if (toasts.Count > 0 && toasts[^1].item == item && Time.time - toasts[^1].time < ToastMergeTime)
        {
            var last = toasts[^1];
            last.count += count;
            last.time = Time.time;
            last.text = $"+{last.count} {Items.Name(item)}";
            toasts[^1] = last;
            return;
        }
        toasts.Add(new Toast { item = item, count = count, time = Time.time, text = $"+{count} {Items.Name(item)}" });
        if (toasts.Count > MaxToasts)
            toasts.RemoveAt(0);
    }

    public void Notify(string text)
    {
        notices.Add((text, Time.time));
        if (notices.Count > MaxNotices)
            notices.RemoveAt(0);
    }

    public void ShowNoise() => noisyUntil = Time.time + NoiseShowTime;

    /// <summary>HUD во время вылазки</summary>
    public void Draw()
    {
        int font = LoopGui.FontSize;
        var cam = Camera.main;
        DrawExtractionMarks(cam, font);
        DrawPickupLabels(cam, font);
        DrawBackpack(font);
        DrawNoise(font);
        DrawToasts(font);
        DrawNotices(font);
    }

    void DrawExtractionMarks(Camera cam, int font)
    {
        var style = LoopGui.Label(font, TextAnchor.LowerCenter);
        foreach (var zone in ExtractionZone.All)
        {
            var position = zone.transform.position;
            float distance = RaidDirector.Flat(position - player.transform.position).magnitude;
            var screen = cam.WorldToScreenPoint(position + Vector3.up * 3f);
            if (screen.z > 0f)
                LoopGui.Shadowed(new Rect(screen.x - 150, Screen.height - screen.y - font * 2.5f, 300, font * 2.5f),
                    $"ЭВАКУАЦИЯ\n{Mathf.RoundToInt(distance)} м", style, ExtractColor);

            float held = zone.HeldBy(player);
            if (held >= 0f)
            {
                var center = LoopGui.Label(font * 2, TextAnchor.MiddleCenter);
                int left = Mathf.CeilToInt(zone.HoldTime - held);
                LoopGui.Shadowed(new Rect(0, Screen.height * 0.25f, Screen.width, font * 3), $"Эвакуация через {left}", center, ExtractColor);
            }
        }
    }

    void DrawPickupLabels(Camera cam, int font)
    {
        var style = LoopGui.Label(Mathf.RoundToInt(font * 0.8f), TextAnchor.LowerCenter);
        foreach (var pickup in Pickup.All)
        {
            var position = pickup.transform.position;
            if (Vector3.Distance(position, player.transform.position) > PickupLabelDistance)
                continue;
            var screen = cam.WorldToScreenPoint(position + Vector3.up * 1.4f);
            if (screen.z <= 0f)
                continue;
            LoopGui.Shadowed(new Rect(screen.x - 150, Screen.height - screen.y - font * 1.5f, 300, font * 1.5f), pickup.Label, style, PickupColor);
        }
    }

    void DrawBackpack(int font)
    {
        var style = LoopGui.Label(Mathf.RoundToInt(font * 0.8f), TextAnchor.LowerLeft);
        var items = backpack.Contents;
        float line = font * 1.1f;
        // Над строкой здоровья (её рисует SurvivorShooter внизу слева)
        float y = Screen.height - 20 - font * 2.2f;
        for (int i = Items.Count - 1; i >= 0; i--)
        {
            var id = (ItemId)i;
            if (items[id] <= 0 || id == ItemId.Ammo)
                continue;
            string text = id == ItemId.Medkit ? $"{Items.Name(id)} {items[id]}  [H]"
                : id == ItemId.Grenade ? $"{Items.Name(id)} {items[id]}  [G]"
                : $"{Items.Name(id)} {items[id]}";
            LoopGui.Shadowed(new Rect(20, y - line, 400, line), text, style, Items.Tint(id));
            y -= line;
        }
        var color = backpack.IsFull ? new Color(1f, 0.5f, 0.4f) : Color.white;
        string weight = backpack.IsFull ? $"рюкзак полон {backpack.Weight}/{backpack.Capacity}" : $"рюкзак {backpack.Weight}/{backpack.Capacity}";
        LoopGui.Shadowed(new Rect(20, y - line, 400, line), weight, style, color);
        y -= line;
        // Что останется, если уничтожат
        var pocket = ProtectedPocket.Of(items, pocketMaterials);
        if (!pocket.IsEmpty)
            LoopGui.Shadowed(new Rect(20, y - line, 500, line), "защищённый карман: " + pocket.Describe(), style, PocketColor);
    }

    // Игрок только что шумел (выстрел, бег): рейдеры рядом могли услышать
    void DrawNoise(int font)
    {
        if (Time.time >= noisyUntil)
            return;
        var style = LoopGui.Label(Mathf.RoundToInt(font * 0.9f), TextAnchor.MiddleCenter);
        var color = new Color(1f, 0.8f, 0.4f, Mathf.Clamp01((noisyUntil - Time.time) / 0.5f));
        LoopGui.Shadowed(new Rect(0, Screen.height * 0.7f, Screen.width, font * 1.5f), "шум", style, color);
    }

    void DrawToasts(int font)
    {
        var style = LoopGui.Label(Mathf.RoundToInt(font * 0.9f), TextAnchor.LowerRight);
        float line = font * 1.2f;
        float y = Screen.height - 20 - font * 2.2f;
        for (int i = toasts.Count - 1; i >= 0; i--)
        {
            float age = Time.time - toasts[i].time;
            if (age > ToastLifetime)
            {
                toasts.RemoveAt(i);
                continue;
            }
            var color = Items.Tint(toasts[i].item);
            color.a = Mathf.Clamp01((ToastLifetime - age) / 0.5f);
            LoopGui.Shadowed(new Rect(0, y - line, Screen.width - 20, line), toasts[i].text, style, color);
            y -= line;
        }
    }

    void DrawNotices(int font)
    {
        var style = LoopGui.Label(Mathf.RoundToInt(font * 0.8f), TextAnchor.UpperCenter);
        float y = font * 1.5f;
        for (int i = notices.Count - 1; i >= 0; i--)
        {
            if (Time.time - notices[i].time > NoticeLifetime)
            {
                notices.RemoveAt(i);
                continue;
            }
            LoopGui.Shadowed(new Rect(0, y, Screen.width, font * 1.3f), notices[i].text, style, new Color(1f, 0.75f, 0.6f));
            y += font * 1.2f;
        }
    }

    /// <summary>Итог вылазки из Profile.LastRaid; true — нажата кнопка «На базу»</summary>
    public bool DrawSummary()
    {
        int font = LoopGui.FontSize;
        var result = Profile.LastRaid;
        float w = Mathf.Min(Screen.width - 40, font * 26), h = font * 13;
        var rect = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
        LoopGui.Panel(rect, 0.85f);

        var title = LoopGui.Label(font * 2, TextAnchor.UpperCenter);
        LoopGui.Shadowed(new Rect(rect.x, rect.y + font, w, font * 3), result.extracted ? "Эвакуация удалась" : "Вы уничтожены",
            title, result.extracted ? ExtractColor : new Color(1f, 0.45f, 0.4f));

        var body = LoopGui.Label(font, TextAnchor.UpperCenter);
        body.wordWrap = true;
        string text = (result.extracted ? "Вынесено: " : "Потеряно: ") + result.items.Describe();
        if (!result.kept.IsEmpty)
            text += "\nЗащищённый карман сохранил: " + result.kept.Describe();
        if (result.unlocked.Length > 0)
            text += "\n\nОткрыты рецепты: " + string.Join(", ", result.unlocked);
        LoopGui.Shadowed(new Rect(rect.x + font, rect.y + font * 4, w - font * 2, h - font * 7), text, body, Color.white);
        body.wordWrap = false;

        return GUI.Button(new Rect(rect.x + (w - font * 10) * 0.5f, rect.yMax - font * 2.8f, font * 10, font * 2), "На базу  [Enter]");
    }
}
