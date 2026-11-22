using StarterAssets;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Бот-стрелок: ведёт того же персонажа, что и игрок, вместо мыши и клавиатуры.
/// Ввод кладёт туда же, куда его кладёт PlayerInput: движение и прыжок - в StarterAssetsInputs
/// (их читает ThirdPersonController), прицел, огонь и точку прицела - в SurvivorShooter.
///
/// Поведение:
///  * цель - ближайший видимый враг (CharacterHealth другой команды), а если никого не видно - прежняя цель
///    или ближайший враг; выбор обновляется раз в targetCheckInterval;
///  * не видит цель - идёт по NavMesh арены к тому месту, где видел её последний раз, бегом;
///    не видит дольше chaseMemory - идёт прямо к ней;
///  * видит и цель дальше keepDistance - идёт к ней с поднятой винтовкой и стреляет;
///  * видит и цель ближе - стоит и шагает вбок, меняя сторону; совсем близко - отходит;
///  * стреляет очередями после задержки реакции, прицел плавно гуляет вокруг груди цели на spreadDegrees;
///  * упёрся в уступ (хочет идти, а стоит на месте) - прыгает;
///  * расходится с союзниками ближе separation, чтобы команда не бежала одной толпой;
///  * рейдер слышит шум врага (Noise: выстрел, бег, взрыв) и идёт проверить место, осматриваясь investigateTime;
///  * раз в grenadeInterval бросает гранату (GrenadeThrower) в цель на средней дистанции, а в спрятавшуюся —
///    навесом туда, где видел её последний раз.
/// Компонент и ссылки ставит Tools/Unity/SetupArenaBot.cs.
/// </summary>
// Раньше ThirdPersonController: ввод этого кадра должен быть готов к его Update
[DefaultExecutionOrder(-50)]
[RequireComponent(typeof(SurvivorShooter), typeof(StarterAssetsInputs), typeof(CharacterHealth))]
public class ShooterBot : MonoBehaviour
{
    [Tooltip("NavMesh арены: по нему бот ищет путь")]
    [SerializeField] ArenaNavMesh navMesh;

    [Header("Бой")]
    [SerializeField] float targetCheckInterval = 0.3f;
    [Tooltip("Сколько секунд бот идёт туда, где видел цель последний раз, прежде чем идти прямо к ней")]
    [SerializeField] float chaseMemory = 4f;
    [SerializeField] float viewDistance = 70f;
    [SerializeField] float fireDistance = 55f;
    [Tooltip("Ближе этого бот к цели не подходит, а шагает вбок")]
    [SerializeField] float keepDistance = 14f;
    [Tooltip("Сколько секунд бот видит цель, прежде чем начать стрелять")]
    [SerializeField] float reactionTime = 0.6f;
    [Tooltip("Насколько гуляет прицел вокруг груди цели, градусы")]
    [SerializeField] float spreadDegrees = 2.5f;
    [SerializeField] Vector2 burstTime = new(0.3f, 0.8f);
    [SerializeField] Vector2 burstPause = new(0.4f, 1.2f);
    [Tooltip("Сколько секунд бот держит винтовку у плеча после того, как потерял цель из виду")]
    [SerializeField] float aimMemory = 1.5f;

    [Header("Движение")]
    [SerializeField] float repathInterval = 0.5f;
    [Tooltip("Вбок бот шагает столько секунд, потом меняет сторону")]
    [SerializeField] Vector2 strafeTime = new(1.2f, 2.5f);
    [Tooltip("От союзников ближе этого, м, бот отходит")]
    [SerializeField] float separation = 2.5f;

    [Header("Рейдер (вылазка)")]
    [Tooltip("Рейдер: добывает ресурсы, подбирает мешки и ящики, уходит через точку эвакуации. " +
             "В бой вступает, только когда видит врага или в него попали. Нужен RaidDirector в сцене")]
    [SerializeField] bool raider;
    [Tooltip("С какого расстояния рейдер стреляет по дереву, камню или руде, м")]
    [SerializeField] float gatherDistance = 6f;
    [Tooltip("Сколько секунд рейдер добывает в одном месте, прежде чем искать другое")]
    [SerializeField] float gatherSpotTime = 25f;
    [Tooltip("Набрал столько материалов — идёт на эвакуацию")]
    [SerializeField] int extractAtMaterials = 40;
    [Tooltip("Через столько секунд на карте идёт на эвакуацию, даже если набрал мало")]
    [SerializeField] float extractAfter = 300f;
    [Tooltip("С какого расстояния рейдер замечает мешок или ящик, м")]
    [SerializeField] float pickupNoticeDistance = 30f;
    [Tooltip("Сколько секунд рейдер отвечает огнём тому, кто в него попал, даже не видя его")]
    [SerializeField] float grudgeTime = 8f;
    [Tooltip("Сколько секунд рейдер проверяет место, где услышал врага (Noise): идёт туда и осматривается")]
    [SerializeField] float investigateTime = 12f;

