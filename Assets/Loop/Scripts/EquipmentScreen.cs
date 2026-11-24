using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Окно снаряжения на базе (Canvas): I открывает и закрывает, Esc закрывает. Слева слоты с надетым, в середине
/// игрок перед камерой окна (OutfitPreview), справа части для выбранного слота и описание выбранной части с кнопками
/// «Надеть» и «Снять». Надетое сразу видно на игроке (Gear.Dress) и сохраняется в профиле; в вылазку уходит надетое.
/// Пока окно открыто, ввод игрока выключен (LoopGui.SetModal), места базы по E не открываются.
/// Canvas, иконки и связи собирает Tools/Unity/SetupEquipment.cs.
/// </summary>
public class EquipmentScreen : MonoBehaviour
{
    [SerializeField] Canvas canvas;
    [SerializeField] CanvasGroup group;
    [Tooltip("Тёмный фон окна на Canvas Screen Space - Camera: его рисует Main Camera, игрок рисуется поверх")]
    [SerializeField] Canvas backdrop;
    [SerializeField] CanvasGroup backdropGroup;
    [SerializeField] RectTransform window;
    [SerializeField] GameObject player;
    [SerializeField] WornGear worn;
    [SerializeField] OutfitPreview preview;
    [Tooltip("По порядку GearSlot")]
    [SerializeField] GearSlotView[] slots;
    [SerializeField] GearCardView cardTemplate;
    [SerializeField] UnityEngine.UI.Text listTitle;
    [SerializeField] UnityEngine.UI.Text listCount;
    [SerializeField] GameObject emptyNote;
    [SerializeField] GameObject details;
    [SerializeField] UnityEngine.UI.Image detailIcon;
    [SerializeField] UnityEngine.UI.Text detailName;
    [SerializeField] UnityEngine.UI.Text detailSet;
    [SerializeField] UnityEngine.UI.Text detailAbout;
    [SerializeField] UnityEngine.UI.Text detailRisk;
    [SerializeField] UnityEngine.UI.Button wearButton;
    [SerializeField] UnityEngine.UI.Text wearLabel;
    [SerializeField] UnityEngine.UI.Button takeOffButton;
    [SerializeField] UnityEngine.UI.Button closeButton;
    [SerializeField] UnityEngine.UI.Button fullBodyButton;
    [SerializeField] UnityEngine.UI.Text riskSummary;
    [Tooltip("Иконки частей: часть GearKind в цвете GearQuality — номер kind * Gear.QualityCount + quality; " +
             "цвета, которых у части не бывает, пустые")]
    [SerializeField] Sprite[] icons;

    static readonly Color SafeColor = new(0.55f, 0.85f, 0.8f);
    static readonly Color RiskColor = new(1f, 0.55f, 0.45f);

    readonly List<GearCardView> cards = new();
    bool isOpen;
    GearSlot slot = GearSlot.Chest;
    // Выбранная карточка: одинаковые части одного цвета — одна карточка с числом
    (GearKind kind, GearQuality quality)? selected;
    Vector2 windowPosition;

    void Awake()
    {
        for (int i = 0; i < slots.Length; i++)
        {
            var s = (GearSlot)i;
            slots[i].Button.onClick.AddListener(() => SelectSlot(s));
        }
        wearButton.onClick.AddListener(WearSelected);
        takeOffButton.onClick.AddListener(TakeOff);
        closeButton.onClick.AddListener(Close);
        fullBodyButton.onClick.AddListener(() => preview.Focus(null));
        cardTemplate.gameObject.SetActive(false);
        windowPosition = window.anchoredPosition;
        canvas.enabled = false;
        backdrop.enabled = false;
    }

    void Start()
    {
        Gear.Dress(worn, Profile.Worn());
    }

    void OnDestroy()
    {
        if (canvas.enabled && player != null)
            LoopGui.SetModal(false, player);
    }

