using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Добыча на земле: выпавший рюкзак, ящик у точки захвата, чертёж. Любой живой персонаж, подошедший ближе radius,
/// забирает из неё в свой рюкзак всё, что влезет; пустая добыча исчезает.
/// Объект с кубиком собирает Spawn (RaidDirector), у кубика нет коллайдера: персонажи не спотыкаются
/// о него, пули проходят насквозь.
/// </summary>
public class Pickup : MonoBehaviour
{
    static readonly List<Pickup> all = new();
    /// <summary>Вся добыча на карте: к ней идут боты-рейдеры, над ней RaidDirector пишет подпись</summary>
    public static IReadOnlyList<Pickup> All => all;

    [SerializeField] float radius = 1.4f;

    readonly ItemCounts contents = new();
    Transform visual;
    float spin;

    public ItemCounts Contents => contents;
    /// <summary>Подпись над добычей: «рюкзак Raider 2», «ящик», «чертёж»</summary>
    public string Label { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => all.Clear();

    /// <summary>Добыча с содержимым items на земле в точке ground, кубик размера size с материалом material</summary>
    public static Pickup Spawn(Vector3 ground, ItemCounts items, string label, Material material, float size)
    {
        var root = new GameObject("Pickup " + label);
        root.transform.position = ground;
        var pickup = root.AddComponent<Pickup>();
        pickup.contents.CopyFrom(items);
        pickup.Label = label;

        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Destroy(cube.GetComponent<Collider>());
        cube.name = "Visual";
        cube.transform.SetParent(root.transform, false);
        cube.transform.localScale = Vector3.one * size;
        cube.transform.localPosition = Vector3.up * (size * 0.5f + 0.15f);
        cube.GetComponent<MeshRenderer>().sharedMaterial = material;
        pickup.visual = cube.transform;
        pickup.spin = Random.Range(0f, 360f);
        return pickup;
    }

    void OnEnable() => all.Add(this);

    void OnDisable() => all.Remove(this);

    void Update()
    {
        // Добыча медленно крутится и покачивается: так её видно среди вокселей
        spin += 40f * Time.deltaTime;
        visual.localRotation = Quaternion.Euler(0f, spin, 0f);

        var position = transform.position;
        foreach (var character in CharacterHealth.All)
        {
            if (character.IsDead)
                continue;
            var offset = character.transform.position - position;
            if (Mathf.Abs(offset.y) > 2f || offset.x * offset.x + offset.z * offset.z > radius * radius)
                continue;
            TakeBy(character.GetComponent<Backpack>());
            if (contents.IsEmpty)
            {
                Destroy(gameObject);
                return;
            }
        }
    }

    void TakeBy(Backpack backpack)
    {
        for (int i = 0; i < Items.Count; i++)
        {
            int count = contents.counts[i];
            if (count > 0)
                contents.counts[i] -= backpack.Add((ItemId)i, count);
        }
    }
}
