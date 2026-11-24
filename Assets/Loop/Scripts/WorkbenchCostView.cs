using UnityEngine;

/// <summary>
/// Строка цены рецепта в окне верстака (WorkbenchScreen): цвет материала, имя, сколько на складе и сколько нужно.
/// Не хватает — число красное. Собирает Tools/Unity/SetupWorkbench.cs.
/// </summary>
public class WorkbenchCostView : MonoBehaviour
{
    [SerializeField] UnityEngine.UI.Image dot;
    [SerializeField] UnityEngine.UI.Text nameLabel;
    [SerializeField] UnityEngine.UI.Text amountLabel;

    static readonly Color Enough = new(0.6f, 0.9f, 0.62f);
    static readonly Color Short = new(1f, 0.55f, 0.45f);

    public void Show(ItemId item, int have, int need)
    {
        gameObject.SetActive(true);
        dot.color = Items.Tint(item);
        nameLabel.text = Items.Name(item);
        amountLabel.text = $"{have} / {need}";
        amountLabel.color = have >= need ? Enough : Short;
    }
}