    void Update()
    {
        var keyboard = Keyboard.current;
        var gamepad = Gamepad.current;
        bool toggle = keyboard.iKey.wasPressedThisFrame || gamepad != null && gamepad.selectButton.wasPressedThisFrame;
        if (isOpen)
        {
            if (toggle || keyboard.escapeKey.wasPressedThisFrame || gamepad != null && gamepad.buttonEast.wasPressedThisFrame)
                Close();
        }
        // Пока камера возвращается за спину, окно ещё модальное: заново оно откроется после возврата
        else if (toggle && !LoopGui.ModalOpen && !SettingsMenu.IsOpen)
            Open();

        if (!canvas.enabled)
            return;
        // Фон темнеет вместе с облётом камеры, окно проявляется во второй половине и чуть поднимается
        float t = preview.Progress;
        backdropGroup.alpha = Mathf.SmoothStep(0f, 1f, t);
        float ui = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.4f, 1f, t));
        group.alpha = ui;
        window.anchoredPosition = windowPosition + Vector2.down * (1f - ui) * 24f;
        if (!isOpen && preview.Returned)
            Finish();
    }

    void Open()
    {
        isOpen = true;
        group.alpha = 0f;
        group.interactable = true;
        backdropGroup.alpha = 0f;
        canvas.enabled = true;
        backdrop.enabled = true;
        LoopGui.SetModal(true, player);
        preview.Begin();
        SelectSlot(slot);
        // Окно открывается с персонажем во весь рост, к части тела камера едет по нажатию на слот
        preview.Focus(null);
    }

    // Камера облетает игрока обратно за спину, окно гаснет; ввод игроку вернётся, когда она встанет на место
    void Close()
    {
        if (!isOpen)
            return;
        isOpen = false;
        group.interactable = false;
        preview.Release();
    }

    void Finish()
    {
        canvas.enabled = false;
        backdrop.enabled = false;
        preview.End();
        LoopGui.SetModal(false, player);
    }

    void SelectSlot(GearSlot value)
    {
        slot = value;
        var wornPiece = Profile.WornIn(slot);
        selected = wornPiece != null ? (wornPiece.kind, wornPiece.quality) : null;
        if (selected == null)
            foreach (var card in Cards(slot))
            {
                selected = card;
                break;
            }
        preview.Focus(slot);
        Refresh();
    }

    void SelectCard(GearKind kind, GearQuality quality)
    {
        selected = (kind, quality);
        Refresh();
    }

    void WearSelected()
    {
        var (kind, quality) = selected.Value;
        if (IsWorn(kind, quality))
            return;
        Profile.Wear(Profile.Current.gear.Find(p => p.kind == kind && p.quality == quality && !p.worn));
        Gear.Dress(worn, Profile.Worn());
        Refresh();
    }

    void TakeOff()
    {
        Profile.TakeOff(slot);
        Gear.Dress(worn, Profile.Worn());
        Refresh();
    }

    static int Count(GearKind kind, GearQuality quality) =>
        Profile.Current.gear.FindAll(p => p.kind == kind && p.quality == quality).Count;

    bool IsWorn(GearKind kind, GearQuality quality)
    {
        var wornPiece = Profile.WornIn(slot);
        return wornPiece != null && wornPiece.kind == kind && wornPiece.quality == quality;
    }

    // Карточки слота: части по порядку GearKind, у каждой цвета от лучшего к худшему
    static IEnumerable<(GearKind kind, GearQuality quality)> Cards(GearSlot s)
    {
        foreach (var kind in Gear.All)
        {
            if (Gear.Slot(kind) != s)
                continue;
            for (int q = Gear.QualityCount - 1; q >= 0; q--)
                if (Count(kind, (GearQuality)q) > 0)
                    yield return (kind, (GearQuality)q);
        }
    }

    Sprite Icon(GearKind kind, GearQuality quality) => icons[(int)kind * Gear.QualityCount + (int)quality];

    // Бледная иконка пустого слота — базовая часть этого слота
    Sprite Placeholder(GearSlot s)
    {
        foreach (var kind in Gear.All)
            if (Gear.Slot(kind) == s && Gear.IsBasic(kind))
                return Icon(kind, GearQuality.Gray);
        return null;
    }

    void Refresh()
    {
        for (int i = 0; i < slots.Length; i++)
        {
            var s = (GearSlot)i;
            var piece = Profile.WornIn(s);
            slots[i].Show(s, piece, piece != null ? Icon(piece.kind, piece.quality) : null, Placeholder(s));
            slots[i].SetSelected(s == slot);
        }

        listTitle.text = Gear.SlotName(slot).ToUpper();
        var wornPiece = Profile.WornIn(slot);
        int shown = 0;
        foreach (var (kind, quality) in Cards(slot))
        {
            if (shown == cards.Count)
            {
                var card = Instantiate(cardTemplate, cardTemplate.transform.parent);
                card.Button.onClick.AddListener(() => SelectCard(card.Kind, card.Quality));
                cards.Add(card);
            }
            var view = cards[shown++];
            view.gameObject.SetActive(true);
            view.Show(kind, quality, Count(kind, quality), IsWorn(kind, quality), Icon(kind, quality));
            view.SetSelected(selected == (kind, quality));
        }
        for (int i = shown; i < cards.Count; i++)
            cards[i].gameObject.SetActive(false);
        listCount.text = shown == 0 ? "" : shown == 1 ? "1 вариант" : shown < 5 ? $"{shown} варианта" : $"{shown} вариантов";
        emptyNote.SetActive(shown == 0);

        details.SetActive(selected != null);
        if (selected != null)
        {
            var (kind, quality) = selected.Value;
            bool isWorn = IsWorn(kind, quality);
            detailIcon.sprite = Icon(kind, quality);
            detailName.text = Gear.Name(kind);
            detailSet.text = $"{Gear.QualityName(quality)}  ·  {Gear.SetName(Gear.Set(kind))}";
            detailSet.color = Gear.QualityColor(quality);
            detailAbout.text = Gear.About(kind);
            bool basic = Gear.IsFree(kind, quality);
            detailRisk.text = basic ? "Не теряется при уничтожении" : "Теряется при уничтожении на вылазке";
            detailRisk.color = basic ? SafeColor : RiskColor;
            wearButton.interactable = !isWorn;
            wearLabel.text = isWorn ? "НАДЕТО" : "НАДЕТЬ";
        }
        takeOffButton.interactable = wornPiece != null;

        var atRisk = new List<string>();
        foreach (var piece in Profile.Worn())
            if (!Gear.IsFree(piece))
                // Без цвета: с ним строка трёх частей не помещается над подсказкой, цвет и так на слотах
                atRisk.Add(Gear.Name(piece.kind));
        riskSummary.text = atRisk.Count == 0
            ? "Надет только серый базовый набор: при уничтожении он остаётся с тобой."
            : "При уничтожении на вылазке потеряешь: " + string.Join(", ", atRisk) + ".";
        riskSummary.color = atRisk.Count == 0 ? SafeColor : RiskColor;
    }
}
