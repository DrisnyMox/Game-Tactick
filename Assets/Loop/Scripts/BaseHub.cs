using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

/// <summary>
/// База между вылазками: сцена Base. Персонаж ходит по острову; у места (BaseStation) по E открывается окно,
/// E или Esc закрывает. Пока окно открыто, ввод игрока выключен и курсор свободен (LoopGui.SetModal).
///  * Склад — что лежит на складе и что в рюкзаке; снаряжение перекладывается между ними. В вылазку идёт рюкзак.
///  * Торговец — продажа со склада за кредиты, покупка патронов и аптечек на склад.
///  * Верстак — рецепты Recipe.All: тратят материалы со склада, результат кладут на склад.
///    Рецепт с чертежом закрыт, пока чертёж не вынесен с вылазки.
///  * Причал — выход в вылазку (сцена Raid).
/// После вылазки сначала показывается её итог (Profile.LastRaid).
/// Сцену и места собирает Tools/Unity/SetupBase.cs.
/// </summary>
public class BaseHub : MonoBehaviour
{
    [SerializeField] GameObject player;
    [SerializeField] BaseStation[] stations;

    BaseStation near;
    BaseStation open;
    bool showResult;
    Vector2 scroll;
    float resetArmedUntil = -1f;
    GUIStyle title, text, button, small;

    void Start()
    {
        Cursor.lockState = CursorLockMode.Locked;
        showResult = Profile.LastRaid != null;
        if (showResult)
            LoopGui.SetModal(true, player);
    }

    void OnDestroy()
    {
        // Сцена выгружается с открытым окном (переход в вылазку закрывает его сам): статический флаг не должен остаться
        if (LoopGui.ModalOpen && player != null)
            LoopGui.SetModal(false, player);
    }

    void Update()
    {
        near = null;
        float best = float.MaxValue;
        foreach (var station in stations)
        {
            float d = Vector3.Distance(Flat(station.transform.position), Flat(player.transform.position));
            if (d <= station.Reach && d < best)
            {
                best = d;
                near = station;
            }
        }

        var keyboard = Keyboard.current;
        if (showResult)
        {
            if (keyboard.enterKey.wasPressedThisFrame || keyboard.eKey.wasPressedThisFrame || keyboard.escapeKey.wasPressedThisFrame)
                CloseResult();
            return;
        }
        if (open != null)
        {
            if (keyboard.eKey.wasPressedThisFrame || keyboard.escapeKey.wasPressedThisFrame)
                Close();
            return;
        }
        if (near != null && keyboard.eKey.wasPressedThisFrame && !SettingsMenu.IsOpen)
        {
            open = near;
            scroll = Vector2.zero;
            LoopGui.SetModal(true, player);
        }
    }

    void Close()
    {
        open = null;
        LoopGui.SetModal(false, player);
    }

    void CloseResult()
    {
        showResult = false;
        Profile.LastRaid = null;
        LoopGui.SetModal(false, player);
    }

    static Vector3 Flat(Vector3 v) => new(v.x, 0f, v.z);

    // ---------- Окна ----------

    void OnGUI()
    {
        if (SettingsMenu.IsOpen)
            return;
        int font = LoopGui.FontSize;
        MakeStyles(font);

        var profile = Profile.Current;
        // Ниже строки FpsCounter в левом верхнем углу
        LoopGui.Shadowed(new Rect(20, 20 + font * 1.6f, 600, font * 1.5f), $"Кредиты: {profile.credits}     Вылазок: {profile.raids}, эвакуаций: {profile.extractions}",
            LoopGui.Label(font, TextAnchor.UpperLeft), new Color(1f, 0.9f, 0.5f));

        if (showResult)
        {
            DrawResult(font);
            return;
        }
        if (open == null)
        {
            if (near != null)
                LoopGui.Shadowed(new Rect(0, Screen.height * 0.62f, Screen.width, font * 2), $"[E]  {near.Title}",
                    LoopGui.Label(Mathf.RoundToInt(font * 1.2f), TextAnchor.MiddleCenter), Color.white);
            return;
        }

        float w = Mathf.Min(Screen.width - 40, font * 34), h = Mathf.Min(Screen.height - 80, font * 26);
        var rect = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
        LoopGui.Panel(rect, 0.9f);
        GUILayout.BeginArea(new Rect(rect.x + font, rect.y + font * 0.6f, w - font * 2, h - font * 1.2f));
        GUILayout.Label(open.Title, title);
        scroll = GUILayout.BeginScrollView(scroll);
        switch (open.Kind)
        {
            case BaseStation.StationKind.Stash:
                DrawStash();
                break;
            case BaseStation.StationKind.Trader:
                DrawTrader();
                break;
            case BaseStation.StationKind.Workbench:
                DrawWorkbench();
                break;
            case BaseStation.StationKind.Exit:
                DrawExit();
                break;
        }
        GUILayout.EndScrollView();
        if (GUILayout.Button("Закрыть  [E]", button))
            Close();
        GUILayout.EndArea();
    }

