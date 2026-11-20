using UnityEngine;

/// <summary>Защищённый карман игрока: что из рюкзака не выпадает, если персонажа уничтожат</summary>
public static class ProtectedPocket
{
    /// <summary>Один чертёж, а без чертежа — до materials штук самого дорогого материала</summary>
    public static ItemCounts Of(ItemCounts items, int materials)
    {
        var pocket = new ItemCounts();
        for (int i = 0; i < Items.Count; i++)
        {
            var id = (ItemId)i;
            if (Items.IsBlueprint(id) && items[id] > 0)
            {
                pocket[id] = 1;
                return pocket;
            }
        }
        ItemId best = default;
        int bestPrice = 0;
        for (int i = 0; i < Items.Count; i++)
        {
            var id = (ItemId)i;
            if (Items.IsMaterial(id) && items[id] > 0 && Items.SellPrice(id) > bestPrice)
            {
                best = id;
                bestPrice = Items.SellPrice(id);
            }
        }
        if (bestPrice > 0)
            pocket[best] = Mathf.Min(materials, items[best]);
        return pocket;
    }
}
