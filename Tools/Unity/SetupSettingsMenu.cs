using StarterAssets;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

// Ставит в открытую сцену меню настроек: Canvas «Settings Canvas» (под экраном загрузки и счётчиком кадров)
// с затемнением, панелью, заголовком, переключателем «Динамические тени» и кнопкой «Продолжить»,
// компонент SettingsMenu, связанный с персонажем игрока (ArenaPlayerSpawn.player), и EventSystem
// с InputSystemUIInputModule, если его нет: без него Canvas не получает клики.
// Повторный запуск ничего не дублирует. Сцену сохраняет.
// Запуск: unity command run_script --file Tools/Unity/SetupSettingsMenu.cs
public static class SetupSettingsMenu
{
    const string CanvasName = "Settings Canvas";

    static Sprite uiSprite;
    static Sprite checkmark;
    static Font font;

    public static string Main()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var old = GameObject.Find(CanvasName);
        if (old != null)
            Object.DestroyImmediate(old);

        uiSprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
        checkmark = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Checkmark.psd");
        font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        var spawn = Object.FindAnyObjectByType<ArenaPlayerSpawn>();
        var player = (CharacterHealth)new SerializedObject(spawn).FindProperty("player").objectReferenceValue;

        var canvasObject = new GameObject(CanvasName, typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        var canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        // Loading Canvas — 90, FPS Canvas — 100
        canvas.sortingOrder = 80;
        var scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 1f;

        // Затемнение на весь экран: игра на паузе видна за меню, клики мимо панели не уходят в игру
        var dim = CreateImage("Dim", canvasObject.transform, new Color(0f, 0f, 0f, 0.6f), null);
        Stretch(dim.rectTransform);
        dim.raycastTarget = true;

        var panel = CreateImage("Panel", canvasObject.transform, new Color32(0x14, 0x16, 0x1B, 0xF2), uiSprite);
        Place(panel.rectTransform, Vector2.zero, new Vector2(720f, 400f));

        var title = CreateText("Title", panel.transform, "Настройки", 52, Color.white, TextAnchor.MiddleCenter);
        Place(title.rectTransform, new Vector2(0f, 135f), new Vector2(680f, 80f));

        var toggle = CreateToggle(panel.transform, "Динамические тени");
        Place((RectTransform)toggle.transform, new Vector2(0f, 20f), new Vector2(520f, 56f));

        var resume = CreateButton(panel.transform, "Продолжить");
        Place((RectTransform)resume.transform, new Vector2(0f, -120f), new Vector2(320f, 64f));

        var menu = canvasObject.AddComponent<SettingsMenu>();
        var so = new SerializedObject(menu);
        so.FindProperty("canvas").objectReferenceValue = canvas;
        so.FindProperty("shadowsToggle").objectReferenceValue = toggle;
        so.FindProperty("resumeButton").objectReferenceValue = resume;
        so.FindProperty("playerInput").objectReferenceValue = player.GetComponent<PlayerInput>();
        so.FindProperty("playerInputs").objectReferenceValue = player.GetComponent<StarterAssetsInputs>();
        so.FindProperty("playerShooter").objectReferenceValue = player.GetComponent<SurvivorShooter>();
        so.ApplyModifiedPropertiesWithoutUndo();

        string eventSystem = "EventSystem уже был";
        if (Object.FindAnyObjectByType<EventSystem>() == null)
        {
            new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
            eventSystem = "EventSystem добавлен";
        }

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        return $"{CanvasName} added to {scene.path}, игрок {player.name}, {eventSystem}";
    }

    static Toggle CreateToggle(Transform parent, string label)
    {
        var go = new GameObject("Shadows Toggle", typeof(RectTransform), typeof(Toggle));
        go.transform.SetParent(parent, false);

        // Квадрат слева, галочка внутри него, подпись справа
        var box = CreateImage("Background", go.transform, new Color32(0x2A, 0x2E, 0x36, 0xFF), uiSprite);
        var boxRect = box.rectTransform;
        boxRect.anchorMin = boxRect.anchorMax = boxRect.pivot = new Vector2(0f, 0.5f);
        boxRect.anchoredPosition = Vector2.zero;
        boxRect.sizeDelta = new Vector2(48f, 48f);
        box.raycastTarget = true;

        var check = CreateImage("Checkmark", box.transform, new Color32(0x6F, 0xA8, 0xFF, 0xFF), checkmark);
        Stretch(check.rectTransform);
        check.rectTransform.offsetMin = new Vector2(4f, 4f);
        check.rectTransform.offsetMax = new Vector2(-4f, -4f);

        var text = CreateText("Label", go.transform, label, 34, new Color32(0xE6, 0xE9, 0xEF, 0xFF), TextAnchor.MiddleLeft);
        var textRect = text.rectTransform;
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(68f, 0f);
        textRect.offsetMax = Vector2.zero;
        // Подпись тоже нажимается
        text.raycastTarget = true;

        var toggle = go.GetComponent<Toggle>();
        toggle.targetGraphic = box;
        toggle.graphic = check;
        toggle.isOn = true;
        return toggle;
    }

    static Button CreateButton(Transform parent, string label)
    {
        var image = CreateImage("Resume Button", parent, new Color32(0x2F, 0x5F, 0xB0, 0xFF), uiSprite);
        image.raycastTarget = true;
        var button = image.gameObject.AddComponent<Button>();
        button.targetGraphic = image;

        var text = CreateText("Label", image.transform, label, 34, Color.white, TextAnchor.MiddleCenter);
        Stretch(text.rectTransform);
        return button;
    }

    static Image CreateImage(string name, Transform parent, Color color, Sprite sprite)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var image = go.GetComponent<Image>();
        image.sprite = sprite;
        if (sprite != null)
            image.type = Image.Type.Sliced;
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    static Text CreateText(string name, Transform parent, string value, int size, Color color, TextAnchor alignment)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Text));
        go.transform.SetParent(parent, false);
        var text = go.GetComponent<Text>();
        text.font = font;
        text.fontSize = size;
        text.alignment = alignment;
        text.color = color;
        text.raycastTarget = false;
        text.text = value;
        return text;
    }

    static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
    }

    static void Place(RectTransform rect, Vector2 position, Vector2 size)
    {
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
    }
}
