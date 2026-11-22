using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Бросок гранаты. Игрок: G или правый бампер (действие Grenade в StarterAssets.inputactions, PlayerInput присылает
/// сообщение OnGrenade), граната летит туда, куда смотрит камера, с подъёмом throwLift.
/// Бот (ShooterBot) зовёт ThrowAt: скорость считается так, чтобы граната упала в точку.
/// Запас — гранаты в рюкзаке Supply (на вылазке ставит RaidDirector); нет рюкзака — гранаты без счёта (бой на арене).
/// Анимации броска нет: граната вылетает от груди.
/// Компонент и ссылку на префаб ставит Tools/Unity/SetupGrenade.cs.
/// </summary>
[RequireComponent(typeof(CharacterHealth), typeof(SurvivorShooter))]
public class GrenadeThrower : MonoBehaviour
{
    [SerializeField] Grenade grenadePrefab;
    [Tooltip("Скорость гранаты при броске, м/с")]
    [SerializeField] float throwSpeed = 16f;
    [Tooltip("Игрок: к броску по камере добавляется столько м/с вверх, чтобы граната летела дугой")]
    [SerializeField] float throwLift = 4f;
    [Tooltip("Сколько секунд после броска нельзя бросить следующую")]
    [SerializeField] float cooldown = 1f;
    [Tooltip("Граната вылетает впереди груди на столько метров")]
    [SerializeField] float forwardOffset = 0.5f;

    CharacterHealth health;
    SurvivorShooter shooter;
    Collider[] ownColliders;
    Transform cam;
    float nextThrow;

    /// <summary>Нажат бросок (игрок); Update сбрасывает</summary>
    public bool ThrowInput { get; set; }
    /// <summary>Откуда берутся гранаты. null — без счёта</summary>
    public Backpack Supply { get; set; }
    /// <summary>Радиус, в котором граната задевает персонажей: бот не бросает её под ноги союзникам</summary>
    public float BlastRadius => grenadePrefab.BlastRadius;
    public float ThrowSpeed => throwSpeed;

    public bool CanThrow => !health.IsDead && !health.KnockedDown && Time.time >= nextThrow &&
                            (Supply == null || Supply.Contents[ItemId.Grenade] > 0);

    void Awake()
    {
        health = GetComponent<CharacterHealth>();
        shooter = GetComponent<SurvivorShooter>();
        ownColliders = GetComponentsInChildren<Collider>(true);
    }

    void Start()
    {
        cam = Camera.main.transform;
    }

    // PlayerInput шлёт сообщение и выключенному компоненту: нажатие, пока персонаж лежит, не копится до подъёма
    void OnGrenade(InputValue value)
    {
        if (value.isPressed && enabled)
            ThrowInput = true;
    }

    void Update()
    {
        if (!ThrowInput)
            return;
        ThrowInput = false;
        if (shooter.BotControlled || !CanThrow)
            return;
        Throw(cam.forward * throwSpeed + Vector3.up * throwLift);
    }

    /// <summary>
    /// Бросить в точку target. high — навесом (через укрытие), иначе низкой дугой. Точка дальше, чем долетает
    /// граната, — бросок под 45°, граната падает с недолётом. Возвращает, брошена ли
    /// </summary>
    public bool ThrowAt(Vector3 target, bool high)
    {
        if (!CanThrow)
            return false;
        var delta = target - ThrowOrigin();
        var flat = new Vector3(delta.x, 0f, delta.z);
        float x = Mathf.Max(flat.magnitude, 0.1f);
        float g = -Physics.gravity.y;
        float v = throwSpeed, v2 = v * v;
        // Угол броска по дальности x и высоте delta.y: из уравнения полёта тела без сопротивления воздуха
        float discriminant = v2 * v2 - g * (g * x * x + 2f * delta.y * v2);
        float angle = discriminant < 0f
            ? 45f * Mathf.Deg2Rad
            : Mathf.Atan((v2 + (high ? 1f : -1f) * Mathf.Sqrt(discriminant)) / (g * x));
        var direction = flat / x;
        Throw(direction * (Mathf.Cos(angle) * v) + Vector3.up * (Mathf.Sin(angle) * v));
        return true;
    }

    Vector3 ThrowOrigin() => health.Chest.position + transform.forward * forwardOffset + Vector3.up * 0.2f;

    void Throw(Vector3 velocity)
    {
        nextThrow = Time.time + cooldown;
        if (Supply != null)
            Supply.Remove(ItemId.Grenade, 1);
        var grenade = Instantiate(grenadePrefab, ThrowOrigin(), Random.rotation);
        grenade.Launch(health, velocity, ownColliders);
    }
}
