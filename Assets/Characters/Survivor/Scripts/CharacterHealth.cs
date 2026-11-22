using System;
using System.Collections.Generic;
using StarterAssets;
using UnityEngine;

/// <summary>
/// Здоровье стрелка и рэгдолл.
/// Пока персонаж жив, кости рэгдолла кинематические: они ходят за анимацией и принимают пули
/// (SurvivorShooter пропускает CharacterController и попадает в коллайдеры костей). Коллайдеры костей
/// лежат на слое Hitbox: так проверка земли в ThirdPersonController не находит под ногами собственные ступни.
/// При уничтожении выключаются аниматор, CharacterController, ThirdPersonController, стрельба и бот,
/// а кости отпускаются падать. Revive возвращает всё обратно и ставит персонажа в новое место.
///
/// Взрыв гранаты (OnBlast) отбрасывает рэгдолл и уцелевшего: персонаж сбит с ног (KnockedDown), кости летят,
/// удар кости о блоки на скорости BreakSpeed выбивает их (RagdollImpact). Когда тело почти остановилось,
/// корень персонажа встаёт к тазу, а поза рэгдолла плавно переходит в стойку (RagdollGetUp).
/// Камера игрока, пока он лежит, смотрит на таз: точка PlayerCameraRoot переезжает к нему.
/// Рэгдолл и компонент ставит Tools/Unity/SetupArenaBot.cs.
/// </summary>
[RequireComponent(typeof(Animator), typeof(CharacterController))]
public class CharacterHealth : MonoBehaviour, IShootable
{
    static readonly List<CharacterHealth> all = new();
    /// <summary>Все персонажи в сцене, живые и мёртвые: среди них бот ищет врагов</summary>
    public static IReadOnlyList<CharacterHealth> All => all;

    // Параметры аниматора ThirdPersonController и SurvivorShooter: перед подъёмом персонаж стоит на месте
    static readonly int SpeedId = Animator.StringToHash("Speed");
    static readonly int GroundedId = Animator.StringToHash("Grounded");
    static readonly int JumpId = Animator.StringToHash("Jump");
    static readonly int FreeFallId = Animator.StringToHash("FreeFall");
    static readonly int AimingId = Animator.StringToHash("Aiming");

    [Tooltip("Команда: 0 - игрок и союзники, 1 - противник. Своя пуля своему урона не наносит. На вылазке RaidDirector даёт каждому рейдеру свою")]
    [SerializeField] int team;
    [SerializeField] float maxHealth = 100f;
    [Tooltip("Во сколько раз больше урон от попадания в голову")]
    [SerializeField] float headMultiplier = 2f;
    [Tooltip("Толчок кости, в которую попала смертельная пуля")]
    [SerializeField] float deathImpulse = 8f;

    [Header("Взрыв и подъём")]
    [Tooltip("Сбитый взрывом лежит не меньше стольких секунд")]
    [SerializeField] float minKnockTime = 1.5f;
    [Tooltip("И не дольше: тело застряло и дрожит — всё равно встаёт")]
    [SerializeField] float maxKnockTime = 6f;
    [Tooltip("Встаёт, когда таз и грудь медленнее этого, м/с")]
    [SerializeField] float settleSpeed = 0.5f;
    [Tooltip("Сколько секунд поза рэгдолла переходит в стойку")]
    [SerializeField] float getUpTime = 0.7f;
    [Tooltip("Кость, ударившаяся о блоки быстрее этого, м/с, выбивает их")]
    [SerializeField] float breakSpeed = 6f;
    [Tooltip("Радиус шара, который выбивает удар кости, в вокселях (на большой скорости до 1,7 раза больше)")]
    [SerializeField] float breakRadius = 1.4f;
    [Tooltip("Сколько секунд после взрыва кости пробивают блоки")]
    [SerializeField] float blastFlightTime = 2.5f;
    [Tooltip("Камера сбитого с ног игрока смотрит на точку выше таза на столько метров")]
    [SerializeField] float knockedCameraLift = 0.6f;
    [Tooltip("Сбитый взрывом упал ниже этой высоты (улетел за край карты, под ним пусто) — встаёт там, где его сбило, м")]
    [SerializeField] float fallLimitY = -20f;

    enum Pose { Standing, Knocked, GettingUp }