    [Header("Гранаты")]
    [Tooltip("Ближе этого к цели граната не летит: взрыв задел бы самого бота, м")]
    [SerializeField] float grenadeMinDistance = 10f;
    [Tooltip("Дальше этого граната не долетает (GrenadeThrower.throwSpeed 16 м/с — до 26 м по ровному), м")]
    [SerializeField] float grenadeMaxDistance = 24f;
    [Tooltip("Пауза между бросками, с. Первый бросок после выхода на карту — не раньше половины паузы")]
    [SerializeField] Vector2 grenadeInterval = new(12f, 25f);
    [Tooltip("Бросает в цель, которую видел не позже стольких секунд назад: туда, где видел")]
    [SerializeField] float grenadeMemory = 3f;
    [Tooltip("Насколько граната ложится мимо точки, м")]
    [SerializeField] float grenadeScatter = 1.5f;

    SurvivorShooter shooter;
    GrenadeThrower thrower;
    StarterAssetsInputs input;
    CharacterController controller;
    CharacterHealth self;
    Collider[] ownColliders;
    Transform cam;

    // Текущая цель: null, если живых врагов нет
    CharacterHealth target;
    float nextTargetCheck;

    // Unity не даёт создавать NavMeshPath в инициализаторе поля, только в Awake
    NavMeshPath path;
    bool hasPath;
    int corner;
    float nextRepath;

    float seenSince = -1f;
    float lastSeenTime = float.NegativeInfinity;
    Vector3 lastSeenPosition;
    float burstEnd;
    float nextBurst;
    float strafeSign = 1f;
    float nextStrafeFlip;
    float stuckTime;
    float noiseSeed;
    float nextGrenade;

    // Рейдер
    Backpack backpack;
    float raidStart;
    int gatherSpot = -1;
    float gatherSpotUntil;
    Vector3 gatherPoint;
    float nextGatherCheck;
    float gatherBurstEnd, gatherPause;
    // Где рейдер последний раз услышал врага и до какого времени проверяет это место
    Vector3 heardPosition;
    float heardUntil = float.NegativeInfinity;

    public bool Raider => raider;

    /// <summary>Рейдер набрал добычи или пробыл на карте долго: идёт на эвакуацию, и точка эвакуации его выпустит</summary>
    public bool WantsExtract => raider &&
        (backpack.MaterialCount >= extractAtMaterials || backpack.IsFull || Time.time - raidStart > extractAfter);

    /// <summary>Рейдер вышел на карту: отсчёт до эвакуации заново, место добычи не выбрано</summary>
    public void BeginRaid()
    {
        raidStart = Time.time;
        gatherSpot = -1;
        heardUntil = float.NegativeInfinity;
        DelayFirstGrenade();
    }

    // Только что вышедший на карту бот не бросает гранату сразу
    void DelayFirstGrenade() => nextGrenade = Time.time + Random.Range(grenadeInterval.x, grenadeInterval.y) * 0.5f;

    /// <summary>Рейдер проверяет место, где слышал врага</summary>
    public bool Investigating => raider && Time.time < heardUntil;

    void OnEnable()
    {
        Noise.Made += Noise_Made;
    }

    // Шум врага в пределах слышимости: рейдер запоминает место и идёт проверить. Своих не слушает
    void Noise_Made(Vector3 position, float radius, CharacterHealth source)
    {
        if (!raider || source == self || source.Team == self.Team || source.IsDead)
            return;
        if (Vector3.Distance(transform.position, position) > radius)
            return;
        heardPosition = position;
        heardUntil = Time.time + investigateTime;
    }

    void Awake()
    {
        shooter = GetComponent<SurvivorShooter>();
        thrower = GetComponent<GrenadeThrower>();
        input = GetComponent<StarterAssetsInputs>();
        controller = GetComponent<CharacterController>();
        self = GetComponent<CharacterHealth>();
        backpack = GetComponent<Backpack>();
        ownColliders = GetComponentsInChildren<Collider>(true);
        noiseSeed = Random.value * 100f;
        path = new NavMeshPath();
        DelayFirstGrenade();
    }

    void Start()
    {
        cam = Camera.main.transform;
    }

