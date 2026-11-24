using UnityEngine;

/// <summary>
/// Карточка части в списке окна снаряжения (EquipmentScreen): иконка, имя, комплект, цвет качества, сколько штук,
/// отметка «надето». Нажатие выбирает часть. Шаблон собирает Tools/Unity/SetupEquipment.cs, копии делает окно.
/// </summary>
public class GearCardView : MonoBehaviour
{
    [SerializeField] UnityEngine.UI.Button button;
    [SerializeField] UnityEngine.UI.Image icon;
    [SerializeField] UnityEngine.UI.Text nameLabel;
    [SerializeField] UnityEngine.UI.Text setLabel;
    [SerializeField] UnityEngine.UI.Text countLabel;
    [SerializeField] UnityEngine.UI.Image qualityBar;
    [SerializeField] UnityEngine.UI.Image selection;
    [SerializeField] GameObject wornBadge;

    public UnityEngine.UI.Button Button => button;
    public GearKind Kind { get; private set; }
    public GearQuality Quality { get; private set; }

    public void Show(GearKind kind, GearQuality quality, int count, bool worn, Sprite pieceIcon)
    {
        Kind = kind;
        Quality = quality;
        icon.sprite = pieceIcon;
        nameLabel.text = Gear.Name(kind);
        setLabel.text = $"{Gear.QualityName(quality)}  ·  {Gear.SetName(Gear.Set(kind))}";
        setLabel.color = Color.Lerp(Gear.QualityColor(quality), new Color(0.55f, 0.6f, 0.63f), 0.45f);
        countLabel.text = count > 1 ? "×" + count : "";
        qualityBar.color = Gear.QualityColor(quality);
        wornBadge.SetActive(worn);
    }

    public void SetSelected(bool value) => selection.enabled = value;
}
