using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// Окно верстака на базе (Canvas): открывает BaseHub по E у верстака, E или Esc закрывает. Слева рецепты —
/// снаряжение (GearRecipe) и припасы (Recipe), справа выбранный рецепт: что нужно и сколько есть на складе, для
/// снаряжения — шансы цвета качества от уровня его комплекта, для припасов — что получится. «Сделать» тратит
/// материалы (Crafting) и показывает результат: часть в выпавшем цвете или припасы на складе. Снаряжение в списке
/// разбито по комплектам, у заголовка — уровень комплекта; вверху справа — опыт комплекта выбранной части.
/// Пока окно открыто, ввод игрока выключен (LoopGui.SetModal). Canvas и связи собирает Tools/Unity/SetupWorkbench.cs.
/// </summary>
public class WorkbenchScreen : MonoBehaviour
{
    [SerializeField] Canvas canvas;
    [SerializeField] CanvasGroup group;
    [SerializeField] RectTransform window;
    [SerializeField] GameObject player;
    [SerializeField] Button closeButton;

    [Header("Уровень комплекта")]
    [Tooltip("Подпись, полоса и опыт: скрыты, когда выбраны припасы")]
    [SerializeField] GameObject levelBlock;
    [SerializeField] Text levelLabel;
    [SerializeField] Image levelBar;
    [SerializeField] Text levelXp;

    [Header("Список рецептов")]
    [SerializeField] Text headerTemplate;
    [SerializeField] WorkbenchCardView cardTemplate;

    [Header("Выбранный рецепт")]
    [SerializeField] Image detailIcon;
    [SerializeField] Text detailName;
    [SerializeField] Text detailInfo;
    [SerializeField] Text detailAbout;
    [SerializeField] WorkbenchCostView[] costViews;
    [SerializeField] GameObject qualitySection;
    [Tooltip("Доли шанса по порядку GearQuality")]
    [SerializeField] LayoutElement[] qualitySegments;
    [Tooltip("Шанс цвета по порядку GearQuality")]
    [SerializeField] Text[] qualityPercents;
    [SerializeField] Text masteryLabel;
    [SerializeField] Image masteryBar;
    [SerializeField] GameObject supplySection;
    [SerializeField] Text supplyYield;
    [SerializeField] Button makeButton;
    [SerializeField] Text reasonLabel;
    [SerializeField] Text stashLabel;

    [Header("Результат")]
    [SerializeField] CanvasGroup resultGroup;
    [SerializeField] RectTransform resultCard;
    [SerializeField] Image resultGlow;
    [SerializeField] Image resultIcon;
    [SerializeField] Text resultName;
    [SerializeField] Text resultQuality;
    [SerializeField] Text resultNote;
    [SerializeField] Text resultLevel;
    [SerializeField] Button resultOk;
    [SerializeField] Button resultAgain;

    [Header("Иконки")]
    [Tooltip("Части снаряжения: номер kind * Gear.QualityCount + quality, как в EquipmentScreen")]
    [SerializeField] Sprite[] gearIcons;
    [Tooltip("Припасы по порядку ItemId")]
    [SerializeField] Sprite[] supplyIcons;

    static readonly Color Accent = new Color32(0x3C, 0xCF, 0xC4, 0xFF);
    static readonly Color Risk = new(1f, 0.55f, 0.45f);
    static readonly Color Locked = new(0.55f, 0.68f, 0.85f);
    static readonly Color Muted = new Color32(0x8F, 0xA0, 0xA8, 0xFF);
    const float FadeTime = 0.18f;
    const float ResultTime = 0.3f;

    // Рецепт в списке: снаряжение или припасы
    sealed class Entry
    {
        public GearRecipe gear;
        public Recipe supply;
        public WorkbenchCardView card;
    }

    readonly List<Entry> entries = new();
    // Заголовок комплекта в списке: в нём уровень, Refresh его обновляет
    readonly Dictionary<GearSet, Text> setHeaders = new();
    Entry selected;
    bool isOpen;
    int openedFrame = -1;
    float fade;
    bool resultShown;
    float resultStart;
    Vector2 windowPosition;

