using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;

/// <summary>
/// Персонаж в окне снаряжения — сам игрок в мире. Пока окно открыто, включается своя CinemachineCamera перед лицом
/// игрока (приоритет выше камеры игры). Остров за игроком закрывает фон окна: он на Canvas в режиме
/// Screen Space - Camera, его рисует Main Camera. Игрок на это время переходит на слой Preview: Main Camera этот слой
/// не рисует, а рисует Overlay-камера из стека Main Camera — поверх фона.
///
/// Переход делает этот компонент, а не Cinemachine: переход Cinemachine ведёт камеру по прямой из-за спины к лицу
/// (почти сквозь игрока) и поворачивает её отдельно, и в середине пути игрок выпадает из кадра. Здесь камера окна
/// встаёт туда, где стоит камера игры, и облетает игрока по дуге, всё время глядя на него; при закрытии облетает
/// обратно и отдаёт кадр камере игры там же, где его взяла (игрок за это время не двигался: ввод выключен).
/// У Cinemachine переход к камере окна и обратно — мгновенный (OutfitBlends.asset).
///
/// Перетаскивание по середине окна крутит камеру вокруг игрока, колесо приближает, выбранный слот наводит камеру
/// на свою часть тела. Время берётся без масштаба.
/// </summary>
public class OutfitPreview : MonoBehaviour, IDragHandler, IScrollHandler
{
    [SerializeField] Transform target;
    [SerializeField] Animator animator;
    [SerializeField] CinemachineCamera outfitCamera;
    [Tooltip("Overlay-камера в стеке Main Camera: рисует только слой Preview")]
    [SerializeField] Camera overlayCamera;
    [Tooltip("Подсветка спереди: на базе солнце может светить игроку в спину")]
    [SerializeField] Light fill;
    [Tooltip("Слой, который рисует только Overlay-камера (Preview)")]
    [SerializeField] int previewLayer = 7;
    [Tooltip("Насколько градусов камера сбоку от лица в начале")]
    [SerializeField] float startYaw = 22f;
    [SerializeField] float dragSpeed = 0.35f;
    [Tooltip("Угол обзора камеры окна, градусы")]
    [SerializeField] float fieldOfView = 18f;
    [Tooltip("Облёт от камеры игры к лицу и обратно, с")]
    [SerializeField] float transitionTime = 0.6f;

    Camera mainCamera;
    Renderer[] renderers;
    int[] savedLayers;
    int savedMask;
    float yaw, pitch = 6f, zoom = 1f;
    GearSlot? focus;
    Vector3 point, pointVelocity;
    float frame, frameVelocity;
    bool active, opening;
    // Ход облёта: 0 — камера игры, 1 — камера окна
    float progress;
    // Камера игры в начале облёта: откуда (вокруг точки, на которую смотрит окно), куда смотрела, угол обзора
    float startYawAngle, startPitch, startDistance, startFov;
    Vector3 startLook;

    /// <summary>Ход облёта: 0 — вид из камеры игры, 1 — вид окна</summary>
    public float Progress => progress;

    /// <summary>Камера вернулась туда, откуда взяла кадр: можно звать End</summary>
    public bool Returned => active && !opening && progress <= 0f;

    void Awake()
    {
        mainCamera = Camera.main;
        outfitCamera.enabled = false;
        overlayCamera.enabled = false;
        fill.enabled = false;
    }

    void OnEnable() => RenderPipelineManager.beginCameraRendering += BeginCameraRendering;

    void OnDisable() => RenderPipelineManager.beginCameraRendering -= BeginCameraRendering;

    // Overlay-камера стоит там же, где Main Camera (дочерняя), но угол обзора Cinemachine меняет только у Main
    // Camera: без копии персонаж поверх фона был бы другого размера, чем в мире
    void BeginCameraRendering(ScriptableRenderContext context, Camera camera)
    {
        if (camera != overlayCamera)
            return;
        overlayCamera.fieldOfView = mainCamera.fieldOfView;
        overlayCamera.nearClipPlane = mainCamera.nearClipPlane;
        overlayCamera.farClipPlane = mainCamera.farClipPlane;
    }

    /// <summary>Окно открылось: игрок переходит на слой Preview, камера окна облетает его от камеры игры к лицу</summary>
    public void Begin()
    {
        // Части снаряжения включаются и выключаются, пока окно открыто: отрисовщики берутся вместе с выключенными.
        // Объекты с коллайдером (кости рэгдолла) слой не меняют: от слоя зависят попадания и проверка земли
        renderers = target.GetComponentsInChildren<Renderer>(true);
        savedLayers = new int[renderers.Length];
        for (int i = 0; i < renderers.Length; i++)
        {
            var go = renderers[i].gameObject;
            savedLayers[i] = go.layer;
            if (go.GetComponent<Collider>() == null)
                go.layer = previewLayer;
        }
        savedMask = mainCamera.cullingMask;
        mainCamera.cullingMask &= ~(1 << previewLayer);

        focus = null;
        yaw = target.eulerAngles.y + 180f + startYaw;
        pitch = 6f;
        zoom = 1f;
        Aim(out point, out frame);
        pointVelocity = Vector3.zero;
        frameVelocity = 0f;

        // Камера игры — в тех же величинах, что и камера окна: поворот и наклон вокруг точки окна и расстояние до неё.
        // Смотрела она не на эту точку (игрок сбоку от центра кадра), поэтому запоминается и её точка взгляда
        var from = mainCamera.transform;
        var offset = point - from.position;
        startDistance = offset.magnitude;
        var dir = offset / startDistance;
        startYawAngle = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
        startPitch = -Mathf.Asin(dir.y) * Mathf.Rad2Deg;
        startLook = from.position + from.forward * startDistance;
        startFov = mainCamera.fieldOfView;

        progress = 0f;
        opening = true;
        active = true;
        Place();
        outfitCamera.enabled = true;
        overlayCamera.enabled = true;
        fill.enabled = true;
    }

