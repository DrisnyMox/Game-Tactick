using StarterAssets;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Меню настроек на Canvas. Открывается и закрывается по Esc (и кнопкой «Продолжить»).
/// Пока меню открыто, игра стоит (Time.timeScale = 0), ввод игрока выключен, курсор свободен.
/// Под экраном загрузки (ArenaLoadingScreen) меню не открывается: тот сам держит паузу.
///
/// Динамические тени: камера перестаёт рисовать карты теней (UniversalAdditionalCameraData.renderShadows),
/// свет и материалы не меняются. Выбор хранится в PlayerPrefs и ставится при запуске.
/// Canvas, переключатель и EventSystem собирает Tools/Unity/SetupSettingsMenu.cs.
/// </summary>
public class SettingsMenu : MonoBehaviour
{
    const string ShadowsKey = "Settings.DynamicShadows";

    /// <summary>
    /// Меню открыто. Надписи OnGUI рисуются поверх любого Canvas, поэтому их скрипты сами не рисуют,
    /// пока оно открыто; ArenaTestController не берёт клики и клавиши
    /// </summary>
    public static bool IsOpen { get; private set; }

    [SerializeField] Canvas canvas;
    [SerializeField] UnityEngine.UI.Toggle shadowsToggle;
    [SerializeField] UnityEngine.UI.Button resumeButton;
    [Header("Игрок")]
    [SerializeField] PlayerInput playerInput;
    [SerializeField] StarterAssetsInputs playerInputs;
    [SerializeField] SurvivorShooter playerShooter;

    UniversalAdditionalCameraData cameraData;
    float pausedTimeScale = 1f;

    // Play Mode входит без перезагрузки домена (Enter Play Mode Settings): флаг прошлого запуска сам не обнуляется
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => IsOpen = false;

    void Awake()
    {
        cameraData = Camera.main.GetUniversalAdditionalCameraData();

        bool shadows = PlayerPrefs.GetInt(ShadowsKey, 1) == 1;
        shadowsToggle.SetIsOnWithoutNotify(shadows);
        ApplyShadows(shadows);
        shadowsToggle.onValueChanged.AddListener(ShadowsToggle_Changed);
        resumeButton.onClick.AddListener(Close);

        canvas.enabled = false;
    }

    void OnDestroy()
    {
        // Выход из Play Mode при открытом меню: флаг и пауза не должны остаться следующему запуску
        if (!IsOpen)
            return;
        IsOpen = false;
        Time.timeScale = pausedTimeScale;
    }

    void Update()
    {
        if (!Keyboard.current.escapeKey.wasPressedThisFrame)
            return;
        if (IsOpen)
            Close();
        // Под окном базы или итогом вылазки меню не открывается: они сами держат ввод игрока
        else if (!ArenaLoadingScreen.IsShown && !LoopGui.ModalOpen && LoopGui.ClosedFrame != Time.frameCount)
            Open();
    }

    void Open()
    {
        IsOpen = true;
        canvas.enabled = true;
        pausedTimeScale = Time.timeScale;
        Time.timeScale = 0f;

        // Ввод игрока выключен: клик по меню не стреляет, мышь не крутит камеру.
        // Зажатые кнопки сбрасываются, иначе после меню персонаж шёл бы и стрелял сам
        playerInput.DeactivateInput();
        playerInputs.MoveInput(Vector2.zero);
        playerInputs.LookInput(Vector2.zero);
        playerInputs.JumpInput(false);
        playerInputs.SprintInput(false);
        playerShooter.AimInput = false;
        playerShooter.FireInput = false;

        // StarterAssetsInputs снова захватывает курсор, когда окно получает фокус: пока меню открыто, не даём
        playerInputs.cursorLocked = false;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    void Close()
    {
        IsOpen = false;
        canvas.enabled = false;
        Time.timeScale = pausedTimeScale;

        playerInput.ActivateInput();
        playerInputs.cursorLocked = true;
        Cursor.lockState = CursorLockMode.Locked;
    }

    void ShadowsToggle_Changed(bool value)
    {
        ApplyShadows(value);
        PlayerPrefs.SetInt(ShadowsKey, value ? 1 : 0);
        PlayerPrefs.Save();
    }

    void ApplyShadows(bool value) => cameraData.renderShadows = value;
}
