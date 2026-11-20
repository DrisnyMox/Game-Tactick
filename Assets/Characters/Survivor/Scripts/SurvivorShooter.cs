using System.Collections.Generic;
using StarterAssets;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Стрельба выжившего поверх ThirdPersonController из Starter Assets.
/// Мышь: ПКМ - прицел, ЛКМ - огонь (тоже поднимает винтовку к плечу). Клавиша R - перезарядка.
/// Геймпад: левый бампер - прицел, правый курок - огонь, X (west) - перезарядка.
///
/// Как устроено:
///  * винтовка висит на правой кисти, левую ладонь на цевье держит своя IK на две кости (аниматор её не трогает);
///  * верх тела играет позы Invector на слое аниматора "Rifle", параметр Aiming переключает "держит" и "целится";
///  * при прицеливании персонаж поворачивается туда же, куда камера, а грудь наклоняется за камерой вверх и вниз;
///  * пуля - луч из центра экрана; если между дулом и целью что-то есть, попадает в это;
///  * камера при прицеливании подъезжает к плечу через CinemachineThirdPersonFollow.
/// Ввод как у StarterAssetsInputs: действия Aim, Fire, Reload в StarterAssets.inputactions,
/// PlayerInput присылает их сообщениями OnAim, OnFire, OnReload и они ложатся в свойства AimInput, FireInput, ReloadInput.
/// У бота (botControlled) свойства ввода и AimTarget ставит ShooterBot, а пуля летит из глаз в AimTarget.
/// Компонент и все ссылки ставит Tools/Unity/SetupSurvivorShooter.cs, бота - Tools/Unity/SetupArenaBot.cs.
/// </summary>
// Update идёт после Update у ThirdPersonController: поворот к камере перекрывает его поворот по ходу движения.
// Поворот персонажа обязан быть в Update, а не в LateUpdate: ThirdPersonController в LateUpdate ставит поворот
// PlayerCameraRoot, и поворот персонажа после этого сбивает точку камеры (камера дёргается при ходьбе с прицелом)
[DefaultExecutionOrder(100)]
[RequireComponent(typeof(Animator), typeof(ThirdPersonController), typeof(StarterAssetsInputs))]
public class SurvivorShooter : MonoBehaviour
{
    static readonly int AimingId = Animator.StringToHash("Aiming");
    static readonly int ShootId = Animator.StringToHash("Shoot");
    static readonly int ReloadId = Animator.StringToHash("Reload");
    static readonly int AimXId = Animator.StringToHash("AimX");
    static readonly int AimYId = Animator.StringToHash("AimY");

    /// <summary>Прицел зажат (ставит PlayerInput или ShooterBot)</summary>
    public bool AimInput { get; set; }
    /// <summary>Огонь зажат (ставит PlayerInput или ShooterBot)</summary>
    public bool FireInput { get; set; }
    /// <summary>Нажата перезарядка; Update сбрасывает, когда начнёт её</summary>
    public bool ReloadInput { get; set; }
    /// <summary>Куда целится бот (ставит ShooterBot)</summary>
    public Vector3 AimTarget { get; set; }

    [Header("Бот")]
    [Tooltip("Стрелком управляет ShooterBot: целится из глаз (PlayerCameraRoot) в AimTarget, а не из камеры " +
             "в центр экрана, камеру к плечу не двигает и ничего не рисует на экране")]
    [SerializeField] bool botControlled;

    [Header("Винтовка")]
    [SerializeField] ShooterWeapon weaponPrefab;
    [Tooltip("Положение винтовки относительно кости правой кисти")]
    [SerializeField] Vector3 weaponLocalPosition;
    [SerializeField] Vector3 weaponLocalEuler;
    [SerializeField] int magazineSize = 30;
    [SerializeField] float fireInterval = 0.1f;
    [SerializeField] float reloadTime = 2.6f;
    [SerializeField] float range = 200f;
    [Tooltip("Урон одной пули (CharacterHealth умножает его при попадании в голову)")]
    [SerializeField] float damage = 20f;
    [Tooltip("Толчок телам с Rigidbody от попадания")]
    [SerializeField] float hitImpulse = 4f;
    [SerializeField] LayerMask hitLayers = ~0;