    Animator animator;
    CharacterController controller;
    SurvivorShooter shooter;
    Transform head;
    Transform hips;
    Rigidbody hipsBody, chestBody;
    Rigidbody[] bones;
    RagdollImpact[] impacts;
    RagdollGetUp getUp;
    // Точка PlayerCameraRoot: за ней ходит камера игрока, из неё целится бот
    Transform cameraRoot;
    Vector3 cameraRootLocal;
    // Компоненты, которые работают только у живого: ThirdPersonController, SurvivorShooter, ShooterBot, GrenadeThrower
    Behaviour[] aliveOnly;

    Pose pose;
    float knockStart;
    float settledSince;
    float getUpStart;
    Vector3 getUpCameraFrom;
    float lastBlastTime = float.NegativeInfinity;

    public int Team => team;

    /// <summary>Сменить команду: на вылазке каждый рейдер сам за себя (RaidDirector)</summary>
    public void SetTeam(int value) => team = value;
    /// <summary>Кость груди: в неё целятся боты</summary>
    public Transform Chest { get; private set; }
    public float Health { get; private set; }
    public float MaxHealth => maxHealth;
    public bool IsDead => Health <= 0f;
    /// <summary>Сбит с ног взрывом и ещё не встал: лежит рэгдоллом или поднимается</summary>
    public bool KnockedDown => pose != Pose.Standing;
    /// <summary>Время последнего попадания (Time.time): по нему экран вспыхивает красным</summary>
    public float LastHitTime { get; private set; } = float.NegativeInfinity;
    /// <summary>Где стоял стрелок при последнем попадании (у взрыва — где граната): туда показывает стрелка урона</summary>
    public Vector3 LastHitFrom { get; private set; }
    /// <summary>Кто попал последним: бот-рейдер отвечает ему огнём</summary>
    public CharacterHealth LastAttacker { get; private set; }

    // Для RagdollImpact
    internal float BreakSpeed => breakSpeed;
    internal float BreakRadius => breakRadius;

    public event Action<CharacterHealth> Died;

    /// <summary>Кто-то убит: убитый, убийца, смертельная пуля попала в голову. Для ленты убийств</summary>
    public static event Action<CharacterHealth, CharacterHealth, bool> Killed;

    public bool IsHead(Collider collider) => collider.transform == head;

