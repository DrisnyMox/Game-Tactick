using UnityEngine;

/// <summary>
/// Слот в окне снаряжения (EquipmentScreen): иконка надетой части, название слота и части, полоса цвета качества,
/// отметка «теряется». Нажатие выбирает слот. Собирает Tools/Unity/SetupEquipment.cs.
/// </summary>
public class GearSlotView : MonoBehaviour
{
    [SerializeField] UnityEngine.UI.Button button;
    [SerializeField] UnityEngine.UI.Image icon;
    [SerializeField] UnityEngine.UI.Text slotLabel;
    [SerializeField] UnityEngine.UI.Text itemLabel;
    [SerializeField] UnityEngine.UI.Image qualityBar;
    [SerializeField] UnityEngine.UI.Image selection;
    [SerializeField] GameObject riskMark;

    public UnityEngine.UI.Button Button => button;

    static readonly Color EmptyText = new(0.5f, 0.55f, 0.58f);

    /// <summary>Показать надетое; piece null — слот пуст, placeholder — бледная иконка базовой части слота</summary>
    public void Show(GearSlot slot, GearPiece piece, Sprite pieceIcon, Sprite placeholder)
    {
        slotLabel.text = Gear.SlotName(slot).ToUpper();
        if (piece != null)
        {
            itemLabel.text = Gear.Name(piece.kind);
            itemLabel.color = Color.white;
            icon.sprite = pieceIcon;
            icon.color = Color.white;
            qualityBar.color = Gear.QualityColor(piece.quality);
            riskMark.SetActive(!Gear.IsBasic(piece.kind));
        }
        else
        {
            itemLabel.text = placeholder != null ? "Не надето" : "Пока нечего надеть";
            itemLabel.color = EmptyText;
            icon.sprite = placeholder;
            icon.color = new Color(1f, 1f, 1f, 0.18f);
            qualityBar.color = new Color(1f, 1f, 1f, 0.08f);
            riskMark.SetActive(false);
        }
        icon.enabled = icon.sprite != null;
    }

    public void SetSelected(bool value) => selection.enabled = value;
}
