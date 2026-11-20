using System;
using UnityEngine;

/// <summary>
/// Рюкзак персонажа на вылазке: материалы, снаряжение, чертежи.
/// Материалы весят по Items.Weight, их общий вес не больше capacity; снаряжение и чертежи места не занимают.
/// Выбитый пулей воксель (VoxelArenaShootable) даёт материал по своему блоку: каждые Items.VoxelsPerUnit
/// вокселей — одна штука.
/// Персонаж уничтожен — рюкзак выпадает: событие Dropped с содержимым (на вылазке RaidDirector кладёт
/// на это место мешок), сам рюкзак пустеет.
/// </summary>
[RequireComponent(typeof(CharacterHealth))]
public class Backpack : MonoBehaviour, IAmmoReserve
{
    [Tooltip("Сколько материалов влезает (по весу)")]
    [SerializeField] int capacity = 120;
    [SerializeField] float medkitHeal = 60f;
    [Tooltip("Сколько секунд после аптечки нельзя взять следующую")]
    [SerializeField] float medkitCooldown = 2f;

    readonly ItemCounts contents = new();
    // Выбитые воксели, которые ещё не набрали на штуку материала
    readonly int[] mined = new int[Items.Count];
    CharacterHealth health;
    float nextMedkit;

    /// <summary>В рюкзак легло count предметов id (для всплывающих надписей у игрока)</summary>
    public event Action<ItemId, int> Gained;

    /// <summary>Рюкзак выпал: чей, где, что в нём было. Содержимое — копия, рюкзак после события пуст</summary>
    public static event Action<Backpack, Vector3, ItemCounts> Dropped;

    public ItemCounts Contents => contents;
    public int Capacity => capacity;
    public bool IsFull => Weight >= capacity;
    public CharacterHealth Owner => health;

    public int Weight
    {
        get
        {
            int weight = 0;
            for (int i = 0; i < Items.Count; i++)
                weight += contents.counts[i] * Items.Weight((ItemId)i);
            return weight;
        }
    }

    /// <summary>Сколько материалов в рюкзаке: бот-рейдер по этому решает, что пора на эвакуацию</summary>
    public int MaterialCount
    {
        get
        {
            int count = 0;
            for (int i = 0; i <= (int)ItemId.Tech; i++)
                count += contents.counts[i];
            return count;
        }
    }

    public int Reserve => contents[ItemId.Ammo];

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => Dropped = null;

    void Awake()
    {
        health = GetComponent<CharacterHealth>();
        health.Died += Health_Died;
    }

    void OnDestroy()
    {
        health.Died -= Health_Died;
    }

    public int TakeAmmo(int count) => Remove(ItemId.Ammo, count);

    /// <summary>Положить до count штук; материалы — сколько влезет. Возвращает, сколько легло</summary>
    public int Add(ItemId id, int count)
    {
        int weight = Items.Weight(id);
        if (weight > 0)
            count = Mathf.Min(count, (capacity - Weight) / weight);
        if (count <= 0)
            return 0;
        contents[id] += count;
        Gained?.Invoke(id, count);
        return count;
    }

    public int Remove(ItemId id, int count)
    {
        count = Mathf.Min(count, contents[id]);
        contents[id] -= count;
        return count;
    }

    /// <summary>Заменить содержимое (снаряжение с базы в начале вылазки, пустой рюкзак нового рейдера)</summary>
    public void Load(ItemCounts items)
    {
        contents.CopyFrom(items);
        Array.Clear(mined, 0, mined.Length);
    }

    /// <summary>Выбит воксель блока block: копится на штуку материала</summary>
    public void Mine(byte block)
    {
        if (!Items.FromBlock(block, out var id))
            return;
        int i = (int)id;
        if (++mined[i] < Items.VoxelsPerUnit(id))
            return;
        // Рюкзак полон: воксели не копятся, материал пропал
        mined[i] = 0;
        Add(id, 1);
    }

    /// <summary>Аптечка: если ранен и аптечка есть. Возвращает, использована ли</summary>
    public bool UseMedkit()
    {
        if (health.IsDead || health.Health >= health.MaxHealth || Time.time < nextMedkit || contents[ItemId.Medkit] <= 0)
            return false;
        contents[ItemId.Medkit]--;
        nextMedkit = Time.time + medkitCooldown;
        health.Heal(medkitHeal);
        return true;
    }

    void Health_Died(CharacterHealth character)
    {
        Array.Clear(mined, 0, mined.Length);
        if (contents.IsEmpty)
            return;
        var dropped = new ItemCounts();
        dropped.CopyFrom(contents);
        contents.Clear();
        Dropped?.Invoke(this, character.Chest.position, dropped);
    }
}