    void MakeStyles(int font)
    {
        title ??= new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
        text ??= new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true };
        button ??= new GUIStyle(GUI.skin.button);
        small ??= new GUIStyle(GUI.skin.button);
        title.fontSize = Mathf.RoundToInt(font * 1.4f);
        text.fontSize = font;
        button.fontSize = font;
        small.fontSize = Mathf.RoundToInt(font * 0.85f);
    }

    void DrawResult(int font)
    {
        var result = Profile.LastRaid;
        float w = Mathf.Min(Screen.width - 40, font * 26), h = font * 12;
        var rect = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
        LoopGui.Panel(rect, 0.9f);
        GUILayout.BeginArea(new Rect(rect.x + font, rect.y + font * 0.6f, w - font * 2, h - font * 1.2f));
        GUILayout.Label(result.extracted ? "Вы вернулись с вылазки" : "Вылазка провалена", title);
        string body = result.extracted
            ? $"Вынесено: {result.items.Describe()}.\nМатериалы — на складе, снаряжение — в рюкзаке."
            : $"Персонаж уничтожен. Потеряно: {result.items.Describe()}.";
        if (!result.extracted && !result.kept.IsEmpty)
            body += $"\nЗащищённый карман сохранил: {result.kept.Describe()}.";
        if (result.unlocked.Length > 0)
            body += $"\n\n<color=#66b3ff>Открыты рецепты: {string.Join(", ", result.unlocked)}</color>";
        GUILayout.Label(body, text);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Дальше  [Enter]", button))
            CloseResult();
        GUILayout.EndArea();
    }

    void DrawStash()
    {
        var profile = Profile.Current;
        GUILayout.Label("В вылазку уходит всё, что в рюкзаке. Материалы в рюкзак не берутся: их добывают на вылазке.", text);
        GUILayout.Space(8);
        for (int i = 0; i < Items.Count; i++)
        {
            var id = (ItemId)i;
            int stored = profile.stash[id], carried = profile.carried[id];
            if (stored == 0 && carried == 0 || Items.IsBlueprint(id))
                continue;
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{Items.Name(id)}:  склад {stored}" + (Items.IsGear(id) ? $",  рюкзак {carried}" : ""), text, GUILayout.ExpandWidth(true));
            if (Items.IsGear(id))
            {
                int step = id == ItemId.Ammo ? 30 : 1;
                bool canTake = stored > 0 && !(Items.IsSingle(id) && carried > 0);
                GUI.enabled = canTake;
                if (GUILayout.Button("В рюкзак", small, GUILayout.Width(text.fontSize * 7)))
                    Move(profile.stash, profile.carried, id, step);
                GUI.enabled = carried > 0;
                if (GUILayout.Button("На склад", small, GUILayout.Width(text.fontSize * 7)))
                    Move(profile.carried, profile.stash, id, step);
                GUI.enabled = true;
            }
            GUILayout.EndHorizontal();
        }

        GUILayout.Space(12);
        var opened = new System.Text.StringBuilder();
        foreach (var recipe in Recipe.All)
            if (recipe.blueprint != null && Profile.HasBlueprint(recipe.blueprint.Value))
                opened.Append(opened.Length > 0 ? ", " : "").Append(Items.Name(recipe.result));
        GUILayout.Label("Открытые чертежи: " + (opened.Length > 0 ? opened.ToString() : "нет"), text);

        GUILayout.Space(12);
        bool armed = Time.unscaledTime < resetArmedUntil;
        if (GUILayout.Button(armed ? "Точно стереть весь прогресс? Нажми ещё раз" : "Начать заново (стереть прогресс)", small))
        {
            if (armed)
            {
                Profile.Reset();
                resetArmedUntil = -1f;
            }
            else
                resetArmedUntil = Time.unscaledTime + 3f;
        }
    }

    static void Move(ItemCounts from, ItemCounts to, ItemId id, int step)
    {
        int count = Mathf.Min(step, from[id]);
        from[id] -= count;
        to[id] += count;
        Profile.Save();
    }

    void DrawTrader()
    {
        var profile = Profile.Current;
        GUILayout.Label("<b>Продать со склада</b>", text);
        bool any = false;
        for (int i = 0; i < Items.Count; i++)
        {
            var id = (ItemId)i;
            int stored = profile.stash[id], price = Items.SellPrice(id);
            if (stored == 0 || price == 0)
                continue;
            any = true;
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{Items.Name(id)} {stored}  —  {price} кр. за штуку", text, GUILayout.ExpandWidth(true));
            if (GUILayout.Button("Продать 1", small, GUILayout.Width(text.fontSize * 7)))
                Sell(id, 1);
            if (GUILayout.Button($"Все за {stored * price}", small, GUILayout.Width(text.fontSize * 7)))
                Sell(id, stored);
            GUILayout.EndHorizontal();
        }
        if (!any)
            GUILayout.Label("На складе нечего продать.", text);

        GUILayout.Space(12);
        GUILayout.Label("<b>Купить на склад</b>", text);
        foreach (var (item, amount, price) in Trader.Offers)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{Items.Name(item)} ×{amount}", text, GUILayout.ExpandWidth(true));
            GUI.enabled = profile.credits >= price;
            if (GUILayout.Button($"{price} кр.", small, GUILayout.Width(text.fontSize * 7)))
            {
                profile.credits -= price;
                profile.stash[item] += amount;
                Profile.Save();
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }
    }

    static void Sell(ItemId id, int count)
    {
        var profile = Profile.Current;
        count = Mathf.Min(count, profile.stash[id]);
        profile.stash[id] -= count;
        profile.credits += count * Items.SellPrice(id);
        Profile.Save();
    }

    void DrawWorkbench()
    {
        var profile = Profile.Current;
        GUILayout.Label("Материалы берутся со склада, готовое ложится на склад.", text);
        GUILayout.Space(8);
        foreach (var recipe in Recipe.All)
        {
            bool unlocked = Profile.IsUnlocked(recipe);
            bool enough = true;
            var cost = new System.Text.StringBuilder();
            foreach (var (item, count) in recipe.cost)
            {
                bool has = profile.stash[item] >= count;
                enough &= has;
                cost.Append(cost.Length > 0 ? ",  " : "")
                    .Append(has ? "<color=#9be59b>" : "<color=#ff8a7a>")
                    .Append(Items.Name(item)).Append(' ').Append(profile.stash[item]).Append('/').Append(count).Append("</color>");
            }

            GUILayout.BeginHorizontal();
            string name = recipe.amount > 1 ? $"{Items.Name(recipe.result)} ×{recipe.amount}" : Items.Name(recipe.result);
            string line = unlocked ? $"<b>{name}</b>\n{cost}" : $"<b>{name}</b>\n<color=#888888>нужен {Items.Name(recipe.blueprint.Value).ToLower()} — найди его на вылазке</color>";
            GUILayout.Label(line, text, GUILayout.ExpandWidth(true));
            GUI.enabled = unlocked && enough;
            if (GUILayout.Button("Сделать", small, GUILayout.Width(text.fontSize * 7)))
            {
                foreach (var (item, count) in recipe.cost)
                    profile.stash[item] -= count;
                profile.stash[recipe.result] += recipe.amount;
                Profile.Save();
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.Space(6);
        }
    }

    void DrawExit()
    {
        var profile = Profile.Current;
        var carried = profile.carried;
        GUILayout.Label($"В рюкзаке: {carried.Describe()}.", text);
        if (carried[ItemId.Ammo] == 0)
            GUILayout.Label("<color=#ff8a7a>Патронов нет: возьми их на складе или купи у торговца.</color>", text);
        // Бесплатный набор: патронов нет ни в рюкзаке, ни на складе — без него из вылазок не выбраться
        if (Profile.CanTakeFreeKit)
        {
            GUILayout.Space(6);
            GUILayout.Label($"Можно взять бесплатный набор: {Profile.FreeKit.Describe()}. Он даётся, когда патронов нет совсем.", text);
            if (GUILayout.Button("Взять бесплатный набор", small))
                Profile.TakeFreeKit();
            GUILayout.Space(6);
        }
        GUILayout.Label("Всё, что в рюкзаке, можно потерять, кроме защищённого кармана: в нём остаётся один чертёж, " +
                        "а без чертежа — несколько штук самого дорогого материала. Эвакуация — 10 секунд в зелёном луче.", text);
        GUILayout.Space(10);
        if (GUILayout.Button("В вылазку", button, GUILayout.Height(text.fontSize * 2.4f)))
        {
            open = null;
            LoopGui.SetModal(false, player);
            SceneManager.LoadScene(SceneNames.Raid);
        }
    }
}
