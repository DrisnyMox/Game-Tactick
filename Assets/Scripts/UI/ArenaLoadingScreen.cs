using UnityEngine;

/// <summary>
/// Экран загрузки на Canvas: закрывает игру, пока генерируется карта (VoxelArena.StartGeneration) и собирается
/// её NavMesh, и показывает шаг и общий процент. Полоса: первая доля generationShare — генерация карты
/// (VoxelArena.GenerationProgress), остальное — NavMesh (ArenaNavMesh.FullBuildProgress).
/// Пока экран открыт, игра стоит (Time.timeScale = 0): персонажи не падают сквозь недостроенную землю,
/// а боты не ищут путь без NavMesh. Открывается и на новую карту (G и кнопка «Новая карта»).
/// Canvas и надписи собирает Tools/Unity/SetupLoadingScreen.cs.
/// </summary>
public class ArenaLoadingScreen : MonoBehaviour
{
    /// <summary>
    /// Экран загрузки открыт. Надписи OnGUI (счёт, лента убийств, патроны) рисуются поверх любого Canvas,
    /// поэтому их скрипты сами не рисуют, пока он открыт
    /// </summary>
    public static bool IsShown { get; private set; }

    [SerializeField] VoxelArena arena;
    [SerializeField] ArenaNavMesh navMesh;
    [SerializeField] Canvas canvas;
    [Tooltip("Заполненная часть полосы: её правый край ставится по прогрессу")]
    [SerializeField] RectTransform barFill;
    [SerializeField] UnityEngine.UI.Text stageLabel;
    [Tooltip("Доля полосы на генерацию карты, остальное — на сборку NavMesh")]
    [SerializeField, Range(0f, 1f)] float generationShare = 0.3f;

    bool shown;
    float pausedTimeScale = 1f;
    string shownStage;
    int shownPercent = -1;

    void Awake()
    {
        // До первой генерации земли нет: экран с самого первого кадра, до первого шага физики
        Show(true);
    }

    void Update()
    {
        bool generating = arena.Layout == null || arena.IsGenerating;
        bool loading = generating || !navMesh.HasNavMesh;
        if (loading != shown)
            Show(loading);
        if (!loading)
            return;

        string stage;
        float progress;
        if (generating)
        {
            // До Start арены генерация ещё не началась
            stage = arena.GenerationStage ?? "Подготовка";
            progress = arena.GenerationProgress * generationShare;
        }
        else
        {
            stage = "NavMesh";
            progress = generationShare + (1f - generationShare) * navMesh.FullBuildProgress;
        }

        barFill.anchorMax = new Vector2(progress, 1f);

        // Текст пересобирается, только когда меняется: новая строка в каждом кадре выделяла бы память
        int percent = Mathf.FloorToInt(progress * 100f);
        if (percent == shownPercent && stage == shownStage)
            return;
        shownPercent = percent;
        shownStage = stage;
        stageLabel.text = $"{stage} · {percent}%";
    }

    void OnDestroy()
    {
        // Выход из Play Mode посреди загрузки: статический флаг и пауза не должны остаться следующему запуску
        // Canvas при выгрузке сцены уже может быть уничтожен: его не трогаем
        if (!shown)
            return;
        IsShown = false;
        Time.timeScale = pausedTimeScale;
    }

    void Show(bool value)
    {
        shown = value;
        IsShown = value;
        canvas.enabled = value;
        if (value)
        {
            pausedTimeScale = Time.timeScale;
            Time.timeScale = 0f;
        }
        else
        {
            Time.timeScale = pausedTimeScale;
        }
    }
}
