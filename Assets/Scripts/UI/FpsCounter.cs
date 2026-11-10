using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Счётчик кадров на Canvas: FPS, среднее время кадра и самый долгий кадр за последние updateInterval секунд.
/// Самый долгий кадр показывает рывки (перестройка чанков, NavMesh), которые среднее сглаживает.
/// Цвет: зелёный от goodFps, жёлтый от okFps, ниже - красный.
/// Canvas и текст собирает Tools/Unity/SetupFpsCounter.cs.
/// </summary>
public class FpsCounter : MonoBehaviour
{
    [SerializeField] Text label;
    [SerializeField] float updateInterval = 0.5f;
    [SerializeField] float goodFps = 55f;
    [SerializeField] float okFps = 30f;

    int frames;
    float elapsed;
    float worst;

    void Update()
    {
        float dt = Time.unscaledDeltaTime;
        frames++;
        elapsed += dt;
        worst = Mathf.Max(worst, dt);
        if (elapsed < updateInterval)
            return;

        float fps = frames / elapsed;
        label.text = $"FPS {fps:0}   кадр {elapsed / frames * 1000f:0.0} мс   худший {worst * 1000f:0.0} мс";
        label.color = fps >= goodFps ? new Color(0.5f, 1f, 0.5f) : fps >= okFps ? new Color(1f, 0.9f, 0.4f) : new Color(1f, 0.45f, 0.4f);

        frames = 0;
        elapsed = 0f;
        worst = 0f;
    }
}
