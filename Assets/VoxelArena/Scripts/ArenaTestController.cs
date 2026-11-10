using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

/// <summary>
/// Тестовое управление ареной: камера, взрывы, агенты, замеры на экране.
/// Мышь: ЛКМ клик — взрыв, ЛКМ тянуть — сдвиг камеры, ПКМ — отправить агентов, колесо — зум.
/// Клавиши: WASD или стрелки — сдвиг, Q/E — поворот, G — новая карта.
/// Телефон: тап — взрыв, палец тянуть — сдвиг, два пальца — зум.
///
/// Ввод — через пакет Input System: в этом проекте старый UnityEngine.Input отключён
/// (Player Settings → Active Input Handling = Input System Package), его вызовы бросали бы исключение.
/// </summary>
[RequireComponent(typeof(VoxelArena), typeof(ArenaNavMesh))]
public class ArenaTestController : MonoBehaviour
{
    // Windows и Linux отдают 120 единиц прокрутки на один щелчок колеса (старый Input давал 1)
    const float ScrollUnitsPerNotch = 120f;

    [SerializeField] float explosionRadius = 2.5f;
    [SerializeField] int agentCount = 6;
    [SerializeField] float pitch = 55f;
    [SerializeField] float yaw = 0f;
    [SerializeField] float dragThresholdPixels = 12f;

    VoxelArena arena;
    ArenaNavMesh navMesh;
    Camera cam;

    Vector3 pivot;
    float distance;
    float minDistance;
    float maxDistance;

    readonly List<NavMeshAgent> agents = new();
    bool agentsGoToTwo = true;

    bool pressActive;
    bool dragging;
    Vector3 pressPosition;
    Vector3 lastPointer;
    float lastPinch;

    float fps;
    int lastRemoved;
    string lastClick = "—";
    string pathInfo = "—";
    bool pathsChecked;
    Rect panelRect;

    void Awake()
    {
        arena = GetComponent<VoxelArena>();
        navMesh = GetComponent<ArenaNavMesh>();
        arena.Generated += Arena_Generated;
    }

    void Start()
    {
        cam = Camera.main;

        var bounds = arena.WorldBounds;
        maxDistance = Mathf.Max(bounds.size.x, bounds.size.z) * 1.2f;
        minDistance = 4f;
        cam.farClipPlane = Mathf.Max(cam.farClipPlane, maxDistance * 3f);

        // Порядок Start у компонентов не задан: если арена ещё не сгенерирована,
        // камеру наведёт событие Generated
        if (arena.Layout != null)
            FocusOwnBase();
    }

    void OnDestroy()
    {
        arena.Generated -= Arena_Generated;
    }

    void Arena_Generated()
    {
        pathsChecked = false;
        pathInfo = "ждём NavMesh";
        if (cam != null)
            FocusOwnBase();
    }

    // Туман в цвет неба биома: дальний край карты растворяется, а не обрывается
    void ApplyFog()
    {
        var color = ArenaBiomes.FogColor(arena.Biome);
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Linear;
        RenderSettings.fogColor = color;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = color;
    }

    // Своя база внизу экрана, противник — вверху
    void FocusOwnBase()
    {
        ApplyFog();
        var axis = arena.Layout.baseAxis;
        yaw = Mathf.Atan2(axis.x, axis.y) * Mathf.Rad2Deg;
        pivot = arena.BaseOne + new Vector3(axis.x, 0f, axis.y) * 20f;
        distance = 45f;
    }

    void Update()
    {
        fps = Mathf.Lerp(fps, 1f / Mathf.Max(Time.unscaledDeltaTime, 0.0001f), 0.05f);

        HandlePinch();
        HandlePointer();
        HandleKeys();

        if (agents.Count == 0 && navMesh.HasNavMesh && !navMesh.IsBusy)
            SpawnAgents();

        if (!pathsChecked && navMesh.HasNavMesh && !navMesh.IsBusy)
            CheckPaths();
    }

    // Проверка по настоящему NavMesh: от ворот к воротам противника и к каждой точке захвата
    void CheckPaths()
    {
        pathsChecked = true;
        var path = new NavMeshPath();
        // Тип агента указываем явно: без фильтра CalculatePath предупреждает, что не может его определить
        var filter = new NavMeshQueryFilter { agentTypeID = navMesh.AgentTypeID, areaMask = NavMesh.AllAreas };

        string baseStatus = PathStatus(arena.GateOne, arena.GateTwo, path, filter);

        int complete = 0, total = 0;
        foreach (var resource in arena.Layout.resources)
        {
            if (resource.kind != ResourceKind.CapturePoint)
                continue;
            total++;
            // Край кольца точки захвата: центр занят рудным бугром
            var target = arena.CellTop(resource.cell + new Unity.Mathematics.int2(3, 0));
            if (PathStatus(arena.GateOne, target, path, filter) == "полный")
                complete++;
        }

        pathInfo = $"база→база {baseStatus}, точки захвата {complete}/{total}";
        Debug.Log($"[Arena] NavMesh: путь {pathInfo}");
    }

