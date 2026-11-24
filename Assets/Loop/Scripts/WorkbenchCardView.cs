using UnityEngine;

/// <summary>
/// Карточка рецепта в списке окна верстака (WorkbenchScreen): иконка, имя, строка под ним (слот и опыт части или
/// сколько получится), отметка «можно сделать», «не хватает» или «нужен чертёж». Нажатие выбирает рецепт.
/// Шаблон собирает Tools/Unity/SetupWorkbench.cs, копии делает окно.
/// </summary>
public class WorkbenchCardView : MonoBehaviour
{
    [SerializeField] UnityEngine.UI.Button button;
    [SerializeField] UnityEngine.UI.Image icon;
    [SerializeField] UnityEngine.UI.Text nameLabel;
    [SerializeField] UnityEngine.UI.Text infoLabel;
    [SerializeField] UnityEngine.UI.Image statusBack;
    [SerializeField] UnityEngine.UI.Text statusLabel;
    [SerializeField] UnityEngine.UI.Image selection;

    public UnityEngine.UI.Button Button => button;

    public void Show(Sprite pieceIcon, string itemName, string info, string status, Color statusColor, bool dim)
    {
        icon.sprite = pieceIcon;
        icon.color = dim ? new Color(1f, 1f, 1f, 0.35f) : Color.white;
        nameLabel.text = itemName;
        nameLabel.color = dim ? new Color(0.62f, 0.67f, 0.7f) : Color.white;
        infoLabel.text = info;
        statusLabel.text = status;
        statusLabel.color = statusColor;
        statusBack.color = new Color(statusColor.r, statusColor.g, statusColor.b, 0.16f);
    }

    public void SetSelected(bool value) => selection.enabled = value;
}