    [Header("Попадание")]
    [Tooltip("Эффект и звук попадания по видам поверхности (HitSurface). Вид без своей строки берёт строку Default")]
    [SerializeField] SurfaceImpact[] impacts;
    [SerializeField] GameObject decalPrefab;
    [SerializeField] float impactLifetime = 2f;
    [SerializeField] float decalLifetime = 20f;

    [Header("Прицел (только игрок), в пикселях при высоте экрана 1080")]
    [Tooltip("От центра до начала штриха")]
    [SerializeField] float crosshairGap = 6f;
    [Tooltip("Длина штриха")]
    [SerializeField] float crosshairSize = 14f;
    [SerializeField] float crosshairThickness = 3f;

    [Header("Отметка попадания (только игрок)")]
    [Tooltip("Щелчок, когда пуля попала во врага")]
    [SerializeField] AudioClip hitMarkerClip;
    [Tooltip("Звук попадания в голову")]
    [SerializeField] AudioClip headshotMarkerClip;
    [Tooltip("Звук убийства")]
    [SerializeField] AudioClip killMarkerClip;
    [Range(0f, 1f)] [SerializeField] float markerVolume = 0.7f;
    [Tooltip("Сколько секунд видна отметка попадания")]
    [SerializeField] float markerTime = 0.25f;

    [Header("Отдача")]
    [Tooltip("Камера игрока уходит вверх на столько градусов за выстрел")]
    [SerializeField] float recoilPitch = 0.3f;
    [Tooltip("И вбок на случайный угол до стольких градусов")]
    [SerializeField] float recoilYaw = 0.08f;
    [Tooltip("Как быстро камера доходит до толчка отдачи (1/с): больше - резче")]
    [SerializeField] float recoilRise = 30f;
    [Tooltip("Как быстро камера возвращается назад после выстрелов (1/с)")]
    [SerializeField] float recoilReturn = 6f;
    [Tooltip("Корпус с винтовкой вздрагивает вверх на столько градусов за выстрел (виден и у ботов)")]
    [SerializeField] float kickAngle = 2.5f;
    [Tooltip("Как быстро корпус возвращается после выстрела (1/с)")]
    [SerializeField] float kickRecovery = 18f;

    [Header("Прицеливание")]
    [SerializeField] float aimMoveSpeed = 1.8f;
    [Tooltip("Насколько быстро поднимается винтовка и подъезжает камера (1/с)")]
    [SerializeField] float aimBlendSpeed = 8f;
    [Tooltip("Доля наклона к камере на кости Spine, остаток идёт на Chest")]
    [Range(0, 1)] [SerializeField] float spinePitchShare = 0.4f;
    [SerializeField] CinemachineThirdPersonFollow followCamera;
    [SerializeField] float aimCameraDistance = 1.8f;
    [SerializeField] Vector3 aimShoulderOffset = new(0.6f, 0.05f, 0f);
    [Tooltip("CameraSide при прицеливании: 1 - камера целиком за правым плечом. " +
             "В PlayerFollowCamera стоит 0.6, и из ShoulderOffset.x работает только пятая часть")]
    [Range(0, 1)] [SerializeField] float aimCameraSide = 1f;

    Animator animator;
    ThirdPersonController controller;
    StarterAssetsInputs input;
    CharacterHealth health;
    Transform cam;
    Transform eye;
    ShooterWeapon weapon;
    Transform spine, chest, leftUpperArm, leftLowerArm, leftHand;
    Collider[] ownColliders;

    float defaultMoveSpeed;
    float defaultCameraDistance;
    Vector3 defaultShoulderOffset;
    float defaultCameraSide;

    bool aiming;
    bool wasAiming;
    float aimYaw;
    float aimWeight;
    Vector2 aimMove;
    int ammo;
    float nextShotTime;
    float reloadEndTime = -1f;
    // Текущий вздрог корпуса от выстрелов, градусы: растёт на kickAngle за выстрел и гаснет
    float kick;
    // Отдача камеры, градусы (x - вверх-вниз, y - вбок): куда она тянет и сколько уже приложено к камере.
    // Цель растёт с каждым выстрелом и гаснет к нулю, приложенное догоняет цель - камера плавно уходит и возвращается
    Vector2 recoilTarget;
    Vector2 recoilApplied;
    // Живой персонаж под прицелом игрока (null - никого): по нему красится прицел
    CharacterHealth aimedCharacter;
    ShooterHud hud;