    void OnDisable()
    {
        Noise.Made -= Noise_Made;
        heardUntil = float.NegativeInfinity;
        // Мёртвый или выключенный бот не должен продолжать бежать и стрелять
        input.move = Vector2.zero;
        input.sprint = false;
        input.jump = false;
        shooter.AimInput = false;
        shooter.FireInput = false;
        hasPath = false;
        seenSince = -1f;
        target = null;
    }

    void Update()
    {
        if (Time.time >= nextTargetCheck || target == null || target.IsDead)
            ChooseTarget();
        // Ранен и давно не попадали — аптечка
        if (raider && self.Health < self.MaxHealth * 0.5f && Time.time - self.LastHitTime > 2f)
            backpack.UseMedkit();
        if (target == null)
        {
            if (raider)
                Roam();
            else
                Stop();
            return;
        }

        var eye = shooter.EyePosition;
        var chest = target.Chest.position;
        float distance = Vector3.Distance(eye, chest);
        bool sees = distance <= viewDistance && CanSee(eye, target);

        if (sees)
        {
            if (seenSince < 0f)
            {
                seenSince = Time.time;
                nextBurst = Time.time + reactionTime;
            }
            lastSeenTime = Time.time;
            lastSeenPosition = target.transform.position;
        }
        else
            seenSince = -1f;

        // Прицел: грудь цели, плавно гуляет на spreadDegrees
        float wobble = Mathf.Tan(spreadDegrees * Mathf.Deg2Rad) * distance;
        float t = Time.time * 1.3f + noiseSeed;
        var offset = new Vector3(Mathf.PerlinNoise(t, 0f) - 0.5f, Mathf.PerlinNoise(0f, t) - 0.5f, Mathf.PerlinNoise(t, t) - 0.5f) * (2f * wobble);
        shooter.AimTarget = (sees ? chest : lastSeenPosition + Vector3.up * 1.3f) + offset;
        shooter.AimInput = Time.time - lastSeenTime <= aimMemory;

        // Огонь очередями
        bool canFire = sees && distance <= fireDistance && Time.time >= nextBurst;
        if (canFire && Time.time >= burstEnd && burstEnd < nextBurst)
            burstEnd = Time.time + Random.Range(burstTime.x, burstTime.y);
        bool firing = canFire && Time.time < burstEnd;
        if (!firing && burstEnd >= nextBurst && Time.time >= burstEnd)
            nextBurst = Time.time + Random.Range(burstPause.x, burstPause.y);
        shooter.FireInput = firing;
        TryGrenade(sees);

        // Движение
        Vector3 move;
        if (sees && distance < keepDistance)
        {
            if (Time.time >= nextStrafeFlip)
            {
                strafeSign = Random.value < 0.5f ? -1f : 1f;
                nextStrafeFlip = Time.time + Random.Range(strafeTime.x, strafeTime.y);
            }
            var toTarget = Flat(target.transform.position - transform.position).normalized;
            move = Vector3.Cross(Vector3.up, toTarget) * strafeSign;
            if (distance < keepDistance * 0.5f)
                move -= toTarget;
            hasPath = false;
        }
        else
        {
            bool remembers = Time.time - lastSeenTime <= chaseMemory;
            move = FollowPath(sees || !remembers ? target.transform.position : lastSeenPosition);
        }
        move = move.normalized + Separation();

        input.sprint = !sees && Time.time - lastSeenTime > aimMemory;
        SetMove(move);
        UpdateStuck(move);
    }

    // Ближайший видимый враг; если никого не видно - прежняя живая цель или ближайший враг
    void ChooseTarget()
    {
        nextTargetCheck = Time.time + targetCheckInterval;
        var eye = shooter.EyePosition;
        CharacterHealth seen = null, nearest = null;
        float seenDistance = float.MaxValue, nearestDistance = float.MaxValue;
        foreach (var other in CharacterHealth.All)
        {
            if (other.Team == self.Team || other.IsDead)
                continue;
            float d = Vector3.Distance(eye, other.Chest.position);
            if (d < nearestDistance)
            {
                nearestDistance = d;
                nearest = other;
            }
            if (d < seenDistance && d <= viewDistance && CanSee(eye, other))
            {
                seenDistance = d;
                seen = other;
            }
        }

        CharacterHealth next;
        if (!raider)
            next = seen != null ? seen : target != null && !target.IsDead ? target : nearest;
        else if (seen != null)
            next = seen;
        // Рейдер не ищет врагов по всей карте: держит цель, пока помнит, где видел, или отвечает тому, кто попал
        else if (target != null && !target.IsDead && Time.time - lastSeenTime <= chaseMemory)
            next = target;
        else
            next = Grudge();
        if (next == target)
            return;
        target = next;
        seenSince = -1f;
        lastSeenTime = float.NegativeInfinity;
        nextRepath = 0f;
    }

