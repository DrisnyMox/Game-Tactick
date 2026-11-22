using UnityEngine;
using UnityEngine.Serialization;

/// <summary>
/// Винтовка выжившего: точки на модели и эффекты выстрела.
/// Стрельбу ведёт SurvivorShooter, а этот компонент только показывает выстрел: вспышку у дула, свет, гильзу и звук.
/// Префаб собирает Tools/Unity/SetupSurvivorShooter.cs из винтовки Invector (точки, частицы, звук),
/// а модель ScrapRifle вместо модели Invector ставит Tools/Unity/SetupScrapRifle.cs.
/// </summary>
public class ShooterWeapon : MonoBehaviour
{
    [Tooltip("Точка дула: от неё летит пуля")]
    [FormerlySerializedAs("Muzzle")]
    [SerializeField] Transform muzzle;

    [Tooltip("Положение и поворот кости левой кисти на цевье: туда SurvivorShooter ставит левую ладонь")]
    [FormerlySerializedAs("LeftHandGrip")]
    [SerializeField] Transform leftHandGrip;

    [Tooltip("Положение и поворот кости правой кисти на пистолетной рукояти: туда SurvivorShooter переносит кисть")]
    [SerializeField] Transform rightHandGrip;

    [Tooltip("Вспышка и дым у дула: перезапускаются на каждый выстрел")]
    [SerializeField] ParticleSystem[] muzzleParticles;

    [Tooltip("Гильза: одна частица на выстрел")]
    [SerializeField] ParticleSystem shellParticles;

    [SerializeField] Light flashLight;
    [SerializeField] float flashTime = 0.04f;
    [SerializeField] AudioSource audioSource;
    [SerializeField] AudioClip fireClip;
    [SerializeField] AudioClip reloadClip;
    [SerializeField] AudioClip emptyClip;

    float flashUntil;

    public Transform Muzzle => muzzle;
    public Transform LeftHandGrip => leftHandGrip;
    public Transform RightHandGrip => rightHandGrip;
    public AudioSource Audio => audioSource;

    void Awake()
    {
        flashLight.enabled = false;
    }

    void Update()
    {
        if (flashLight.enabled && Time.time >= flashUntil)
            flashLight.enabled = false;
    }

    public void PlayShot()
    {
        foreach (var particles in muzzleParticles)
        {
            particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            particles.Play(true);
        }
        shellParticles.Emit(1);
        flashLight.enabled = true;
        flashUntil = Time.time + flashTime;
        audioSource.PlayOneShot(fireClip);
    }

    public void PlayReload()
    {
        audioSource.PlayOneShot(reloadClip);
    }

    public void PlayEmpty()
    {
        audioSource.PlayOneShot(emptyClip);
    }
}