    void Awake()
    {
        headerTemplate.gameObject.SetActive(false);
        cardTemplate.gameObject.SetActive(false);
        for (var set = GearSet.Basic; (int)set < Gear.SetCount; set++)
            foreach (var recipe in GearRecipe.All)
            {
                if (Gear.Set(recipe.result) != set)
                    continue;
                if (!setHeaders.ContainsKey(set))
                    setHeaders.Add(set, AddHeader(""));
                AddCard(new Entry { gear = recipe });
            }
        AddHeader("ПРИПАСЫ");
        foreach (var recipe in Recipe.All)
            AddCard(new Entry { supply = recipe });

        closeButton.onClick.AddListener(Close);
        makeButton.onClick.AddListener(Make);
        resultOk.onClick.AddListener(HideResult);
        resultAgain.onClick.AddListener(() =>
        {
            HideResult();
            Make();
        });
        windowPosition = window.anchoredPosition;
        canvas.enabled = false;
        resultGroup.gameObject.SetActive(false);
    }

    void OnDestroy()
    {
        if (canvas.enabled && player != null)
            LoopGui.SetModal(false, player);
    }

    Text AddHeader(string text)
    {
        var header = Instantiate(headerTemplate, headerTemplate.transform.parent);
        header.text = text;
        header.gameObject.SetActive(true);
        return header;
    }

    void AddCard(Entry entry)
    {
        entry.card = Instantiate(cardTemplate, cardTemplate.transform.parent);
        entry.card.gameObject.SetActive(true);
        entry.card.Button.onClick.AddListener(() => Select(entry));
        entries.Add(entry);
    }

    /// <summary>Открыть окно (BaseHub, E у верстака)</summary>
    public void Open()
    {
        isOpen = true;
        openedFrame = Time.frameCount;
        canvas.enabled = true;
        group.interactable = true;
        resultShown = false;
        resultGroup.gameObject.SetActive(false);
        LoopGui.SetModal(true, player);
        // Сначала то, что можно сделать прямо сейчас
        selected ??= entries.Find(e => CanMake(e)) ?? entries[0];
        Refresh();
    }

    void Close()
    {
        if (!isOpen)
            return;
        isOpen = false;
        group.interactable = false;
    }

    void Update()
    {
        if (!canvas.enabled)
            return;
        fade = Mathf.MoveTowards(fade, isOpen ? 1f : 0f, Time.unscaledDeltaTime / FadeTime);
        float ui = Mathf.SmoothStep(0f, 1f, fade);
        group.alpha = ui;
        window.anchoredPosition = windowPosition + Vector2.down * (1f - ui) * 24f;
        if (!isOpen && fade == 0f)
        {
            canvas.enabled = false;
            LoopGui.SetModal(false, player);
            return;
        }

        if (resultShown)
            AnimateResult();

        // Тот же E открыл окно в BaseHub: в этом кадре не закрывать
        if (!isOpen || Time.frameCount == openedFrame)
            return;
        var keyboard = Keyboard.current;
        var gamepad = Gamepad.current;
        bool back = keyboard.eKey.wasPressedThisFrame || keyboard.escapeKey.wasPressedThisFrame ||
                    gamepad != null && gamepad.buttonEast.wasPressedThisFrame;
        if (resultShown)
        {
            if (back || keyboard.enterKey.wasPressedThisFrame || keyboard.spaceKey.wasPressedThisFrame)
                HideResult();
        }
        else if (back)
            Close();
    }

    void Select(Entry entry)
    {
        selected = entry;
        Refresh();
    }

    static bool Unlocked(Entry e) => e.supply == null || Profile.IsUnlocked(e.supply);

    static bool CanMake(Entry e) => Unlocked(e) && Crafting.HasCost(e.gear != null ? e.gear.cost : e.supply.cost);

    Sprite Icon(GearKind kind, GearQuality quality) => gearIcons[(int)kind * Gear.QualityCount + (int)quality];

    void Make()
    {
        var entry = selected;
        if (!CanMake(entry))
            return;
        if (entry.gear != null)
        {
            var set = Gear.Set(entry.gear.result);
            int levelBefore = Crafting.Level(set);
            var piece = Crafting.Craft(entry.gear);
            int level = Crafting.Level(set);
            var color = Gear.QualityColor(piece.quality);
            var usual = Crafting.Usual(level);
            string levelUp = level == levelBefore ? ""
                : usual != Crafting.Usual(levelBefore) ? $"УРОВЕНЬ КОМПЛЕКТА {level}  ·  теперь обычно {Gear.QualityName(usual).ToLower()}"
                : $"УРОВЕНЬ КОМПЛЕКТА {level}  ·  {Gear.QualityName(usual + 1).ToLower()} чаще";
            ShowResult(Icon(piece.kind, piece.quality), Gear.Name(piece.kind), Gear.QualityName(piece.quality).ToUpper(), color,
                $"{Gear.SlotName(Gear.Slot(piece.kind))}  ·  надеть — в окне снаряжения [I]", color, levelUp);
        }
        else
        {
            var recipe = entry.supply;
            Crafting.Craft(recipe);
            ShowResult(supplyIcons[(int)recipe.result], Items.Name(recipe.result),
                recipe.amount > 1 ? $"×{recipe.amount}" : "ГОТОВО", Color.white,
                $"На складе теперь: {Profile.Current.stash[recipe.result]}", Items.Tint(recipe.result), "");
        }
        Refresh();
    }

