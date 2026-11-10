using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

// Ставит в открытую сцену счётчик кадров: Canvas «FPS Canvas» (поверх экрана) с текстом в левом верхнем углу
// и компонентом FpsCounter. Если в сцене нет EventSystem, он не нужен: счётчик ничего не нажимает.
// Повторный запуск ничего не дублирует. Сцену сохраняет.
// Запуск: unity command run_script --file Tools/Unity/SetupFpsCounter.cs --project-path "D:\Game Tactick"
public static class SetupFpsCounter
{
    const string CanvasName = "FPS Canvas";

    public static string Main()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var old = GameObject.Find(CanvasName);
        if (old != null)
            Object.DestroyImmediate(old);

        var canvasObject = new GameObject(CanvasName, typeof(Canvas), typeof(CanvasScaler));
        var canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;
        // Размер текста растёт с экраном так же, как надписи OnGUI (от высоты экрана)
        var scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 1f;

        var textObject = new GameObject("FPS", typeof(RectTransform), typeof(Text), typeof(Shadow));
        textObject.transform.SetParent(canvasObject.transform, false);
        var rect = textObject.GetComponent<RectTransform>();
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = new Vector2(20f, -16f);
        rect.sizeDelta = new Vector2(900f, 50f);

        var text = textObject.GetComponent<Text>();
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.fontSize = 32;
        text.alignment = TextAnchor.UpperLeft;
        text.raycastTarget = false;
        text.text = "FPS";
        textObject.GetComponent<Shadow>().effectDistance = new Vector2(2f, -2f);

        var counter = canvasObject.AddComponent<FpsCounter>();
        var so = new SerializedObject(counter);
        so.FindProperty("label").objectReferenceValue = text;
        so.ApplyModifiedPropertiesWithoutUndo();

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        return $"{CanvasName} added to {scene.path}";
    }
}
