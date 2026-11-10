using System;
using System.Collections.Generic;
using StarterAssets;
using UnityEngine;

/// <summary>
/// Здоровье стрелка и рэгдолл.
/// Пока персонаж жив, кости рэгдолла кинематические: они ходят за анимацией и принимают пули
/// (SurvivorShooter пропускает CharacterController и попадает в коллайдеры костей). Коллайдеры костей
/// лежат на слое Hitbox: так проверка земли в ThirdPersonController не находит под ногами собственные ступни.
/// При смерти выключаются аниматор, CharacterController, ThirdPersonController, стрельба и бот,
/// а кости отпускаются падать. Revive возвращает всё обратно и ставит персонажа в новое место.
/// Рэгдолл и компонент ставит Tools/Unity/SetupArenaBot.cs.
/// </summary>
[RequireComponent(typeof(Animator), typeof(CharacterController))]
public class CharacterHealth : MonoBehaviour, IShootable
{
    static readonly List<CharacterHealth> all = new();
    /// <summary>Все персонажи в сцене, живые и мёртвые: среди них бот ищет врагов</summary>
    public static IReadOnlyList<CharacterHealth> All => all;

    [Tooltip("Команда: 0 - игрок и союзники, 1 - противник. Своя пуля своему урона не наносит")]
    [SerializeField] int team;
    [SerializeField] float maxHealth = 100f;
    [Tooltip("Во сколько раз больше урон от попадания в голову")]
    [SerializeField] float headMultiplier = 2f;
    [Tooltip("Толчок кости, в которую попала смертельная пуля")]
    [SerializeField] float deathImpulse = 8f;

    Animator animator;
    CharacterController controller;
    SurvivorShooter shooter;
    Transform head;
    Rigidbody[] bones;
    // Компоненты, которые работают только у живого: ThirdPersonController, SurvivorShooter, ShooterBot
    Behaviour[] aliveOnly;

    public int Team => team;
    /// <summary>Кость груди: в неё целятся боты</summary>
    public Transform Chest { get; private set; }
    public float Health { get; private set; }
    public float MaxHealth => maxHealth;
    public bool IsDead => Health <= 0f;
    /// <summary>Время последнего попадания (Time.time): по нему экран вспыхивает красным</summary>
    public float LastHitTime { get; private set; } = float.NegativeInfinity;
    /// <summary>Где стоял стрелок при последнем попадании: туда показывает стрелка урона</summary>
    public Vector3 LastHitFrom { get; private set; }

    public event Action<CharacterHealth> Died;

    /// <summary>Кто-то убит: убитый, убийца, смертельная пуля попала в голову. Для ленты убийств</summary>
    public static event Action<CharacterHealth, CharacterHealth, bool> Killed;

    public bool IsHead(Collider collider) => collider.transform == head;

    void Awake()
    {
        animator = GetComponent<Animator>();
        controller = GetComponent<CharacterController>();
        shooter = GetComponent<SurvivorShooter>();
        head = animator.GetBoneTransform(HumanBodyBones.Head);
        Chest = animator.GetBoneTransform(HumanBodyBones.Chest);
        all.Add(this);

        bones = GetComponentsInChildren<Rigidbody>(true);
        if (bones.Length == 0)
            Debug.LogError($"[CharacterHealth] {name}: на костях нет рэгдолла, пули пройдут насквозь и смерти не будет. " +
                           "Запусти Tools/Unity/SetupArenaBot.cs.", this);

        // Свой CharacterController не должен упираться в собственные кости
        foreach (var bone in bones)
            foreach (var collider in bone.GetComponents<Collider>())
                Physics.IgnoreCollision(controller, collider);

        var behaviours = new System.Collections.Generic.List<Behaviour> { GetComponent<ThirdPersonController>() };
        behaviours.AddRange(GetComponents<SurvivorShooter>());
        behaviours.AddRange(GetComponents<ShooterBot>());
        aliveOnly = behaviours.ToArray();

        Health = maxHealth;
        SetRagdoll(false);
    }

    void OnDestroy()
    {
        all.Remove(this);
    }

    public HitSurface SurfaceAt(RaycastHit hit) => HitSurface.Body;

    public bool OnShot(RaycastHit hit, Vector3 direction, float damage, CharacterHealth attacker)
    {
        if (attacker.Team == team)
            return false;
        if (IsDead)
        {
            if (hit.rigidbody != null)
                hit.rigidbody.AddForceAtPosition(direction * deathImpulse * 0.3f, hit.point, ForceMode.Impulse);
            return false;
        }

        bool headshot = IsHead(hit.collider);
        Health -= damage * (headshot ? headMultiplier : 1f);
        LastHitTime = Time.time;
        LastHitFrom = attacker.transform.position;
        if (Health <= 0f)
        {
            Health = 0f;
            SetRagdoll(true);
            if (hit.rigidbody != null)
                hit.rigidbody.AddForceAtPosition(direction * deathImpulse, hit.point, ForceMode.Impulse);
            Died?.Invoke(this);
            Killed?.Invoke(this, attacker, headshot);
        }
        // След от пули на теле не нужен: он висел бы на месте, а тело ходит
        return false;
    }

    /// <summary>Снова живой, полное здоровье, стоит в position лицом в rotation</summary>
    public void Revive(Vector3 position, Quaternion rotation)
    {
        SetRagdoll(false);
        // CharacterController перезаписывает положение, пока включён
        controller.enabled = false;
        transform.SetPositionAndRotation(position, rotation);
        controller.enabled = true;
        // Камера смотрит туда же, куда персонаж (у бота камеры нет, поворачивается только точка PlayerCameraRoot)
        GetComponent<ThirdPersonController>().SetCameraYaw(rotation.eulerAngles.y);
        Health = maxHealth;
        LastHitTime = float.NegativeInfinity;
        shooter.Refill();
    }

    void SetRagdoll(bool on)
    {
        foreach (var bone in bones)
            bone.isKinematic = !on;
        animator.enabled = !on;
        controller.enabled = !on;
        foreach (var behaviour in aliveOnly)
            behaviour.enabled = !on;
    }
}