    public int Ammo => ammo;
    /// <summary>
    /// Запас патронов для перезарядки (рюкзак игрока на вылазке). null — запас бесконечный, как у ботов
    /// и в бою на арене
    /// </summary>
    public IAmmoReserve Reserve { get; set; }
    /// <summary>Сколько патронов в магазине (большой магазин — больше). Новый размер — магазин полный</summary>
    public int MagazineSize
    {
        get => magazineSize;
        set
        {
            magazineSize = value;
            Refill();
        }
    }
    public bool Reloading => reloadEndTime >= 0f;
    public bool BotControlled => botControlled;
    /// <summary>Точка у глаз персонажа: из неё целится бот</summary>
    public Vector3 EyePosition => eye.position;

    // Для ShooterHud
    internal float AimWeight => aimWeight;
    internal CharacterHealth AimedCharacter => aimedCharacter;
    internal float CrosshairGap => crosshairGap;
    internal float CrosshairSize => crosshairSize;
    internal float CrosshairThickness => crosshairThickness;
    internal float MarkerTime => markerTime;

    void Awake()
    {
        // HUD рисуется только GUI.*, без GUILayout: без раскладки Unity не гоняет OnGUI лишний раз и не выделяет под неё память
        useGUILayout = false;
        animator = GetComponent<Animator>();
        controller = GetComponent<ThirdPersonController>();
        input = GetComponent<StarterAssetsInputs>();
        health = GetComponent<CharacterHealth>();
        hud = new ShooterHud(this, health);
        ownColliders = GetComponentsInChildren<Collider>(true);
        eye = controller.CinemachineCameraTarget.transform;

        spine = animator.GetBoneTransform(HumanBodyBones.Spine);
        chest = animator.GetBoneTransform(HumanBodyBones.Chest);
        leftUpperArm = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
        leftLowerArm = animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
        leftHand = animator.GetBoneTransform(HumanBodyBones.LeftHand);

        var rightHand = animator.GetBoneTransform(HumanBodyBones.RightHand);
        weapon = Instantiate(weaponPrefab, rightHand);
        weapon.transform.SetLocalPositionAndRotation(weaponLocalPosition, Quaternion.Euler(weaponLocalEuler));

        defaultMoveSpeed = controller.MoveSpeed;
        ammo = magazineSize;

        if (botControlled)
        {
            // Чужой выстрел слышно с той стороны, где стреляют, и тише издалека
            weapon.Audio.spatialBlend = 1f;
            weapon.Audio.minDistance = 4f;
            return;
        }

        if (followCamera == null)
        {
            Debug.LogError($"[SurvivorShooter] {name}: не задана камера Follow Camera (CinemachineThirdPersonFollow " +
                           "на PlayerFollowCamera). Без неё камера не подъезжает к плечу при прицеливании. " +
                           "Перетащи PlayerFollowCamera в поле Follow Camera или запусти Tools/Unity/SetupSurvivorShooter.cs.", this);
            return;
        }
        defaultCameraDistance = followCamera.CameraDistance;
        defaultShoulderOffset = followCamera.ShoulderOffset;
        defaultCameraSide = followCamera.CameraSide;
    }

    void Start()
    {
        cam = Camera.main.transform;
    }

    void OnAim(InputValue value) => AimInput = value.isPressed;
    void OnFire(InputValue value) => FireInput = value.isPressed;

    void OnReload(InputValue value)
    {
        if (value.isPressed)
            ReloadInput = true;
    }

