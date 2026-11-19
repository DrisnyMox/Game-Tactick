using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

// Ставит в открытую сцену экран загрузки: Canvas «Loading Canvas» поверх игры (под счётчиком кадров, чтобы FPS
// был виден и во время загрузки) с непрозрачным фоном, заголовком, полосой прогресса и строкой шага,
// и компонент ArenaLoadingScreen, связанный с VoxelArena и ArenaNavMesh сцены. Кнопок нет, EventSystem не нужен.
// Повторный запуск ничего не дублирует. Сцену сохраняет.
// Запуск: unity command run_script --file Tools/Unity/SetupLoadingScreen.cs
public static class SetupLoadingScreen
{
    const string CanvasName = "Loading Canvas";

    public static string Main()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var old = GameObject.Find(CanvasName);
        if (old != null)
            Object.DestroyImmediate(old);

        var arena = Object.FindAnyObjectByType<VoxelArena>();
        var navMesh = arena.GetComponent<ArenaNavMesh>();

        var canvasObject = new GameObject(CanvasName, typeof(Canvas), typeof(CanvasScaler));
        var canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        // FPS Canvas — 100
        canvas.sortingOrder = 90;
        // Как у FPS Canvas: размер растёт с высотой экрана
        var scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 1f;

        // Фон на весь экран: недостроенную карту не видно
        var background = CreateImage("Background", canvasObject.transform, new Color32(0x14, 0x16, 0x1B, 0xFF));
        var backgroundRect = background.rectTransform;
        backgroundRect.anchorMin = Vector2.zero;
        backgroundRect.anchorMax = Vector2.one;
        backgroundRect.offsetMin = backgroundRect.offsetMax = Vector2.zero;

        CreateText("Title", canvasObject.transform, "Загрузка карты", 56, new Vector2(0f, 70f), Color.white);

        var bar = CreateImage("Bar", canvasObject.transform, new Color32(0x2A, 0x2E, 0x36, 0xFF));
        var barRect = bar.rectTransform;
        barRect.anchorMin = barRect.anchorMax = barRect.pivot = new Vector2(0.5f, 0.5f);
        barRect.sizeDelta = new Vector2(900f, 20f);

        // Заполнение растянуто по полосе от левого края; ArenaLoadingScreen двигает правый край (anchorMax.x)
        var fill = CreateImage("Fill", bar.transform, new Color32(0x6F, 0xA8, 0xFF, 0xFF));
        var fillRect = fill.rectTransform;
        fillRect.anchorMin = Vector2.zero;
        fillRect.anchorMax = new Vector2(0f, 1f);
        fillRect.offsetMin = fillRect.offsetMax = Vector2.zero;

        var stage = CreateText("Stage", canvasObject.transform, "Подготовка · 0%", 32, new Vector2(0f, -50f),
            new Color32(0xC8, 0xCD, 0xD6, 0xFF));

        var screen = canvasObject.AddComponent<ArenaLoadingScreen>();
        var so = new SerializedObject(screen);
        so.FindProperty("arena").objectReferenceValue = arena;
        so.FindProperty("navMesh").objectReferenceValue = navMesh;
        so.FindProperty("canvas").objectReferenceValue = canvas;
        so.FindProperty("barFill").objectReferenceValue = fillRect;
        so.FindProperty("stageLabel").objectReferenceValue = stage;
        so.ApplyModifiedPropertiesWithoutUndo();

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        return $"{CanvasName} added to {scene.path}";
    }

    static Image CreateImage(string name, Transform parent, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var image = go.GetComponent<Image>();
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    static Text CreateText(string name, Transform parent, string value, int size, Vector2 position, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Text));
        go.transform.SetParent(parent, false);
        var rect = go.GetComponent<RectTransform>();
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = position;
        rect.sizeDelta = new Vector2(1200f, size * 1.5f);

        var text = go.GetComponent<Text>();
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.fontSize = size;
        text.alignment = TextAnchor.MiddleCenter;
        text.color = color;
        text.raycastTarget = false;
        text.text = value;
        return text;
    }
}