    // Граната в цель: туда, где она сейчас (видна — низкой дугой) или где её видели последний раз (за укрытием —
    // навесом). Не ближе grenadeMinDistance и не дальше grenadeMaxDistance, не под ноги союзникам
    void TryGrenade(bool sees)
    {
        if (Time.time < nextGrenade || Time.time - lastSeenTime > grenadeMemory || !thrower.CanThrow)
            return;
        var point = sees ? target.transform.position : lastSeenPosition;
        float distance = Flat(point - transform.position).magnitude;
        if (distance < grenadeMinDistance || distance > grenadeMaxDistance)
            return;
        foreach (var other in CharacterHealth.All)
            if (other != self && other.Team == self.Team && !other.IsDead &&
                Vector3.Distance(other.transform.position, point) < thrower.BlastRadius)
                return;

        var scatter = Random.insideUnitCircle * grenadeScatter;
        if (thrower.ThrowAt(point + new Vector3(scatter.x, 0f, scatter.y), !sees))
            nextGrenade = Time.time + Random.Range(grenadeInterval.x, grenadeInterval.y);
    }

    // Шаг в сторону от союзников, которые ближе separation
    Vector3 Separation()
    {
        var push = Vector3.zero;
        foreach (var other in CharacterHealth.All)
        {
            if (other == self || other.Team != self.Team || other.IsDead)
                continue;
            var away = Flat(transform.position - other.transform.position);
            float d = away.magnitude;
            if (d < separation && d > 1e-3f)
                push += away / d * (1f - d / separation);
        }
        return push;
    }

    // Враг, который недавно попал в рейдера; null — никто
    CharacterHealth Grudge()
    {
        var attacker = self.LastAttacker;
        if (attacker == null || attacker.IsDead || attacker.Team == self.Team || Time.time - self.LastHitTime > grudgeTime)
            return null;
        return attacker;
    }

    // Рейдер без цели: набрал добычи — идёт на ближайшую эвакуацию, услышал врага — идёт проверить и осматривается,
    // видит мешок или ящик — идёт к нему, иначе добывает: подходит к дереву, камню или руде на gatherDistance
    // и стреляет в них
    void Roam()
    {
        shooter.FireInput = false;
        shooter.AimInput = false;
        var director = RaidDirector.Current;
        Vector3 goal;

        if (WantsExtract)
            goal = director.NearestExtraction(transform.position);
        else if (Investigating)
        {
            // Дошёл — стоит с поднятой винтовкой и смотрит в сторону шума, пока не выйдет время
            if (Flat(heardPosition - transform.position).magnitude <= 4f)
            {
                input.move = Vector2.zero;
                input.sprint = false;
                hasPath = false;
                float t = Time.time * 0.6f + noiseSeed;
                shooter.AimTarget = heardPosition + Vector3.up * 1.3f + Quaternion.Euler(0f, Mathf.Sin(t) * 120f, 0f) * Vector3.forward * 6f;
                shooter.AimInput = true;
                return;
            }
            goal = heardPosition;
        }
        else if (NearestPickup(out var pickup))
            goal = pickup;
        else
        {
            if (gatherSpot < 0 || Time.time >= gatherSpotUntil)
                ChooseGatherSpot(director);
            if (gatherSpot >= 0 && Time.time >= nextGatherCheck)
            {
                // Добыл всё в этом месте — к другому
                nextGatherCheck = Time.time + 0.5f;
                if (!director.GatherSpots.Target(gatherSpot, out gatherPoint))
                {
                    director.GatherSpots.Remove(gatherSpot);
                    gatherSpot = -1;
                }
            }
            if (gatherSpot < 0)
            {
                Stop();
                return;
            }

            if (Flat(gatherPoint - transform.position).magnitude <= gatherDistance)
            {
                input.move = Vector2.zero;
                input.sprint = false;
                hasPath = false;
                shooter.AimTarget = gatherPoint;
                shooter.AimInput = true;
                // Очередями, как в бою: сплошной огонь выбивал материалы слишком быстро
                if (Time.time >= gatherBurstEnd + gatherPause)
                {
                    gatherBurstEnd = Time.time + Random.Range(burstTime.x, burstTime.y);
                    gatherPause = Random.Range(burstPause.x, burstPause.y) * 2f;
                }
                shooter.FireInput = Time.time < gatherBurstEnd;
                return;
            }
            goal = gatherPoint;
        }

        var move = FollowPath(goal).normalized + Separation();
        input.sprint = Flat(goal - transform.position).magnitude > 12f;
        SetMove(move);
        UpdateStuck(move);
    }