    void Update()
    {
        bool fireHeld = FireInput;

        if (Reloading && Time.time >= reloadEndTime)
        {
            reloadEndTime = -1f;
            int need = magazineSize - ammo;
            ammo += Reserve == null ? need : Reserve.TakeAmmo(need);
        }
        if (ReloadInput)
        {
            ReloadInput = false;
            if (!Reloading && ammo < magazineSize && HasReserve)
                StartReload();
        }

        aiming = (AimInput || fireHeld) && !Reloading;
        aimWeight = Mathf.MoveTowards(aimWeight, aiming ? 1f : 0f, aimBlendSpeed * Time.deltaTime);

        if (aiming)
            input.sprint = false;
        controller.MoveSpeed = aiming ? aimMoveSpeed : defaultMoveSpeed;

        // Персонаж смотрит туда же, куда целится (игрок - куда камера). ThirdPersonController выше в этом кадре
        // повернул его по ходу движения, поэтому угол хранится здесь, догоняет прицел и ставится целиком,
        // перекрывая тот поворот
        if (aiming)
        {
            if (!wasAiming)
                aimYaw = transform.eulerAngles.y;
            var flat = AimDirection();
            flat.y = 0f;
            float lookYaw = flat.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(flat).eulerAngles.y : aimYaw;
            aimYaw = Mathf.LerpAngle(aimYaw, lookYaw, 1f - Mathf.Exp(-25f * Time.deltaTime));
            transform.rotation = Quaternion.Euler(0f, aimYaw, 0f);
        }
        wasAiming = aiming;

        aimMove = Vector2.MoveTowards(aimMove, aiming ? MoveInFacing() : Vector2.zero, 6f * Time.deltaTime);
        animator.SetBool(AimingId, aiming);
        animator.SetFloat(AimXId, aimMove.x);
        animator.SetFloat(AimYId, aimMove.y);

        // Огонь только когда винтовка уже у плеча, иначе пуля летит из опущенного ствола
        if (fireHeld && aimWeight > 0.9f && Time.time >= nextShotTime && !Reloading)
        {
            nextShotTime = Time.time + fireInterval;
            if (ammo > 0)
                Shoot();
            else
            {
                weapon.PlayEmpty();
                if (HasReserve)
                    StartReload();
            }
        }

        if (!botControlled)
        {
            ApplyCameraRecoil();
            aimedCharacter = null;
            if (aiming && Raycast(cam.position, cam.forward, range, out var aimed, true))
            {
                var character = aimed.collider.GetComponentInParent<CharacterHealth>();
                if (character != null && !character.IsDead)
                    aimedCharacter = character;
            }
        }

        if (followCamera != null)
        {
            followCamera.CameraDistance = Mathf.Lerp(defaultCameraDistance, aimCameraDistance, aimWeight);
            followCamera.ShoulderOffset = Vector3.Lerp(defaultShoulderOffset, aimShoulderOffset, aimWeight);
            followCamera.CameraSide = Mathf.Lerp(defaultCameraSide, aimCameraSide, aimWeight);
        }
    }

    // Камера сдвигается на разницу между тем, куда тянет отдача, и тем, что уже приложено.
    // Мышь игрока при этом работает как обычно: отдача только добавляется к ней
    void ApplyCameraRecoil()
    {
        float dt = Time.deltaTime;
        recoilTarget *= Mathf.Exp(-recoilReturn * dt);
        var next = Vector2.Lerp(recoilApplied, recoilTarget, 1f - Mathf.Exp(-recoilRise * dt));
        var delta = next - recoilApplied;
        controller.AddCameraRotation(delta.x, delta.y);
        recoilApplied = next;
    }

    // Ввод движения относительно того, куда смотрит персонаж: x - вбок, y - вперёд (для смеси Aim Strafe).
    // ThirdPersonController понимает input.move относительно камеры. У игрока с прицелом персонаж смотрит
    // туда же, куда камера, и это одно и то же; у бота - нет
    Vector2 MoveInFacing()
    {
        var world = Quaternion.Euler(0f, cam.eulerAngles.y, 0f) * new Vector3(input.move.x, 0f, input.move.y);
        var local = Quaternion.Inverse(transform.rotation) * world;
        return new Vector2(local.x, local.z);
    }

    // Откуда целится: игрок - из камеры в центр экрана, бот - из глаз в AimTarget
    Vector3 AimOrigin() => botControlled ? eye.position : cam.position;