    /// <summary>Окно закрывается: камера облетает игрока обратно к камере игры</summary>
    public void Release() => opening = false;

    /// <summary>Облёт обратно закончен: кадр снова у камеры игры, игрок на своих слоях</summary>
    public void End()
    {
        active = false;
        outfitCamera.enabled = false;
        overlayCamera.enabled = false;
        fill.enabled = false;
        mainCamera.cullingMask = savedMask;
        for (int i = 0; i < renderers.Length; i++)
            renderers[i].gameObject.layer = savedLayers[i];
    }

    /// <summary>Навести камеру на часть тела слота; null — весь рост</summary>
    public void Focus(GearSlot? slot)
    {
        focus = slot;
        zoom = 1f;
    }

    public void OnDrag(PointerEventData eventData)
    {
        yaw += eventData.delta.x * dragSpeed;
        pitch = Mathf.Clamp(pitch - eventData.delta.y * dragSpeed * 0.5f, -10f, 35f);
    }

    public void OnScroll(PointerEventData eventData)
    {
        zoom = Mathf.Clamp(zoom * (eventData.scrollDelta.y > 0f ? 1.15f : 1f / 1.15f), 0.6f, 2.5f);
    }

    // Камера окна ставится в Update: Cinemachine читает её положение в LateUpdate того же кадра
    void Update()
    {
        if (!active)
            return;
        float dt = Time.unscaledDeltaTime;
        // Подвисший кадр (загрузка, сборка мусора) не проглатывает облёт целиком: шаг не больше 0,05 с
        progress = Mathf.MoveTowards(progress, opening ? 1f : 0f, Mathf.Min(dt, 0.05f) / transitionTime);
        Aim(out var wantPoint, out var wantFrame);
        point = Vector3.SmoothDamp(point, wantPoint, ref pointVelocity, 0.18f, Mathf.Infinity, dt);
        frame = Mathf.SmoothDamp(frame, wantFrame, ref frameVelocity, 0.18f, Mathf.Infinity, dt);
        Place();
    }

    // Облёт: поворот, наклон, расстояние и угол обзора идут от камеры игры к камере окна, точка взгляда — от той,
    // куда смотрела камера игры, к игроку. Камера всё время на сфере вокруг игрока и смотрит на него
    void Place()
    {
        float e = Mathf.SmoothStep(0f, 1f, progress);
        float fov = Mathf.Lerp(startFov, fieldOfView, e);
        float viewDistance = frame * 0.5f / Mathf.Tan(fieldOfView * 0.5f * Mathf.Deg2Rad);
        float distance = Mathf.Lerp(startDistance, viewDistance, e);
        var orbit = Quaternion.Euler(Mathf.Lerp(startPitch, pitch, e), Mathf.LerpAngle(startYawAngle, yaw, e), 0f);
        var position = point - orbit * Vector3.forward * distance;
        var look = Vector3.Lerp(startLook, point, e);
        outfitCamera.transform.SetPositionAndRotation(position, Quaternion.LookRotation(look - position));
        var lens = outfitCamera.Lens;
        lens.FieldOfView = fov;
        outfitCamera.Lens = lens;
    }

    // Куда смотреть и сколько метров по высоте должно поместиться в кадр
    void Aim(out Vector3 at, out float height)
    {
        Vector3 Bone(HumanBodyBones bone) => animator.GetBoneTransform(bone).position;
        switch (focus)
        {
            case GearSlot.Head:
                at = Bone(HumanBodyBones.Head) + Vector3.up * 0.08f;
                height = 0.9f;
                break;
            case GearSlot.Chest:
                at = Bone(HumanBodyBones.Chest);
                height = 1.3f;
                break;
            case GearSlot.Hands:
                var left = Bone(HumanBodyBones.LeftHand);
                var right = Bone(HumanBodyBones.RightHand);
                at = (left + right) * 0.5f;
                height = Mathf.Max(1.3f, Vector3.Distance(left, right) + 0.6f);
                break;
            case GearSlot.Legs:
                at = Vector3.Lerp(Bone(HumanBodyBones.Hips), (Bone(HumanBodyBones.LeftFoot) + Bone(HumanBodyBones.RightFoot)) * 0.5f, 0.42f);
                height = 1.5f;
                break;
            case GearSlot.Feet:
                at = (Bone(HumanBodyBones.LeftFoot) + Bone(HumanBodyBones.RightFoot)) * 0.5f + Vector3.up * 0.2f;
                height = 1.05f;
                break;
            default:
                at = target.position + Vector3.up * 0.97f;
                height = 2.5f;
                break;
        }
        height /= zoom;
    }
}
