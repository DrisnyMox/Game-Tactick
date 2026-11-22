using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Точка эвакуации: круг радиуса radius. Кто простоял в нём holdTime секунд подряд, тот эвакуирован
/// (событие Extracted); вышел из круга — отсчёт заново. Игрока точка выпускает всегда, бота-рейдера —
/// только когда он сам хочет уйти (ShooterBot.WantsExtract), чтобы рейдер, пробегая мимо, не исчез.
/// Объект с лучом и кольцом собирает Spawn (RaidDirector).
/// </summary>
public class ExtractionZone : MonoBehaviour
{
    static readonly List<ExtractionZone> all = new();
    public static IReadOnlyList<ExtractionZone> All => all;

    [SerializeField] float radius = 4f;
    [SerializeField] float holdTime = 10f;

    // Кто стоит в круге и с какого времени
    readonly Dictionary<CharacterHealth, float> inside = new();
    readonly List<CharacterHealth> left = new();
    readonly Dictionary<CharacterHealth, ShooterBot> bots = new();

    /// <summary>Кто-то эвакуирован через эту точку</summary>
    public static event Action<ExtractionZone, CharacterHealth> Extracted;

    public float Radius => radius;
    public float HoldTime => holdTime;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        all.Clear();
        Extracted = null;
    }

    /// <summary>Точка эвакуации на земле в ground: высокий луч и кольцо на земле</summary>
    public static ExtractionZone Spawn(Vector3 ground, string name, Material beam, Material ring)
    {
        var root = new GameObject(name);
        root.transform.position = ground;
        var zone = root.AddComponent<ExtractionZone>();
        float d = zone.radius * 2f;
        Part(root.transform, beam, new Vector3(0.6f, 30f, 0.6f), 30f);
        Part(root.transform, ring, new Vector3(d, 0.05f, d), 0.05f);
        return zone;
    }

    static void Part(Transform parent, Material material, Vector3 scale, float y)
    {
        // Цилиндр Unity высотой 2: центр на половине высоты
        var part = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        Destroy(part.GetComponent<Collider>());
        part.transform.SetParent(parent, false);
        part.transform.localScale = scale;
        part.transform.localPosition = Vector3.up * y;
        var renderer = part.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
    }

    void OnEnable() => all.Add(this);

    void OnDisable() => all.Remove(this);

    /// <summary>Сколько секунд character уже стоит в круге; -1 — не стоит</summary>
    public float HeldBy(CharacterHealth character) =>
        inside.TryGetValue(character, out float since) ? Time.time - since : -1f;

    void Update()
    {
        var center = transform.position;
        left.Clear();
        foreach (var character in CharacterHealth.All)
        {
            var offset = character.transform.position - center;
            // Сбитый взрывом лежит не там, где его корень: отсчёт заново, когда встанет
            bool there = !character.IsDead && !character.KnockedDown && Mathf.Abs(offset.y) < 4f &&
                         offset.x * offset.x + offset.z * offset.z <= radius * radius && Wants(character);
            if (!there)
            {
                inside.Remove(character);
                continue;
            }
            if (!inside.TryGetValue(character, out float since))
                inside[character] = Time.time;
            else if (Time.time - since >= holdTime)
                left.Add(character);
        }

        foreach (var character in left)
        {
            inside.Remove(character);
            Extracted?.Invoke(this, character);
        }
    }

    bool Wants(CharacterHealth character)
    {
        if (!bots.TryGetValue(character, out var bot))
        {
            bot = character.GetComponent<ShooterBot>();
            bots[character] = bot;
        }
        // У игрока бота нет или он выключен
        return bot == null || !bot.enabled || bot.WantsExtract;
    }
}