    Vector3 AimDirection() => botControlled ? (AimTarget - eye.position).normalized : cam.forward;

    void LateUpdate()
    {
        var look = AimDirection();

        if (aimWeight > 0f)
        {
            // Наклон корпуса вверх и вниз за камерой. Позы Invector держат ствол горизонтально
            float pitch = -Mathf.Asin(Mathf.Clamp(look.y, -1f, 1f)) * Mathf.Rad2Deg * aimWeight;
            var axis = transform.right;
            spine.rotation = Quaternion.AngleAxis(pitch * spinePitchShare, axis) * spine.rotation;
            chest.rotation = Quaternion.AngleAxis(pitch * (1f - spinePitchShare), axis) * chest.rotation;

            // Довернуть грудь, чтобы ствол смотрел в точку под прицелом. Винтовка висит на правой кисти,
            // кисть на груди, поэтому поворот груди поворачивает ствол так же
            var muzzle = weapon.Muzzle;
            var target = AimPoint();
            var fix = Quaternion.FromToRotation(muzzle.forward, target - muzzle.position);
            chest.rotation = Quaternion.Slerp(Quaternion.identity, fix, aimWeight) * chest.rotation;
        }

        // Отдача: корпус с винтовкой вздрагивает вверх (после доворота к прицелу, иначе доворот её съест).
        // Пуля летит по прицелу, вздрог только виден
        kick *= Mathf.Exp(-kickRecovery * Time.deltaTime);
        if (kick > 0.01f)
            chest.rotation = Quaternion.AngleAxis(-kick, transform.right) * chest.rotation;

        // Во время перезарядки левая рука работает по анимации Reload_Rifle
        float gripWeight = Reloading ? 0f : 1f;
        if (gripWeight > 0f)
            TwoBoneIK.Solve(leftUpperArm, leftLowerArm, leftHand, weapon.LeftHandGrip.position, gripWeight);
    }

    /// <summary>Полный магазин, перезарядка прервана (возрождение)</summary>
    public void Refill()
    {
        ammo = magazineSize;
        reloadEndTime = -1f;
    }

    /// <summary>Сколько патронов в магазине сейчас (первый магазин вылазки берётся из рюкзака)</summary>
    public void SetLoaded(int count)
    {
        ammo = Mathf.Clamp(count, 0, magazineSize);
        reloadEndTime = -1f;
    }

    bool HasReserve => Reserve == null || Reserve.Reserve > 0;

    void StartReload()
    {
        reloadEndTime = Time.time + reloadTime;
        animator.SetTrigger(ReloadId);
        weapon.PlayReload();
    }

    // Точка под прицелом: куда попадает луч прицела, а если никуда - точка на дальности range
    Vector3 AimPoint()
    {
        var origin = AimOrigin();
        var direction = AimDirection();
        return Raycast(origin, direction, range, out var hit) ? hit.point : origin + direction * range;
    }

    void Shoot()
    {
        ammo--;
        Noise.Make(transform.position, Noise.ShotRadius, health);
        animator.SetTrigger(ShootId);
        weapon.PlayShot();
        kick += kickAngle;
        recoilTarget += new Vector2(-recoilPitch, Random.Range(-recoilYaw, recoilYaw));

        var origin = AimOrigin();
        var direction = AimDirection();
        bool hasHit = Raycast(origin, direction, range, out var hit);
        Vector3 target = hasHit ? hit.point : origin + direction * range;

        // Цель видна из камеры, но от дула её может закрывать стена или угол
        var muzzle = weapon.Muzzle.position;
        var toTarget = target - muzzle;
        if (Raycast(muzzle, toTarget.normalized, toTarget.magnitude, out var blocked))
        {
            hit = blocked;
            hasHit = true;
        }
        if (!hasHit)
            return;

        // Объект может сам ответить на попадание (арена выбивает воксели, и след висел бы в воздухе).
        // Вид поверхности спрашивается до OnShot: после него вокселя в точке уже нет
        var shootable = hit.collider.GetComponentInParent<IShootable>();
        PlayImpact(shootable != null ? shootable.SurfaceAt(hit) : HitSurface.Default, hit);

        var victim = shootable as CharacterHealth;
        bool enemyHit = victim != null && victim.Team != health.Team && !victim.IsDead;
        bool leaveDecal = shootable == null || shootable.OnShot(hit, toTarget.normalized, damage, health);
        if (enemyHit && !botControlled)
            ShowHitMarker(victim.IsHead(hit.collider), victim.IsDead);
        if (leaveDecal)
        {
            var decal = Instantiate(decalPrefab, hit.point + hit.normal * 0.01f, Quaternion.LookRotation(-hit.normal), hit.collider.transform);
            Destroy(decal, decalLifetime);
        }

        if (hit.rigidbody != null)
            hit.rigidbody.AddForceAtPosition(toTarget.normalized * hitImpulse, hit.point, ForceMode.Impulse);
    }

