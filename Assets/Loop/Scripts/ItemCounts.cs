using System;

/// <summary>Счётчики предметов по ItemId: содержимое рюкзака, склада, ящика</summary>
[Serializable]
public sealed class ItemCounts
{
    public int[] counts = new int[Items.Count];

    public int this[ItemId id]
    {
        get => counts[(int)id];
        set => counts[(int)id] = value;
    }

    public bool IsEmpty
    {
        get
        {
            foreach (int c in counts)
                if (c > 0)
                    return false;
            return true;
        }
    }

    /// <summary>Сохранение старой версии знает меньше предметов: массив дорастает до Items.Count</summary>
    public void Fit()
    {
        if (counts == null)
            counts = new int[Items.Count];
        else if (counts.Length != Items.Count)
            Array.Resize(ref counts, Items.Count);
    }

    public void Clear() => Array.Clear(counts, 0, counts.Length);

    public void CopyFrom(ItemCounts other) => Array.Copy(other.counts, counts, Items.Count);

    /// <summary>Строка вида «Дерево 12, Камень 4»; пусто — «ничего»</summary>
    public string Describe()
    {
        var text = new System.Text.StringBuilder();
        for (int i = 0; i < Items.Count; i++)
        {
            if (counts[i] <= 0)
                continue;
            if (text.Length > 0)
                text.Append(", ");
            text.Append(Items.Name((ItemId)i)).Append(' ').Append(counts[i]);
        }
        return text.Length > 0 ? text.ToString() : "ничего";
    }
}