    void ShowResult(Sprite icon, string itemName, string quality, Color qualityColor, string note, Color glow, string levelUp)
    {
        resultShown = true;
        resultStart = Time.unscaledTime;
        resultGroup.gameObject.SetActive(true);
        resultGroup.alpha = 0f;
        resultIcon.sprite = icon;
        resultName.text = itemName;
        resultQuality.text = quality;
        resultQuality.color = qualityColor;
        resultNote.text = note;
        resultGlow.color = new Color(glow.r, glow.g, glow.b, 0f);
        resultLevel.text = levelUp;
        resultAgain.interactable = CanMake(selected);
        // Иначе Enter или пробел нажали бы ещё и кнопку «Сделать», которая была выбрана
        UnityEngine.EventSystems.EventSystem.current.SetSelectedGameObject(null);
    }

    // Карточка результата появляется с лёгким увеличением, свечение цвета качества разгорается и чуть дышит
    void AnimateResult()
    {
        float t = Mathf.Clamp01((Time.unscaledTime - resultStart) / ResultTime);
        float ease = 1f - (1f - t) * (1f - t) * (1f - t);
        resultGroup.alpha = ease;
        resultCard.localScale = Vector3.one * Mathf.Lerp(0.86f, 1f, ease);
        var c = resultGlow.color;
        float pulse = 0.5f + 0.06f * Mathf.Sin((Time.unscaledTime - resultStart) * 2.4f);
        resultGlow.color = new Color(c.r, c.g, c.b, pulse * ease);
    }

    void HideResult()
    {
        resultShown = false;
        resultGroup.gameObject.SetActive(false);
    }

    void Refresh()
    {
        var profile = Profile.Current;
        foreach (var (set, header) in setHeaders)
            header.text = $"{Gear.SetName(set).ToUpper()}  ·  УРОВЕНЬ {Crafting.Level(set)}";

        // Вверху — комплект выбранной части: сколько до следующего уровня
        levelBlock.SetActive(selected.gear != null);
        if (selected.gear != null)
        {
            var set = Gear.Set(selected.gear.result);
            int level = Crafting.Level(set);
            levelLabel.text = $"{Gear.SetName(set).ToUpper()}  ·  УРОВЕНЬ {level}";
            if (level >= Crafting.MaxLevel)
            {
                SetFill(levelBar, 1f);
                levelXp.text = "Наивысший уровень комплекта";
            }
            else
            {
                int xp = Crafting.Xp(set);
                int from = Crafting.XpForLevel(level), to = Crafting.XpForLevel(level + 1);
                SetFill(levelBar, (xp - from) / (float)(to - from));
                int parts = to - xp;
                levelXp.text = $"До уровня {level + 1}: {parts} {Plural(parts, "часть", "части", "частей")} комплекта  ·  " +
                               $"или найди часть цветом выше обычного";
            }
        }

        foreach (var entry in entries)
        {
            bool unlocked = Unlocked(entry);
            bool can = CanMake(entry);
            string status = !unlocked ? "НУЖЕН ЧЕРТЁЖ" : can ? "МОЖНО" : "НЕ ХВАТАЕТ";
            var statusColor = !unlocked ? Locked : can ? Accent : Risk;
            if (entry.gear != null)
            {
                var kind = entry.gear.result;
                int owned = profile.gear.FindAll(p => p.kind == kind).Count;
                entry.card.Show(Icon(kind, Crafting.Usual(Crafting.Level(Gear.Set(kind)))), Gear.Name(kind),
                    $"{Gear.SlotName(Gear.Slot(kind))}  ·  " + (owned == 0 ? "у тебя нет" : $"у тебя {owned}"),
                    status, statusColor, false);
            }
            else
            {
                var recipe = entry.supply;
                entry.card.Show(supplyIcons[(int)recipe.result], Items.Name(recipe.result),
                    (recipe.amount > 1 ? $"×{recipe.amount}  ·  " : "") + $"на складе {profile.stash[recipe.result]}",
                    status, statusColor, !unlocked);
            }
            entry.card.SetSelected(entry == selected);
        }

        ShowDetails(selected);

        var stash = new System.Text.StringBuilder("На складе:  ");
        for (var id = ItemId.Wood; id <= ItemId.Tech; id++)
            stash.Append(id == ItemId.Wood ? "" : "   ·   ").Append(Items.Name(id)).Append(' ').Append(profile.stash[id]);
        stashLabel.text = stash.ToString();
    }

