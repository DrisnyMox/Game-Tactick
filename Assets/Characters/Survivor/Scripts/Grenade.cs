using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Брошенная граната: летит и отскакивает как тело, через fuseTime взрывается.
/// Взрыв выбивает воксели арены шаром craterRadius, а персонажей в blastRadius ранит и отбрасывает
/// (CharacterHealth.OnBlast): уничтоженный падает рэгдоллом, уцелевший тоже, а потом встаёт.
/// Взрыв шумит (Noise), рейдеры идут проверить. Свои гранаты союзников не задевают, бросившего — задевают.
/// Бросает GrenadeThrower, префаб собирает Tools/Unity/SetupGrenade.cs.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class Grenade : MonoBehaviour
{
    [Tooltip("Через сколько секунд после броска взрыв")]
    [SerializeField] float fuseTime = 3f;
    [Tooltip("Радиус шара выбитых вокселей, м")]
    [SerializeField] float craterRadius = 1.6f;
    [Tooltip("Во сколько раз быстрее пули разлетаются осколки выбитых вокселей (VoxelArena.DebrisPower)")]
    [SerializeField] float debrisPower = 3.5f;
    [Tooltip("Дальше этого от груди персонажа взрыв его не задевает, м")]
    [SerializeField] float blastRadius = 7f;
    [Tooltip("Урон в центре взрыва, к краю blastRadius спадает до нуля")]
    [SerializeField] float damage = 120f;
    [Tooltip("Скорость, которую взрыв даёт телу с грудью в центре взрыва, м/с (к краю blastRadius спадает до нуля)")]
    [SerializeField] float knockSpeed = 39f;
    [Tooltip("Насколько взрыв подбрасывает вверх: тело летит от точки ниже центра на столько метров")]
    [SerializeField] float knockLift = 1.5f;

    [Header("Удар о землю")]
    [Tooltip("Какую долю скорости граната сохраняет после первого удара: падает почти туда, куда брошена")]
    [Range(0f, 1f)] [SerializeField] float firstImpactKeep = 0.45f;
    [Tooltip("Торможение после первого удара (linearDamping): граната не катится дальше пары метров")]
    [SerializeField] float groundDamping = 1.5f;
    [Tooltip("Торможение вращения после первого удара (angularDamping): шар без него катится почти без потерь")]
    [SerializeField] float groundAngularDamping = 3f;

    [Header("Эффект")]
    [Tooltip("Модель гранаты: прячется при взрыве")]
    [SerializeField] Renderer body;
    [Tooltip("Огонь и пыль взрыва: запускаются при взрыве")]
    [SerializeField] ParticleSystem[] explosionParticles;
    [Tooltip("Вспышка: включается при взрыве и гаснет за flashTime")]
    [SerializeField] Light flash;
    [SerializeField] float flashTime = 0.35f;
    [Tooltip("Звук гранаты: взрыв (clip) и стук при ударах (PlayOneShot)")]
    [SerializeField] AudioSource explosionAudio;
    [Tooltip("Сколько секунд объект живёт после взрыва: пока горят частицы и звучит звук")]
    [SerializeField] float afterlife = 3f;

    [Header("Звук удара")]
    [Tooltip("Удар о песок, землю, траву")]
    [SerializeField] AudioClip[] softImpactClips;
    [Tooltip("Удар о дерево и листья")]
    [SerializeField] AudioClip[] woodImpactClips;
    [Tooltip("Удар о камень, металл и всё остальное")]
    [SerializeField] AudioClip[] hardImpactClips;
    [Tooltip("Медленнее этого, м/с, удар не слышно: граната катится")]
    [SerializeField] float quietImpactSpeed = 1.5f;
    [Tooltip("С этой скорости, м/с, удар звучит в полную громкость")]
    [SerializeField] float loudImpactSpeed = 12f;
    [Range(0f, 1f)] [SerializeField] float impactVolume = 0.8f;

    // Катящаяся граната касается стыков граней вокселей часто: звуки не чаще этого, с
    const float ImpactSoundInterval = 0.1f;

    Rigidbody body3d;
    float nextImpactSound;
    CharacterHealth thrower;
    float explodeTime;
    float explodedAt = -1f;
    float flashIntensity;
    bool landed;

    public float BlastRadius => blastRadius;

    // Коллайдеры вокруг взрыва: ищется арена. Один массив на все гранаты
    static readonly Collider[] Nearby = new Collider[32];
    static readonly HashSet<VoxelArena> Arenas = new();

    void Awake()
    {
        body3d = GetComponent<Rigidbody>();
        flashIntensity = flash.intensity;
        flash.enabled = false;
    }

    /// <summary>Граната вылетает со скоростью velocity. Коллайдеры бросившего она пролетает насквозь</summary>
    public void Launch(CharacterHealth owner, Vector3 velocity, Collider[] ownerColliders)
    {
        thrower = owner;
        var own = GetComponent<Collider>();
        foreach (var collider in ownerColliders)
            Physics.IgnoreCollision(own, collider);
        body3d.linearVelocity = velocity;
        body3d.angularVelocity = Random.insideUnitSphere * 10f;
        explodeTime = Time.time + fuseTime;
    }

    // Первый удар гасит почти всю скорость и включает сильное торможение: граната остаётся там, куда упала,
    // а не отскакивает и не катится по уклону. Шар в PhysX катится без трения качения, торможение его останавливает
    void OnCollisionEnter(Collision collision)
    {
        PlayImpact(collision);
        // Удар о другую гранату или кость рэгдолла — не земля
        if (landed || collision.rigidbody != null)
            return;
        landed = true;
        body3d.linearVelocity *= firstImpactKeep;
        body3d.angularVelocity *= firstImpactKeep;
        body3d.linearDamping = groundDamping;
        body3d.angularDamping = groundAngularDamping;
    }

    // Стук об поверхность: звук по виду поверхности (как у пули, IShootable.SurfaceAt), громкость по скорости удара.
    // Не арена (тело, ящик) — звук твёрдой поверхности
    void PlayImpact(Collision collision)
    {
        float speed = collision.relativeVelocity.magnitude;
        if (speed < quietImpactSpeed || Time.time < nextImpactSound)
            return;
        nextImpactSound = Time.time + ImpactSoundInterval;

        var contact = collision.GetContact(0).point;
        var surface = HitSurface.Default;
        var shootable = collision.collider.GetComponentInParent<IShootable>();
        if (shootable != null)
        {
            // Нормаль — от точки касания к середине гранаты: наружу из поверхности, как у луча пули
            var hit = new RaycastHit { point = contact, normal = (transform.position - contact).normalized };
            surface = shootable.SurfaceAt(hit);
        }
        var clips = surface == HitSurface.Soft ? softImpactClips : surface == HitSurface.Wood ? woodImpactClips : hardImpactClips;
        float volume = impactVolume * Mathf.InverseLerp(quietImpactSpeed, loudImpactSpeed, speed);
        explosionAudio.PlayOneShot(clips[Random.Range(0, clips.Length)], volume);
    }

    void Update()
    {
        if (explodedAt < 0f)
        {
            if (Time.time >= explodeTime)
                Explode();
            return;
        }
        float t = (Time.time - explodedAt) / flashTime;
        flash.intensity = flashIntensity * Mathf.Clamp01(1f - t);
        flash.enabled = t < 1f;
        if (Time.time - explodedAt >= afterlife)
            Destroy(gameObject);
    }

    void Explode()
    {
        explodedAt = Time.time;
        var center = transform.position;

        // Граната больше не тело: стоит на месте, пока догорают частицы
        body3d.isKinematic = true;
        GetComponent<Collider>().enabled = false;
        body.enabled = false;
        foreach (var particles in explosionParticles)
            particles.Play();
        flash.enabled = true;
        explosionAudio.Play();

        // Арена рядом: её коллайдеры чанков лежат под объектом арены. Без арены (площадка) воксели не выбиваются
        Arenas.Clear();
        int count = Physics.OverlapSphereNonAlloc(center, craterRadius, Nearby, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
        {
            var arena = Nearby[i].GetComponentInParent<VoxelArena>();
            if (arena != null && Arenas.Add(arena))
                arena.Explode(center, craterRadius, int.MaxValue, debrisPower);
        }

        // Ушедший с карты рейдер (Withdraw) выключен: его взрыв не задевает
        foreach (var character in CharacterHealth.All)
            if (character.gameObject.activeInHierarchy)
                character.OnBlast(center, blastRadius, damage, knockSpeed, knockLift, thrower);

        Noise.Make(center, Noise.BlastRadius, thrower);
    }
}