    // Play Mode входит без перезагрузки домена (Enter Play Mode Settings): статика прошлого запуска
    // не обнуляется сама. Сброс до Awake сцены
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        all.Clear();
        Killed = null;
    }

    void Awake()
    {
        animator = GetComponent<Animator>();
        controller = GetComponent<CharacterController>();
        shooter = GetComponent<SurvivorShooter>();
        head = animator.GetBoneTransform(HumanBodyBones.Head);
        hips = animator.GetBoneTransform(HumanBodyBones.Hips);
        Chest = animator.GetBoneTransform(HumanBodyBones.Chest);
        cameraRoot = GetComponent<ThirdPersonController>().CinemachineCameraTarget.transform;
        cameraRootLocal = cameraRoot.localPosition;
        getUp = new RagdollGetUp(animator);
        all.Add(this);

        bones = GetComponentsInChildren<Rigidbody>(true);
        if (bones.Length == 0)
            Debug.LogError($"[CharacterHealth] {name}: на костях нет рэгдолла, пули пройдут насквозь и смерти не будет. " +
                           "Запусти Tools/Unity/SetupArenaBot.cs.", this);
        hipsBody = hips.GetComponent<Rigidbody>();
        chestBody = Chest.GetComponentInParent<Rigidbody>();

        impacts = new RagdollImpact[bones.Length];
        for (int i = 0; i < bones.Length; i++)
        {
            // Отброшенное взрывом тело летит быстро, а стены арены в один воксель (0,23 м) тонкие:
            // без непрерывной проверки кость проскакивает их за шаг физики
            bones[i].collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            impacts[i] = bones[i].gameObject.AddComponent<RagdollImpact>();
            impacts[i].Init(this);
        }
        IgnoreOwnBones();

        var behaviours = new List<Behaviour> { GetComponent<ThirdPersonController>() };
        behaviours.AddRange(GetComponents<SurvivorShooter>());
        behaviours.AddRange(GetComponents<ShooterBot>());
        behaviours.AddRange(GetComponents<GrenadeThrower>());
        aliveOnly = behaviours.ToArray();

        Health = maxHealth;
        SetRagdoll(false);
    }

    void OnDestroy()
    {
        all.Remove(this);
    }

    // Свой CharacterController не должен упираться в собственные кости
    void IgnoreOwnBones()
    {
        foreach (var bone in bones)
            foreach (var collider in bone.GetComponents<Collider>())
                Physics.IgnoreCollision(controller, collider);
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
        TakeDamage(damage * (headshot ? headMultiplier : 1f), attacker.transform.position, attacker, headshot);
        // Лежащий рэгдоллом вздрагивает от пули, как уничтоженный
        if (pose == Pose.Knocked && !IsDead && hit.rigidbody != null)
            hit.rigidbody.AddForceAtPosition(direction * deathImpulse * 0.3f, hit.point, ForceMode.Impulse);
        else if (IsDead && hit.rigidbody != null)
            hit.rigidbody.AddForceAtPosition(direction * deathImpulse, hit.point, ForceMode.Impulse);
        // След от пули на теле не нужен: он висел бы на месте, а тело ходит
        return false;
    }

    /// <summary>
    /// Взрыв в center: урон damage в центре спадает к краю radius (по расстоянию до груди), кости получают скорость
    /// до knockSpeed от центра и вверх (knockLift — насколько ниже центра точка толчка). Уцелевший сбит с ног
    /// и потом встаёт, уничтоженный падает рэгдоллом. Союзников бросившего не задевает, самого бросившего — да
    /// </summary>
    public void OnBlast(Vector3 center, float radius, float damage, float knockSpeed, float knockLift, CharacterHealth attacker)
    {
        if (attacker != this && attacker.Team == team)
            return;
        float distance = Vector3.Distance(center, Chest.position);
        if (distance >= radius)
            return;

        float falloff = 1f - distance / radius;
        if (!IsDead)
        {
            TakeDamage(damage * falloff, center, attacker, false);
            if (!IsDead)
                KnockDown();
        }
        lastBlastTime = Time.time;
        foreach (var impact in impacts)
            impact.enabled = true;
        // Всё тело получает одну скорость, по расстоянию до груди. Толчок каждой кости по её расстоянию до центра
        // давал ближним костям заметно больше скорости, и на сильном взрыве суставы рэгдолла растягивало
        var velocity = (Chest.position - (center - Vector3.up * knockLift)).normalized * (knockSpeed * falloff);
        foreach (var bone in bones)
            bone.AddForce(velocity, ForceMode.VelocityChange);
    }

    void TakeDamage(float amount, Vector3 from, CharacterHealth attacker, bool headshot)
    {
        Health -= amount;
        LastHitTime = Time.time;
        LastHitFrom = from;
        LastAttacker = attacker;
        if (Health > 0f)
            return;
        Health = 0f;
        // Уничтожен лёжа: подъёма не будет, тело остаётся рэгдоллом
        pose = Pose.Standing;
        SetRagdoll(true);
        Died?.Invoke(this);
        Killed?.Invoke(this, attacker, headshot);
    }

    // Сбит с ног: рэгдолл как у уничтоженного, но здоровье осталось. Стрельба не выключается, а ждёт подъёма
    // (SurvivorShooter сам молчит, пока KnockedDown): иначе пропал бы HUD игрока со здоровьем
    void KnockDown()
    {
        // Сбит снова, пока вставал: кости опять отпускаются (CharacterController включается только в конце подъёма)
        if (pose != Pose.Knocked)
            SetRagdoll(true, keepShooter: true);
        pose = Pose.Knocked;
        knockStart = Time.time;
        settledSince = -1f;
    }

    void Update()
    {
        if (Time.time - lastBlastTime > blastFlightTime && impacts.Length > 0 && impacts[0].enabled)
            foreach (var impact in impacts)
                impact.enabled = false;

        if (pose != Pose.Knocked)
            return;
        // Сильный взрыв выбрасывает тело за край карты, а там встать негде. Здоровье при этом осталось, поэтому
        // персонаж встаёт сразу там, где его сбило: корень, пока тело летело, стоял на месте
        if (hips.position.y < fallLimitY)
        {
            ReturnFromFall();
            return;
        }
        // Встаёт, когда тело почти остановилось и полежало так немного, но не раньше minKnockTime
        bool still = hipsBody.linearVelocity.magnitude < settleSpeed && chestBody.linearVelocity.magnitude < settleSpeed;
        if (!still)
            settledSince = -1f;
        else if (settledSince < 0f)
            settledSince = Time.time;
        float lying = Time.time - knockStart;
        if (lying >= maxKnockTime || (lying >= minKnockTime && settledSince >= 0f && Time.time - settledSince >= 0.3f))
            StartGetUp();
    }

    // Корень персонажа встаёт под таз лицом туда, куда лежит голова, кости становятся кинематическими,
    // аниматор включается в стойке. Поза рэгдолла запомнена и в LateUpdate переходит в стойку за getUpTime
    void StartGetUp()
    {
        // Земля под тазом: луч по слою Default (арена), как RaidDirector.Ground. Нет земли — тело за краем карты
        // и ещё падает (maxKnockTime вышло раньше fallLimitY): встать негде
        if (!Physics.Raycast(hips.position + Vector3.up * 0.3f, Vector3.down, out var ground, 3f, 1, QueryTriggerInteraction.Ignore))
        {
            ReturnFromFall();
            return;
        }
        var feet = ground.point;
        getUp.Capture();
        getUpCameraFrom = cameraRoot.position;

        var along = head.position - hips.position;
        along.y = 0f;
        var facing = along.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(along) : Quaternion.Euler(0f, transform.eulerAngles.y, 0f);

        foreach (var bone in bones)
            bone.isKinematic = true;
        transform.SetPositionAndRotation(feet, facing);
        StandAnimator();

        pose = Pose.GettingUp;
        getUpStart = Time.time;
    }

    // Аниматор снова ведёт кости, персонаж стоит на месте
    void StandAnimator()
    {
        animator.enabled = true;
        animator.SetFloat(SpeedId, 0f);
        animator.SetBool(GroundedId, true);
        animator.SetBool(JumpId, false);
        animator.SetBool(FreeFallId, false);
        animator.SetBool(AimingId, false);
    }

    void LateUpdate()
    {
        if (pose == Pose.Knocked)
        {
            cameraRoot.position = hips.position + Vector3.up * knockedCameraLift;
            return;
        }
        if (pose != Pose.GettingUp)
            return;

        float t = Mathf.Clamp01((Time.time - getUpStart) / getUpTime);
        float smooth = t * t * (3f - 2f * t);
        getUp.Apply(smooth);
        cameraRoot.position = Vector3.Lerp(getUpCameraFrom, transform.TransformPoint(cameraRootLocal), smooth);
        if (t >= 1f)
            FinishGetUp();
    }

    // Без плавного перехода: кости далеко внизу, переход тянул бы тело из пустоты. Кости сразу встают по аниматору
    void ReturnFromFall()
    {
        foreach (var bone in bones)
            bone.isKinematic = true;
        StandAnimator();
        FinishGetUp();
    }

    void FinishGetUp()
    {
        pose = Pose.Standing;
        cameraRoot.localPosition = cameraRootLocal;
        controller.enabled = true;
        IgnoreOwnBones();
        foreach (var behaviour in aliveOnly)
            behaviour.enabled = true;
    }

    /// <summary>Снова живой, полное здоровье, стоит в position лицом в rotation</summary>
    public void Revive(Vector3 position, Quaternion rotation)
    {
        // Рейдер, ушедший с карты (Withdraw), возвращается новым рейдером
        gameObject.SetActive(true);
        pose = Pose.Standing;
        cameraRoot.localPosition = cameraRootLocal;
        SetRagdoll(false);
        // CharacterController перезаписывает положение, пока включён
        controller.enabled = false;
        transform.SetPositionAndRotation(position, rotation);
        controller.enabled = true;
        // Камера смотрит туда же, куда персонаж (у бота камеры нет, поворачивается только точка PlayerCameraRoot)
        GetComponent<ThirdPersonController>().SetCameraYaw(rotation.eulerAngles.y);
        Health = maxHealth;
        LastHitTime = float.NegativeInfinity;
        LastAttacker = null;
        shooter.Refill();
    }

    /// <summary>Прибавить здоровья, не больше максимума</summary>
    public void Heal(float amount)
    {
        if (!IsDead)
            Health = Mathf.Min(maxHealth, Health + amount);
    }

    /// <summary>Новый максимум здоровья (бронежилет), здоровье полное</summary>
    public void SetMaxHealth(float value)
    {
        maxHealth = value;
        Health = value;
    }

    /// <summary>
    /// Ушёл с карты (рейдер эвакуировался): объект выключен, для ботов он как уничтоженный, но без рэгдолла,
    /// событий Died и Killed. Вернуть — Revive
    /// </summary>
    public void Withdraw()
    {
        Health = 0f;
        gameObject.SetActive(false);
    }

    // keepShooter — сбит с ног: SurvivorShooter остаётся включённым ради HUD игрока
    void SetRagdoll(bool on, bool keepShooter = false)
    {
        foreach (var bone in bones)
            bone.isKinematic = !on;
        animator.enabled = !on;
        controller.enabled = !on;
        foreach (var behaviour in aliveOnly)
            behaviour.enabled = !on || (keepShooter && behaviour is SurvivorShooter);
    }
}