    // Место добычи: случайное из нескольких ближайших
    void ChooseGatherSpot(RaidDirector director)
    {
        gatherSpot = director.GatherSpots.RandomNear(transform.position);
        gatherSpotUntil = Time.time + gatherSpotTime;
        nextGatherCheck = 0f;
    }

    bool NearestPickup(out Vector3 position)
    {
        position = default;
        float best = pickupNoticeDistance;
        bool found = false;
        if (backpack.IsFull)
            return false;
        foreach (var pickup in Pickup.All)
        {
            float d = Vector3.Distance(transform.position, pickup.transform.position);
            if (d < best)
            {
                best = d;
                position = pickup.transform.position;
                found = true;
            }
        }
        return found;
    }

    void Stop()
    {
        input.move = Vector2.zero;
        input.sprint = false;
        shooter.AimInput = false;
        shooter.FireInput = false;
    }

    // Цель видна, если луч от глаз до её груди первым попадает в неё, а не в стену или в союзника
    bool CanSee(Vector3 eye, CharacterHealth other)
    {
        var direction = other.Chest.position - eye;
        float nearest = float.MaxValue;
        Collider first = null;
        int count = Physics.RaycastNonAlloc(eye, direction.normalized, SightHits, direction.magnitude + 0.5f, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
        {
            var hit = SightHits[i];
            if (hit.collider is CharacterController || System.Array.IndexOf(ownColliders, hit.collider) >= 0)
                continue;
            if (hit.distance < nearest)
            {
                nearest = hit.distance;
                first = hit.collider;
            }
        }
        return first != null && first.GetComponentInParent<CharacterHealth>() == other;
    }

    // Попадания луча взгляда: один массив на всех ботов, Physics.RaycastAll выделял бы новый на каждый луч
    static readonly RaycastHit[] SightHits = new RaycastHit[64];

    // Направление по пути к goal. Пока NavMesh не собран или пути нет - прямо на goal
    Vector3 FollowPath(Vector3 goal)
    {
        if (Time.time >= nextRepath)
        {
            nextRepath = Time.time + repathInterval;
            Repath(goal);
        }

        if (!hasPath)
            return Flat(goal - transform.position);

        while (corner < cornerCount && Flat(corners[corner] - transform.position).sqrMagnitude < 0.36f)
            corner++;
        if (corner >= cornerCount)
            return Vector3.zero;
        return Flat(corners[corner] - transform.position);
    }

    // Точки пути: path.corners отдаёт новую копию массива при каждом чтении, поэтому точки один раз после
    // CalculatePath копируются сюда. Путь через арену с её связками на швах бывает и в сотню точек: массив растёт
    Vector3[] corners = new Vector3[64];
    int cornerCount;

    void Repath(Vector3 goal)
    {
        hasPath = false;
        if (!navMesh.HasNavMesh)
            return;

        var filter = new NavMeshQueryFilter { agentTypeID = navMesh.AgentTypeID, areaMask = NavMesh.AllAreas };
        if (!NavMesh.SamplePosition(transform.position, out var from, 2f, filter) ||
            !NavMesh.SamplePosition(goal, out var to, 4f, filter))
            return;

        // Частичный путь тоже годится: бот дойдёт до ближайшей точки и оттуда увидит цель или прыгнет
        if (!NavMesh.CalculatePath(from.position, to.position, filter, path))
            return;
        cornerCount = path.GetCornersNonAlloc(corners);
        while (cornerCount == corners.Length)
        {
            corners = new Vector3[corners.Length * 2];
            cornerCount = path.GetCornersNonAlloc(corners);
        }
        hasPath = cornerCount > 1;
        corner = 1;
    }

    // ThirdPersonController понимает input.move относительно камеры игрока
    void SetMove(Vector3 world)
    {
        if (world.sqrMagnitude < 1e-4f)
        {
            input.move = Vector2.zero;
            return;
        }
        var local = Quaternion.Euler(0f, -cam.eulerAngles.y, 0f) * world.normalized;
        input.move = new Vector2(local.x, local.z);
    }

    // Хочет идти, а почти стоит - впереди уступ выше шага CharacterController: прыгнуть
    void UpdateStuck(Vector3 move)
    {
        bool wantsMove = move.sqrMagnitude > 1e-4f;
        bool standing = Flat(controller.velocity).sqrMagnitude < 0.09f;
        stuckTime = wantsMove && standing ? stuckTime + Time.deltaTime : 0f;
        if (stuckTime > 0.4f)
        {
            input.jump = true;
            stuckTime = 0f;
        }
    }

    static Vector3 Flat(Vector3 v) => new(v.x, 0f, v.z);
}