    void ShowDetails(Entry entry)
    {
        var cost = entry.gear != null ? entry.gear.cost : entry.supply.cost;
        for (int i = 0; i < costViews.Length; i++)
        {
            if (i < cost.Length)
                costViews[i].Show(cost[i].item, Profile.Current.stash[cost[i].item], cost[i].count);
            else
                costViews[i].gameObject.SetActive(false);
        }

        bool unlocked = Unlocked(entry);
        bool can = CanMake(entry);
        makeButton.interactable = can;
        qualitySection.SetActive(entry.gear != null);
        supplySection.SetActive(entry.supply != null);

        if (entry.gear != null)
        {
            var kind = entry.gear.result;
            var set = Gear.Set(kind);
            int level = Crafting.Level(set);
            detailIcon.sprite = Icon(kind, Crafting.Usual(level));
            detailIcon.color = Color.white;
            detailName.text = Gear.Name(kind);
            detailInfo.text = $"{Gear.SlotName(Gear.Slot(kind))}  ·  {Gear.SetName(set)}";
            detailAbout.text = Gear.About(kind);
            for (int i = 0; i < qualitySegments.Length; i++)
            {
                float chance = Crafting.Chance(kind, (GearQuality)i);
                qualitySegments[i].flexibleWidth = chance;
                // Цвет, который на этом уровне не выпадает, — прочерк приглушённым
                qualityPercents[i].text = chance > 0f ? Percent(chance) : "—";
                qualityPercents[i].color = chance > 0f ? Gear.QualityColor((GearQuality)i) : Muted;
            }
            SetFill(masteryBar, level / (float)Crafting.MaxLevel);
            masteryLabel.text = $"Уровень комплекта {level} из {Crafting.MaxLevel}";
            reasonLabel.text = can ? "Цвет выпадет случайно  ·  часть засчитается в уровень комплекта" : "Не хватает материалов: их добывают на вылазке";
        }
        else
        {
            var recipe = entry.supply;
            detailIcon.sprite = supplyIcons[(int)recipe.result];
            detailIcon.color = unlocked ? Color.white : new Color(1f, 1f, 1f, 0.35f);
            detailName.text = Items.Name(recipe.result);
            detailInfo.text = "Припасы  ·  ложатся на склад";
            detailAbout.text = recipe.about;
            supplyYield.text = $"{Items.Name(recipe.result)}{(recipe.amount > 1 ? $"  ×{recipe.amount}" : "")}";
            reasonLabel.text = !unlocked ? $"Нужен {Items.Name(recipe.blueprint.Value).ToLower()} — найди его на вылазке"
                : can ? "Опыта комплекту припасы не дают" : "Не хватает материалов: их добывают на вылазке";
        }
        reasonLabel.color = can ? Muted : unlocked ? Risk : Locked;
    }

    // Заливка полосы — правый край по доле: у скруглённой плашки заливка Image искажает углы
    static void SetFill(Image bar, float value)
    {
        var rect = bar.rectTransform;
        rect.anchorMax = new Vector2(Mathf.Clamp01(value), 1f);
        rect.offsetMax = new Vector2(0f, rect.offsetMax.y);
        // Пустая полоса: скруглённые края нулевой ширины рисовались бы точкой
        bar.enabled = value > 0.01f;
    }

    static string Plural(int n, string one, string few, string many)
    {
        int tens = n % 100, ones = n % 10;
        return tens is >= 11 and <= 14 ? many : ones == 1 ? one : ones is >= 2 and <= 4 ? few : many;
    }

    // Целые проценты, меньше 10 — с десятой: 1 % и 4 % на шкале шансов различимы
    static string Percent(float value)
    {
        float p = value * 100f;
        string s = p < 9.95f ? p.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',') : Mathf.RoundToInt(p).ToString();
        return s + " %";
    }
}