    static string PathStatus(Vector3 from, Vector3 to, NavMeshPath path, NavMeshQueryFilter filter)
    {
        if (!NavMesh.SamplePosition(from, out var a, 3f, filter))
            return "нет навмеша у старта";
        if (!NavMesh.SamplePosition(to, out var b, 3f, filter))
            return "нет навмеша у цели";
        if (!NavMesh.CalculatePath(a.position, b.position, filter, path))
            return "не найден";
        return path.status == NavMeshPathStatus.PathComplete ? "полный" : "частичный";
    }

    void LateUpdate()
    {
        var rotation = Quaternion.Euler(pitch, yaw, 0f);
        cam.transform.SetPositionAndRotation(pivot - rotation * Vector3.forward * distance, rotation);

        // Туман начинается за точкой, куда смотрит камера, и зависит от зума
        RenderSettings.fogStartDistance = distance * 1.2f;
        RenderSettings.fogEndDistance = distance * 2.8f;
    }

    // ---- Ввод (Input System) ----

    // Сколько пальцев сейчас на экране
    static int ActiveTouchCount()
    {
        var screen = Touchscreen.current;
        if (screen == null)
            return 0;

        int count = 0;
        var touches = screen.touches;
        for (int i = 0; i < touches.Count; i++)
        {
            if (touches[i].press.isPressed)
                count++;
        }

        return count;
    }

    // Позиции двух пальцев; false, если пальцев не ровно два
    static bool TryGetTwoTouches(out Vector2 first, out Vector2 second)
    {
        first = default;
        second = default;

        var screen = Touchscreen.current;
        if (screen == null)
            return false;

        int found = 0;
        var touches = screen.touches;
        for (int i = 0; i < touches.Count; i++)
        {
            if (!touches[i].press.isPressed)
                continue;

            var position = touches[i].position.ReadValue();
            if (found == 0)
                first = position;
            else if (found == 1)
                second = position;
            found++;
        }

        return found == 2;
    }

    // Основная кнопка указателя: ЛКМ у мыши, касание у экрана
    static ButtonControl PrimaryButton(Pointer pointer)
    {
        return pointer is Mouse mouse ? mouse.leftButton : pointer.press;
    }

    static float Axis(bool negative, bool positive)
    {
        return (positive ? 1f : 0f) - (negative ? 1f : 0f);
    }

    void HandlePointer()
    {
        if (ActiveTouchCount() > 1)
        {
            // Два пальца — это зум, тап не засчитываем
            dragging = true;
            return;
        }

        // Pointer.current — последнее использованное устройство указки (мышь или экран)
        var device = Pointer.current;
        if (device == null)
            return;

        Vector3 pointer = device.position.ReadValue();
        var primary = PrimaryButton(device);

        if (primary.wasPressedThisFrame)
        {
            pressActive = !InPanel(pointer);
            dragging = false;
            pressPosition = pointer;
            lastPointer = pointer;
        }

        if (pressActive && primary.isPressed)
        {
            if (!dragging && (pointer - pressPosition).magnitude > dragThresholdPixels)
                dragging = true;

            if (dragging)
                Pan(pointer - lastPointer);

            lastPointer = pointer;
        }

        if (pressActive && primary.wasReleasedThisFrame)
        {
            if (!dragging)
                ExplodeAt(pointer);
            else
                lastClick = "перетаскивание камеры, не взрыв";

            pressActive = false;
        }

        var mouse = Mouse.current;
        if (mouse == null)
            return;

        if (mouse.rightButton.wasPressedThisFrame && RaycastArena(pointer, out var hit))
            SendAgents(hit.point);

        float scroll = mouse.scroll.ReadValue().y / ScrollUnitsPerNotch;
        if (scroll != 0f)
            distance = Mathf.Clamp(distance * (1f - scroll * 0.1f), minDistance, maxDistance);
    }

    void HandlePinch()
    {
        if (!TryGetTwoTouches(out var first, out var second))
        {
            lastPinch = 0f;
            return;
        }

        float pinch = (first - second).magnitude;
        if (lastPinch > 0f && pinch > 0f)
            distance = Mathf.Clamp(distance * lastPinch / pinch, minDistance, maxDistance);

        lastPinch = pinch;
    }

    void HandleKeys()
    {
        var keyboard = Keyboard.current;
        if (keyboard == null)
            return;

        var move = new Vector2(
            Axis(keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed,
                 keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed),
            Axis(keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed,
                 keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed));

        if (move != Vector2.zero)
        {
            GetFlatAxes(out var right, out var forward);
            pivot += (right * move.x + forward * move.y) * (distance * 0.8f * Time.deltaTime);
            ClampPivot();
        }

        if (keyboard.qKey.isPressed) yaw += 90f * Time.deltaTime;
        if (keyboard.eKey.isPressed) yaw -= 90f * Time.deltaTime;

        if (keyboard.gKey.wasPressedThisFrame)
            Regenerate();
    }

    void Pan(Vector3 screenDelta)
    {
        GetFlatAxes(out var right, out var forward);
        float scale = distance / Screen.height * 1.5f;
        pivot -= (right * screenDelta.x + forward * screenDelta.y) * scale;
        ClampPivot();
    }