    // Эффект и случайный звук из строки impacts для этого вида поверхности (нет строки - строка Default)
    void PlayImpact(HitSurface surface, RaycastHit hit)
    {
        int index = ImpactIndex(surface);
        if (index < 0)
            index = ImpactIndex(HitSurface.Default);
        var entry = impacts[index];

        var effect = Instantiate(entry.Effect, hit.point, Quaternion.LookRotation(hit.normal));
        if (!Mathf.Approximately(entry.Scale, 1f))
        {
            // У частиц Invector режим Shape: масштаб объекта меняет только область вылета, а не размер частиц
            effect.GetComponentsInChildren(EffectParticles);
            foreach (var particles in EffectParticles)
            {
                var main = particles.main;
                main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            }
            effect.transform.localScale *= entry.Scale;
        }
        Destroy(effect, impactLifetime);
        if (entry.Sounds.Length > 0)
            AudioSource.PlayClipAtPoint(entry.Sounds[Random.Range(0, entry.Sounds.Length)], hit.point, entry.Volume);
    }

    // Строка impacts для вида поверхности или -1. Циклом, а не Array.FindIndex с лямбдой: лямбда с захватом выделяет память
    int ImpactIndex(HitSurface surface)
    {
        for (int i = 0; i < impacts.Length; i++)
            if (impacts[i].Surface == surface)
                return i;
        return -1;
    }

    // Системы частиц эффекта попадания: один список на всех стрелков
    static readonly List<ParticleSystem> EffectParticles = new();

    void ShowHitMarker(bool head, bool kill)
    {
        hud.ShowHitMarker(head, kill);
        weapon.Audio.PlayOneShot(kill ? killMarkerClip : head ? headshotMarkerClip : hitMarkerClip, markerVolume);
    }

    // Ближайшее попадание, кроме коллайдеров самого стрелка. CharacterController других персонажей пуля
    // проходит насквозь: попадают кости рэгдолла (CharacterHealth), а промах между рукой и телом остаётся промахом
    // withControllers - считать и CharacterController (для цвета прицела: капсула не мигает между рукой и телом)
    bool Raycast(Vector3 origin, Vector3 direction, float distance, out RaycastHit nearest, bool withControllers = false)
    {
        nearest = default;
        bool found = false;
        int count = Physics.RaycastNonAlloc(origin, direction, RayHits, distance, hitLayers, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
        {
            var h = RayHits[i];
            if ((!withControllers && h.collider is CharacterController) || System.Array.IndexOf(ownColliders, h.collider) >= 0 ||
                (found && h.distance >= nearest.distance))
                continue;
            nearest = h;
            found = true;
        }
        return found;
    }

    // Попадания луча для Raycast: один массив на всех стрелков, Physics.RaycastAll выделял бы новый на каждый луч.
    // Больше 64 коллайдеров на одном луче не бывает: дальность луча - range, а на нём земля, стены и пара тел
    static readonly RaycastHit[] RayHits = new RaycastHit[64];

    void OnGUI()
    {
        // OnGUI рисуется поверх любого Canvas: под экраном загрузки и меню настроек надписи не нужны
        if (ArenaLoadingScreen.IsShown || SettingsMenu.IsOpen)
            return;
        if (botControlled)
            return;
        hud.Draw(cam);
    }
}