    void GetFlatAxes(out Vector3 right, out Vector3 forward)
    {
        right = cam.transform.right;
        right.y = 0f;
        right.Normalize();
        forward = Vector3.Cross(right, Vector3.up);
    }

    void ClampPivot()
    {
        var bounds = arena.PlayableBounds;
        pivot.x = Mathf.Clamp(pivot.x, bounds.min.x, bounds.max.x);
        pivot.z = Mathf.Clamp(pivot.z, bounds.min.z, bounds.max.z);
    }

    bool RaycastArena(Vector3 screenPoint, out RaycastHit hit)
    {
        var ray = cam.ScreenPointToRay(screenPoint);
        return Physics.Raycast(ray, out hit, 1000f) && hit.collider.transform.IsChildOf(arena.transform);
    }

    void ExplodeAt(Vector3 screenPoint)
    {
        if (!RaycastArena(screenPoint, out var hit))
        {
            lastClick = "мимо арены";
            return;
        }

        // Центр чуть внутри поверхности, чтобы воронка была и вглубь
        var center = hit.point - hit.normal * (arena.VoxelSize * 0.5f);
        lastRemoved = arena.Explode(center, explosionRadius);
        lastClick = hit.collider.name;
    }

    void Regenerate()
    {
        foreach (var agent in agents)
            Destroy(agent.gameObject);
        agents.Clear();

        arena.Seed = Random.Range(1, 100000);
        arena.Generate();
    }

    void SpawnAgents()
    {
        for (int i = 0; i < agentCount; i++)
        {
            var offset = Random.insideUnitCircle * 3f;
            var point = arena.BaseOne + new Vector3(offset.x, 0f, offset.y);

            if (!NavMesh.SamplePosition(point, out var navHit, 4f, NavMesh.AllAreas))
                continue;

            var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = $"Test Agent {i}";
            Destroy(go.GetComponent<Collider>());
            go.transform.position = navHit.position;

            var agent = go.AddComponent<NavMeshAgent>();
            agent.agentTypeID = navMesh.AgentTypeID;
            agent.speed = 4f;
            agent.SetDestination(agentsGoToTwo ? arena.BaseTwo : arena.BaseOne);
            agents.Add(agent);
        }
    }

    void SendAgents(Vector3 target)
    {
        foreach (var agent in agents)
            agent.SetDestination(target);
    }

    void ToggleAgentsTarget()
    {
        agentsGoToTwo = !agentsGoToTwo;
        SendAgents(agentsGoToTwo ? arena.BaseTwo : arena.BaseOne);
    }

    bool InPanel(Vector3 screenPoint)
    {
        return panelRect.Contains(new Vector2(screenPoint.x, Screen.height - screenPoint.y));
    }

    void OnGUI()
    {
        int fontSize = Mathf.Clamp(Mathf.Min(Screen.width, Screen.height) / 55, 10, 28);
        GUI.skin.label.fontSize = fontSize;
        GUI.skin.button.fontSize = fontSize;
        GUI.skin.label.wordWrap = false;

        // Высота панели считается по содержимому, чтобы кнопки не обрезались
        var area = new Rect(10, 10, Screen.width - 20, Screen.height - 20);
        GUILayout.BeginArea(area);
        GUILayout.BeginVertical(GUI.skin.box, GUILayout.Width(fontSize * 22));

        var dims = arena.Dims;
        GUILayout.Label($"FPS: {fps:0}");
        GUILayout.Label($"Арена {dims.x}x{dims.y}x{dims.z}, воксель {arena.VoxelSize}");
        GUILayout.Label($"Сид {arena.Seed}, биом: {ArenaBiomes.DisplayName(arena.Biome)}, генерация {arena.LastGenerateMs:0} мс");
        GUILayout.Label($"Треугольников: {arena.TotalTriangles}, в форме для коллайдера и NavMesh: {arena.TotalShapeTriangles}");
        GUILayout.Label($"Перестроено чанков: {arena.LastRebuiltChunks}, выбито: {lastRemoved}");
        GUILayout.Label($"  меш (джобы): {arena.LastMeshMs:0.00} мс");
        GUILayout.Label($"  меш + коллайдер: {arena.LastApplyMs:0.00} мс");
        GUILayout.Label($"NavMesh: {navMesh.LastBuildMs:0.0} мс за {navMesh.LastBuildFrames} кадр., плитка {navMesh.TileSize}");
        GUILayout.Label($"Путь: {pathInfo}");
        GUILayout.Label($"Клик: {lastClick}");

        if (GUILayout.Button("Новая карта"))
            Regenerate();

        if (GUILayout.Button("Агенты к другой базе"))
            ToggleAgentsTarget();

        GUILayout.EndVertical();

        if (Event.current.type == EventType.Repaint)
        {
            var panel = GUILayoutUtility.GetLastRect();
            panelRect = new Rect(panel.x + area.x, panel.y + area.y, panel.width, panel.height);
        }

        GUILayout.EndArea();
    }
}
