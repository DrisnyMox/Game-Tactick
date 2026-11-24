using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

// Окно верстака в сцене Base:
//  1. иконки припасов в Assets/Loop/UI/Icons (Supply_<предмет>.png): рисуются здесь силуэтами цвета предмета
//     (Items.Tint) — моделей у патронов, аптечки и магазина нет. Иконки снаряжения — из SetupEquipment.cs,
//     его надо запустить раньше (и спрайты окна Assets/Loop/UI/Sprites тоже его);
//  2. Canvas «Workbench Canvas» с WorkbenchScreen: тёмный фон, слева рецепты, справа выбранный рецепт, сверху
//     уровень ремесла, поверх — карточка результата;
//  3. окно связывается с BaseHub (поле workbench): E у верстака открывает его. Сцену сохраняет.
// Повторный запуск ничего не дублирует: Canvas собирается заново, файлы перезаписываются.
// Запуск: unity command run_script --file Tools/Unity/SetupWorkbench.cs
public static class SetupWorkbench
{
    const string ScenePath = "Assets/Scenes/Base.unity";
    const string SpriteFolder = "Assets/Loop/UI/Sprites/";
    const string IconFolder = "Assets/Loop/UI/Icons/";
    const string DisplayFontPath = "Assets/ThirdParty/Invector/Basic Locomotion/Resources/BebasKai-Regular.otf";
    const string CanvasName = "Workbench Canvas";
    const int IconSize = 256;
    const float ListWidth = 560f;

    static readonly Color Accent = new Color32(0x3C, 0xCF, 0xC4, 0xFF);
    static readonly Color Card = new Color32(0x12, 0x1A, 0x20, 0xFF);
    static readonly Color Panel = new Color32(0x0F, 0x16, 0x1B, 0xFF);
    static readonly Color Frame = new Color32(0x22, 0x30, 0x38, 0xFF);
    static readonly Color IconBack = new Color32(0x0B, 0x11, 0x15, 0xFF);
    static readonly Color Muted = new Color32(0x8F, 0xA0, 0xA8, 0xFF);
    static readonly Color Light = new Color32(0xC9, 0xD3, 0xD8, 0xFF);
    static readonly Color Caption = new Color32(0x6F, 0xB4, 0xAE, 0xFF);
    static readonly Color Track = new Color32(0x1E, 0x29, 0x30, 0xFF);

    static Sprite rounded, outline, glow;
    static Font display, body;

    public static string Main()
    {
        var log = new StringBuilder();
        var active = EditorSceneManager.GetActiveScene();
        if (active.isDirty)
            throw new System.InvalidOperationException($"{active.path}: есть несохранённые изменения");
        rounded = AssetDatabase.LoadAssetAtPath<Sprite>(SpriteFolder + "Rounded.png");
        outline = AssetDatabase.LoadAssetAtPath<Sprite>(SpriteFolder + "Outline.png");
        glow = AssetDatabase.LoadAssetAtPath<Sprite>(SpriteFolder + "Glow.png");
        if (rounded == null || outline == null || glow == null)
            throw new System.InvalidOperationException($"нет спрайтов окна в {SpriteFolder}: сначала SetupEquipment.cs");
        display = AssetDatabase.LoadAssetAtPath<Font>(DisplayFontPath);
        body = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        var gearIcons = LoadGearIcons();
        var supplyIcons = MakeSupplyIcons(log);

        var scene = EditorSceneManager.OpenScene(ScenePath);
        var old = GameObject.Find(CanvasName);
        if (old != null)
            Object.DestroyImmediate(old);
        var hub = Object.FindAnyObjectByType<BaseHub>();
        var hubSo = new SerializedObject(hub);
        var player = (GameObject)hubSo.FindProperty("player").objectReferenceValue;

        var screen = BuildCanvas(player, gearIcons, supplyIcons);
        hubSo.FindProperty("workbench").objectReferenceValue = screen;
        hubSo.ApplyModifiedPropertiesWithoutUndo();

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        log.AppendLine($"{CanvasName} в {ScenePath}, связан с BaseHub, игрок {player.name}");
        return log.ToString();
    }

    // ---------- Иконки ----------

    static Sprite[] LoadGearIcons()
    {
        var icons = new Sprite[Gear.All.Length * Gear.QualityCount];
        foreach (var kind in Gear.All)
            for (int q = 0; q < Gear.QualityCount; q++)
            {
                var path = $"{IconFolder}{Gear.Model(kind)}_{(GearQuality)q}.png";
                icons[(int)kind * Gear.QualityCount + q] = AssetDatabase.LoadAssetAtPath<Sprite>(path)
                    ?? throw new System.InvalidOperationException($"нет иконки {path}: сначала SetupEquipment.cs");
            }
        return icons;
    }

    // Силуэт: расстояние со знаком до фигуры в пикселях (меньше нуля — внутри) и тон заливки по месту
    delegate float Shape(Vector2 p, out float tone);

    static Sprite[] MakeSupplyIcons(StringBuilder log)
    {
        var icons = new Sprite[Items.Count];
        foreach (var recipe in Recipe.All)
        {
            var id = recipe.result;
            icons[(int)id] = SaveIcon("Supply_" + id, Items.Tint(id), SupplyShape(id));
            log.AppendLine($"иконка припасов {id}");
        }
        return icons;
    }

    static Shape SupplyShape(ItemId id) => id switch
    {
        // Три патрона: гильза и пуля темнее
        ItemId.Ammo => (Vector2 p, out float tone) =>
        {
            float d = float.MaxValue;
            tone = 1f;
            for (int i = -1; i <= 1; i++)
            {
                var c = new Vector2(128f + i * 52f, 0f);
                float casing = RoundRect(p, c + new Vector2(0f, 104f), new Vector2(20f, 52f), 4f);
                float tip = Capsule(p, c + new Vector2(0f, 168f), c + new Vector2(0f, 186f), 20f);
                float rim = RoundRect(p, c + new Vector2(0f, 52f), new Vector2(23f, 6f), 2f);
                float bullet = Mathf.Min(casing, Mathf.Min(tip, rim));
                if (bullet < d)
                {
                    d = bullet;
                    tone = tip < casing && tip < rim ? 0.72f : 1f;
                }
            }
            return d;
        },
        // Коробка с крестом: крест светлее
        ItemId.Medkit => (Vector2 p, out float tone) =>
        {
            float box = RoundRect(p, new Vector2(128f, 120f), new Vector2(92f, 74f), 18f);
            float handle = Mathf.Max(RoundRect(p, new Vector2(128f, 204f), new Vector2(36f, 22f), 10f),
                                     -RoundRect(p, new Vector2(128f, 200f), new Vector2(22f, 12f), 4f));
            float cross = Mathf.Min(RoundRect(p, new Vector2(128f, 120f), new Vector2(14f, 44f), 3f),
                                    RoundRect(p, new Vector2(128f, 120f), new Vector2(44f, 14f), 3f));
            tone = cross < 0f ? 1.9f : 1f;
            return Mathf.Min(box, handle);
        },
        // Корпус с рёбрами, запал и кольцо
        ItemId.Grenade => (Vector2 p, out float tone) =>
        {
            float shell = RoundRect(p, new Vector2(128f, 104f), new Vector2(62f, 76f), 54f);
            float fuse = RoundRect(p, new Vector2(128f, 194f), new Vector2(26f, 16f), 4f);
            float lever = RoundRect(p, new Vector2(164f, 176f), new Vector2(10f, 40f), 5f);
            float ring = Mathf.Abs((p - new Vector2(92f, 214f)).magnitude - 18f) - 5f;
            bool groove = Mathf.Abs(Mathf.Repeat(p.y - 70f, 34f) - 17f) < 3f || Mathf.Abs(p.x - 128f) < 3f;
            tone = shell < 0f && groove ? 0.7f : fuse < 0f || lever < 0f ? 0.8f : 1f;
            return Mathf.Min(Mathf.Min(shell, fuse), Mathf.Min(lever, ring));
        },
        // Жилет: вырез горловины и проймы
        ItemId.Armor => (Vector2 p, out float tone) =>
        {
            float vest = RoundRect(p, new Vector2(128f, 120f), new Vector2(82f, 96f), 22f);
            float neck = (p - new Vector2(128f, 226f)).magnitude - 44f;
            float armL = (p - new Vector2(30f, 176f)).magnitude - 40f;
            float armR = (p - new Vector2(226f, 176f)).magnitude - 40f;
            bool plate = RoundRect(p, new Vector2(128f, 104f), new Vector2(54f, 46f), 8f) < 0f;
            tone = plate ? 1.25f : 1f;
            return Mathf.Max(vest, -Mathf.Min(neck, Mathf.Min(armL, armR)));
        },
        // Изогнутый магазин: прямоугольник, наклон растёт вниз
        _ => (Vector2 p, out float tone) =>
        {
            float bend = (200f - p.y) / 160f;
            var q = new Vector2(p.x - 128f + bend * bend * 46f, p.y);
            float mag = RoundRect(q, new Vector2(0f, 118f), new Vector2(40f, 88f), 10f);
            float lip = RoundRect(p, new Vector2(128f, 212f), new Vector2(30f, 12f), 4f);
            bool rib = Mathf.Abs(Mathf.Repeat(p.y - 50f, 36f) - 18f) < 3f;
            tone = rib ? 0.75f : 1f;
            return Mathf.Min(mag, lip);
        },
    };

    static float RoundRect(Vector2 p, Vector2 center, Vector2 half, float radius)
    {
        var q = new Vector2(Mathf.Abs(p.x - center.x), Mathf.Abs(p.y - center.y)) - half + Vector2.one * radius;
        return Vector2.Max(q, Vector2.zero).magnitude + Mathf.Min(Mathf.Max(q.x, q.y), 0f) - radius;
    }

    static float Capsule(Vector2 p, Vector2 a, Vector2 b, float radius)
    {
        var pa = p - a;
        var ba = b - a;
        float h = Mathf.Clamp01(Vector2.Dot(pa, ba) / ba.sqrMagnitude);
        return (pa - ba * h).magnitude - radius;
    }

    // Заливка цветом предмета: светлее сверху, тёмная кромка по краю, мягкий край в пиксель
    static Sprite SaveIcon(string name, Color tint, Shape shape)
    {
        var tex = new Texture2D(IconSize, IconSize, TextureFormat.RGBA32, false);
        var pixels = new Color[IconSize * IconSize];
        for (int y = 0; y < IconSize; y++)
            for (int x = 0; x < IconSize; x++)
            {
                float d = shape(new Vector2(x + 0.5f, y + 0.5f), out float tone);
                float alpha = Mathf.Clamp01(0.5f - d);
                float light = Mathf.Lerp(0.78f, 1.12f, y / (float)IconSize) * tone;
                var c = new Color(Mathf.Clamp01(tint.r * light), Mathf.Clamp01(tint.g * light), Mathf.Clamp01(tint.b * light));
                // Кромка: тёмная полоса у края, силуэт читается на тёмной рамке иконки
                float rim = Mathf.Clamp01((d + 6f) / 3f);
                c = Color.Lerp(c, c * 0.45f, rim);
                pixels[y * IconSize + x] = new Color(c.r, c.g, c.b, alpha);
            }
        tex.SetPixels(pixels);
        var path = IconFolder + name + ".png";
        File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        AssetDatabase.ImportAsset(path);
        var importer = (TextureImporter)AssetImporter.GetAtPath(path);
        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.alphaIsTransparency = true;
        importer.mipmapEnabled = true;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.maxTextureSize = IconSize;
        importer.SaveAndReimport();
        return AssetDatabase.LoadAssetAtPath<Sprite>(path);
    }

    // ---------- Canvas ----------

    static WorkbenchScreen BuildCanvas(GameObject player, Sprite[] gearIcons, Sprite[] supplyIcons)
    {
        var root = new GameObject(CanvasName, typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster), typeof(CanvasGroup));
        var canvas = root.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        // Как Equipment Canvas: под меню настроек (80), загрузкой (90) и FPS (100)
        canvas.sortingOrder = 70;
        var scaler = root.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;
        var rootRect = (RectTransform)root.transform;

        // Фон: окно закрывает базу почти целиком, остров чуть просвечивает
        var backdrop = NewImage("Backdrop", rootRect, new Color(0.035f, 0.055f, 0.07f, 0.95f), null);
        Stretch(backdrop.rectTransform);
        backdrop.raycastTarget = true;
        var halo = NewImage("Glow", rootRect, new Color(Accent.r, Accent.g, Accent.b, 0.09f), glow);
        Place(halo.rectTransform, new Vector2(0.62f, 0.45f), Vector2.zero, new Vector2(1500f, 1500f));

        var window = new GameObject("Window", typeof(RectTransform)).GetComponent<RectTransform>();
        window.SetParent(rootRect, false);
        Stretch(window);

        // Заголовок
        var bar = NewImage("Accent", window, Accent, rounded);
        Place(bar.rectTransform, new Vector2(0f, 1f), new Vector2(98f, -74f), new Vector2(56f, 5f));
        var title = Label("Title", window, display, "ВЕРСТАК", 88, Color.white, TextAnchor.UpperLeft);
        Place(title.rectTransform, new Vector2(0f, 1f), new Vector2(96f, -82f), new Vector2(700f, 96f));
        var subtitle = Label("Subtitle", window, body, "Материалы берутся со склада. Цвет качества снаряжения выпадает случайно", 26, Muted, TextAnchor.UpperLeft);
        Place(subtitle.rectTransform, new Vector2(0f, 1f), new Vector2(98f, -176f), new Vector2(1000f, 36f));

        var close = NewButton("Close Button", window, "ЗАКРЫТЬ   E", 30, false);
        Place((RectTransform)close.transform, new Vector2(1f, 1f), new Vector2(-96f, -80f), new Vector2(230f, 60f));

        // Уровень комплекта выбранной части: блок целиком скрыт, когда выбраны припасы
        const float levelRight = -96f - 230f - 48f;
        var levelBlock = new GameObject("Level", typeof(RectTransform)).GetComponent<RectTransform>();
        levelBlock.SetParent(window, false);
        Stretch(levelBlock);
        var levelLabel = Label("Level Label", levelBlock, display, "", 36, Color.white, TextAnchor.UpperRight);
        Place(levelLabel.rectTransform, new Vector2(1f, 1f), new Vector2(levelRight, -76f), new Vector2(800f, 42f));
        var levelTrack = NewImage("Level Track", levelBlock, Track, rounded);
        Place(levelTrack.rectTransform, new Vector2(1f, 1f), new Vector2(levelRight, -124f), new Vector2(560f, 10f));
        levelTrack.pixelsPerUnitMultiplier = 8f;
        var levelFill = FillBar(levelTrack.rectTransform, Accent);
        var levelXp = Label("Level Xp", levelBlock, body, "", 20, Muted, TextAnchor.UpperRight);
        Place(levelXp.rectTransform, new Vector2(1f, 1f), new Vector2(levelRight, -142f), new Vector2(700f, 28f));

        // Список рецептов
        var scroll = new GameObject("List", typeof(RectTransform), typeof(ScrollRect)).GetComponent<ScrollRect>();
        scroll.transform.SetParent(window, false);
        var scrollRect = (RectTransform)scroll.transform;
        scrollRect.anchorMin = new Vector2(0f, 0f);
        scrollRect.anchorMax = new Vector2(0f, 1f);
        scrollRect.pivot = new Vector2(0f, 1f);
        scrollRect.offsetMin = new Vector2(96f, 96f);
        scrollRect.offsetMax = new Vector2(96f + ListWidth, -240f);
        var viewport = NewImage("Viewport", scrollRect, new Color(0f, 0f, 0f, 0f), null);
        Stretch(viewport.rectTransform);
        viewport.raycastTarget = true;
        viewport.gameObject.AddComponent<RectMask2D>();
        var content = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter)).GetComponent<RectTransform>();
        content.SetParent(viewport.rectTransform, false);
        content.anchorMin = new Vector2(0f, 1f);
        content.anchorMax = new Vector2(1f, 1f);
        content.pivot = new Vector2(0.5f, 1f);
        content.offsetMin = content.offsetMax = Vector2.zero;
        var contentLayout = content.GetComponent<VerticalLayoutGroup>();
        contentLayout.spacing = 10f;
        contentLayout.childControlWidth = true;
        contentLayout.childForceExpandWidth = true;
        contentLayout.childControlHeight = false;
        contentLayout.childForceExpandHeight = false;
        content.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        scroll.viewport = viewport.rectTransform;
        scroll.content = content;
        scroll.horizontal = false;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 30f;
        var header = Label("Header", content, display, "СНАРЯЖЕНИЕ", 28, Caption, TextAnchor.LowerLeft);
        header.rectTransform.sizeDelta = new Vector2(ListWidth, 46f);
        var card = BuildCard(content);

        // Выбранный рецепт
        var details = NewImage("Details", window, Panel, rounded);
        var detailsRect = details.rectTransform;
        detailsRect.anchorMin = Vector2.zero;
        detailsRect.anchorMax = Vector2.one;
        detailsRect.offsetMin = new Vector2(96f + ListWidth + 40f, 96f);
        detailsRect.offsetMax = new Vector2(-96f, -240f);
        var detailsFrame = NewImage("Frame", detailsRect, Frame, outline);
        Stretch(detailsFrame.rectTransform);

        var iconFrame = NewImage("Icon Frame", detailsRect, IconBack, rounded);
        Place(iconFrame.rectTransform, new Vector2(0f, 1f), new Vector2(32f, -32f), new Vector2(144f, 144f));
        var detailIcon = NewImage("Icon", iconFrame.rectTransform, Color.white, null);
        Place(detailIcon.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(134f, 134f));
        detailIcon.preserveAspect = true;
        var detailName = Label("Name", detailsRect, display, "", 60, Color.white, TextAnchor.UpperLeft);
        Place(detailName.rectTransform, new Vector2(0f, 1f), new Vector2(208f, -30f), new Vector2(820f, 66f));
        FitLine(detailName, 36);
        var detailInfo = Label("Info", detailsRect, body, "", 24, Caption, TextAnchor.UpperLeft);
        Place(detailInfo.rectTransform, new Vector2(0f, 1f), new Vector2(210f, -96f), new Vector2(820f, 32f));
        var detailAbout = Label("About", detailsRect, body, "", 23, Light, TextAnchor.UpperLeft);
        Place(detailAbout.rectTransform, new Vector2(0f, 1f), new Vector2(210f, -132f), new Vector2(820f, 60f));
        detailAbout.horizontalOverflow = HorizontalWrapMode.Wrap;

        var divider = NewImage("Divider", detailsRect, Frame, null);
        TopStretch(divider.rectTransform, -204f, 2f);

        var needCaption = Label("Need Caption", detailsRect, display, "НУЖНО", 26, Caption, TextAnchor.UpperLeft);
        Place(needCaption.rectTransform, new Vector2(0f, 1f), new Vector2(36f, -222f), new Vector2(300f, 30f));
        var costRow = new GameObject("Cost", typeof(RectTransform), typeof(HorizontalLayoutGroup)).GetComponent<RectTransform>();
        costRow.SetParent(detailsRect, false);
        Place(costRow, new Vector2(0f, 1f), new Vector2(36f, -258f), new Vector2(940f, 58f));
        var costLayout = costRow.GetComponent<HorizontalLayoutGroup>();
        costLayout.spacing = 14f;
        costLayout.childControlWidth = costLayout.childControlHeight = false;
        costLayout.childForceExpandWidth = costLayout.childForceExpandHeight = false;
        var costViews = new WorkbenchCostView[3];
        for (int i = 0; i < costViews.Length; i++)
            costViews[i] = BuildCost(costRow);

        // Цвет качества (снаряжение)
        var quality = new GameObject("Quality", typeof(RectTransform)).GetComponent<RectTransform>();
        quality.SetParent(detailsRect, false);
        TopStretch(quality, -344f, 150f);
        var qualityCaption = Label("Caption", quality, display, "ЦВЕТ КАЧЕСТВА", 26, Caption, TextAnchor.UpperLeft);
        Place(qualityCaption.rectTransform, new Vector2(0f, 1f), Vector2.zero, new Vector2(400f, 30f));
        var segmentsRow = new GameObject("Chances", typeof(RectTransform), typeof(HorizontalLayoutGroup)).GetComponent<RectTransform>();
        segmentsRow.SetParent(quality, false);
        TopStretch(segmentsRow, -42f, 18f, 0f);
        var segmentsLayout = segmentsRow.GetComponent<HorizontalLayoutGroup>();
        segmentsLayout.spacing = 3f;
        segmentsLayout.childControlWidth = segmentsLayout.childControlHeight = true;
        segmentsLayout.childForceExpandWidth = false;
        segmentsLayout.childForceExpandHeight = true;
        var legendRow = new GameObject("Legend", typeof(RectTransform), typeof(HorizontalLayoutGroup)).GetComponent<RectTransform>();
        legendRow.SetParent(quality, false);
        TopStretch(legendRow, -76f, 76f, 0f);
        var legendLayout = legendRow.GetComponent<HorizontalLayoutGroup>();
        legendLayout.childControlWidth = legendLayout.childControlHeight = true;
        legendLayout.childForceExpandWidth = legendLayout.childForceExpandHeight = true;
        const int shown = Gear.QualityCount;
        var segments = new LayoutElement[shown];
        var percents = new Text[shown];
        for (int i = 0; i < shown; i++)
        {
            var q = (GearQuality)i;
            var color = Gear.QualityColor(q);
            var segment = NewImage(q + " Chance", segmentsRow, color, null);
            segments[i] = segment.gameObject.AddComponent<LayoutElement>();
            segments[i].flexibleWidth = 1f;

            var column = new GameObject(q + " Legend", typeof(RectTransform)).GetComponent<RectTransform>();
            column.SetParent(legendRow, false);
            var swatch = NewImage("Swatch", column, color, rounded);
            Place(swatch.rectTransform, new Vector2(0f, 1f), new Vector2(0f, -6f), new Vector2(14f, 14f));
            swatch.pixelsPerUnitMultiplier = 6f;
            var name = Label("Name", column, body, Gear.QualityName(q), 21, Light, TextAnchor.UpperLeft);
            Place(name.rectTransform, new Vector2(0f, 1f), new Vector2(22f, 0f), new Vector2(180f, 28f));
            percents[i] = Label("Percent", column, display, "", 38, color, TextAnchor.UpperLeft);
            Place(percents[i].rectTransform, new Vector2(0f, 1f), new Vector2(0f, -30f), new Vector2(120f, 44f));
        }
        // Прокачка — в строке заголовка справа: окно невысокое, под шансами место занимает кнопка
        var masteryTrack = NewImage("Mastery Track", quality, Track, rounded);
        Place(masteryTrack.rectTransform, new Vector2(1f, 1f), new Vector2(0f, -12f), new Vector2(160f, 8f));
        var mastery = Label("Mastery", quality, body, "", 21, Muted, TextAnchor.UpperRight);
        Place(mastery.rectTransform, new Vector2(1f, 1f), new Vector2(-176f, 0f), new Vector2(800f, 30f));
        masteryTrack.pixelsPerUnitMultiplier = 8f;
        var masteryFill = FillBar(masteryTrack.rectTransform, Accent);

        // Что получится (припасы)
        var supply = new GameObject("Supply", typeof(RectTransform)).GetComponent<RectTransform>();
        supply.SetParent(detailsRect, false);
        TopStretch(supply, -344f, 150f);
        var supplyCaption = Label("Caption", supply, display, "ПОЛУЧИШЬ", 26, Caption, TextAnchor.UpperLeft);
        Place(supplyCaption.rectTransform, new Vector2(0f, 1f), Vector2.zero, new Vector2(400f, 30f));
        var supplyYield = Label("Yield", supply, display, "", 50, Color.white, TextAnchor.UpperLeft);
        Place(supplyYield.rectTransform, new Vector2(0f, 1f), new Vector2(0f, -40f), new Vector2(900f, 58f));
        var supplyNote = Label("Note", supply, body, "Ложится на склад. В вылазку припасы уходят из рюкзака — переложи их на складе.", 22, Muted, TextAnchor.UpperLeft);
        Place(supplyNote.rectTransform, new Vector2(0f, 1f), new Vector2(0f, -108f), new Vector2(900f, 60f));
        supplyNote.horizontalOverflow = HorizontalWrapMode.Wrap;

        var make = NewButton("Make Button", detailsRect, "СДЕЛАТЬ", 38, true);
        Place((RectTransform)make.transform, new Vector2(0f, 0f), new Vector2(36f, 36f), new Vector2(340f, 78f));
        var reason = Label("Reason", detailsRect, body, "", 22, Muted, TextAnchor.MiddleLeft);
        Place(reason.rectTransform, new Vector2(0f, 0f), new Vector2(400f, 75f), new Vector2(680f, 40f), new Vector2(0f, 0.5f));

        var footer = Label("Footer", window, body, "E  или  Esc  —  закрыть", 22, Muted, TextAnchor.LowerLeft);
        Place(footer.rectTransform, new Vector2(0f, 0f), new Vector2(98f, 44f), new Vector2(500f, 30f));
        var stash = Label("Stash", window, body, "", 22, Light, TextAnchor.LowerRight);
        Place(stash.rectTransform, new Vector2(1f, 0f), new Vector2(-96f, 44f), new Vector2(1100f, 30f));

        // Результат крафта: поверх окна, фон под ним ловит нажатия
        var result = NewImage("Result", rootRect, new Color(0.02f, 0.03f, 0.04f, 0.72f), null);
        Stretch(result.rectTransform);
        result.raycastTarget = true;
        var resultGroup = result.gameObject.AddComponent<CanvasGroup>();
        var resultGlow = NewImage("Glow", result.rectTransform, Color.white, glow);
        Place(resultGlow.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0f, 40f), new Vector2(1100f, 1100f));
        var resultCard = NewImage("Card", result.rectTransform, Panel, rounded);
        Place(resultCard.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(640f, 600f));
        var resultFrame = NewImage("Frame", resultCard.rectTransform, Frame, outline);
        Stretch(resultFrame.rectTransform);
        var cardRect = resultCard.rectTransform;
        var made = Label("Caption", cardRect, display, "СДЕЛАНО", 30, Caption, TextAnchor.UpperCenter);
        Place(made.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -32f), new Vector2(560f, 36f));
        var resultIconFrame = NewImage("Icon Frame", cardRect, IconBack, rounded);
        Place(resultIconFrame.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -80f), new Vector2(220f, 220f));
        var resultIcon = NewImage("Icon", resultIconFrame.rectTransform, Color.white, null);
        Place(resultIcon.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(208f, 208f));
        resultIcon.preserveAspect = true;
        var resultName = Label("Name", cardRect, display, "", 52, Color.white, TextAnchor.UpperCenter);
        Place(resultName.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -318f), new Vector2(580f, 58f));
        FitLine(resultName, 32);
        var resultQuality = Label("Quality", cardRect, display, "", 44, Color.white, TextAnchor.UpperCenter);
        Place(resultQuality.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -374f), new Vector2(580f, 50f));
        var resultNote = Label("Note", cardRect, body, "", 22, Muted, TextAnchor.UpperCenter);
        Place(resultNote.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -428f), new Vector2(580f, 30f));
        var resultLevel = Label("Level", cardRect, display, "", 26, Accent, TextAnchor.UpperCenter);
        Place(resultLevel.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -464f), new Vector2(580f, 32f));
        var ok = NewButton("Ok Button", cardRect, "ДАЛЬШЕ", 32, true);
        Place((RectTransform)ok.transform, new Vector2(0.5f, 0f), new Vector2(-130f, 32f), new Vector2(240f, 64f), new Vector2(0.5f, 0f));
        var again = NewButton("Again Button", cardRect, "ЕЩЁ РАЗ", 30, false);
        Place((RectTransform)again.transform, new Vector2(0.5f, 0f), new Vector2(130f, 32f), new Vector2(240f, 64f), new Vector2(0.5f, 0f));

        var screen = root.AddComponent<WorkbenchScreen>();
        var so = new SerializedObject(screen);
        so.FindProperty("canvas").objectReferenceValue = canvas;
        so.FindProperty("group").objectReferenceValue = root.GetComponent<CanvasGroup>();
        so.FindProperty("window").objectReferenceValue = window;
        so.FindProperty("player").objectReferenceValue = player;
        so.FindProperty("closeButton").objectReferenceValue = close;
        so.FindProperty("levelBlock").objectReferenceValue = levelBlock.gameObject;
        so.FindProperty("levelLabel").objectReferenceValue = levelLabel;
        so.FindProperty("levelBar").objectReferenceValue = levelFill;
        so.FindProperty("levelXp").objectReferenceValue = levelXp;
        so.FindProperty("headerTemplate").objectReferenceValue = header;
        so.FindProperty("cardTemplate").objectReferenceValue = card;
        so.FindProperty("detailIcon").objectReferenceValue = detailIcon;
        so.FindProperty("detailName").objectReferenceValue = detailName;
        so.FindProperty("detailInfo").objectReferenceValue = detailInfo;
        so.FindProperty("detailAbout").objectReferenceValue = detailAbout;
        SetArray(so.FindProperty("costViews"), costViews);
        so.FindProperty("qualitySection").objectReferenceValue = quality.gameObject;
        SetArray(so.FindProperty("qualitySegments"), segments);
        SetArray(so.FindProperty("qualityPercents"), percents);
        so.FindProperty("masteryLabel").objectReferenceValue = mastery;
        so.FindProperty("masteryBar").objectReferenceValue = masteryFill;
        so.FindProperty("supplySection").objectReferenceValue = supply.gameObject;
        so.FindProperty("supplyYield").objectReferenceValue = supplyYield;
        so.FindProperty("makeButton").objectReferenceValue = make;
        so.FindProperty("reasonLabel").objectReferenceValue = reason;
        so.FindProperty("stashLabel").objectReferenceValue = stash;
        so.FindProperty("resultGroup").objectReferenceValue = resultGroup;
        so.FindProperty("resultCard").objectReferenceValue = cardRect;
        so.FindProperty("resultGlow").objectReferenceValue = resultGlow;
        so.FindProperty("resultIcon").objectReferenceValue = resultIcon;
        so.FindProperty("resultName").objectReferenceValue = resultName;
        so.FindProperty("resultQuality").objectReferenceValue = resultQuality;
        so.FindProperty("resultNote").objectReferenceValue = resultNote;
        so.FindProperty("resultLevel").objectReferenceValue = resultLevel;
        so.FindProperty("resultOk").objectReferenceValue = ok;
        so.FindProperty("resultAgain").objectReferenceValue = again;
        SetArray(so.FindProperty("gearIcons"), gearIcons);
        SetArray(so.FindProperty("supplyIcons"), supplyIcons);
        so.ApplyModifiedPropertiesWithoutUndo();
        return screen;
    }

    static WorkbenchCardView BuildCard(RectTransform parent)
    {
        var back = NewImage("Card", parent, Card, rounded);
        back.rectTransform.sizeDelta = new Vector2(ListWidth, 96f);
        back.raycastTarget = true;
        var button = back.gameObject.AddComponent<Button>();
        Tint(button, back);
        var selection = NewImage("Selection", back.rectTransform, Accent, outline);
        Stretch(selection.rectTransform);
        selection.enabled = false;
        var iconBack = NewImage("Icon Frame", back.rectTransform, IconBack, rounded);
        Place(iconBack.rectTransform, new Vector2(0f, 0.5f), new Vector2(18f, 0f), new Vector2(74f, 74f), new Vector2(0f, 0.5f));
        var icon = NewImage("Icon", iconBack.rectTransform, Color.white, null);
        Place(icon.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(68f, 68f));
        icon.preserveAspect = true;
        var name = Label("Name", back.rectTransform, body, "", 26, Color.white, TextAnchor.LowerLeft);
        Place(name.rectTransform, new Vector2(0f, 0.5f), new Vector2(110f, 0f), new Vector2(270f, 34f), new Vector2(0f, 0f));
        FitLine(name, 18);
        var info = Label("Info", back.rectTransform, body, "", 20, Muted, TextAnchor.UpperLeft);
        Place(info.rectTransform, new Vector2(0f, 0.5f), new Vector2(110f, -4f), new Vector2(290f, 28f), new Vector2(0f, 1f));
        var status = NewImage("Status", back.rectTransform, Accent, rounded);
        Place(status.rectTransform, new Vector2(1f, 0.5f), new Vector2(-16f, 0f), new Vector2(150f, 30f), new Vector2(1f, 0.5f));
        status.pixelsPerUnitMultiplier = 2f;
        var statusLabel = Label("Label", status.rectTransform, display, "", 20, Accent, TextAnchor.MiddleCenter);
        Stretch(statusLabel.rectTransform);
        statusLabel.rectTransform.offsetMin = new Vector2(0f, -1f);

        var view = back.gameObject.AddComponent<WorkbenchCardView>();
        var so = new SerializedObject(view);
        so.FindProperty("button").objectReferenceValue = button;
        so.FindProperty("icon").objectReferenceValue = icon;
        so.FindProperty("nameLabel").objectReferenceValue = name;
        so.FindProperty("infoLabel").objectReferenceValue = info;
        so.FindProperty("statusBack").objectReferenceValue = status;
        so.FindProperty("statusLabel").objectReferenceValue = statusLabel;
        so.FindProperty("selection").objectReferenceValue = selection;
        so.ApplyModifiedPropertiesWithoutUndo();
        return view;
    }

    static WorkbenchCostView BuildCost(RectTransform parent)
    {
        var back = NewImage("Material", parent, Card, rounded);
        back.rectTransform.sizeDelta = new Vector2(300f, 58f);
        var dot = NewImage("Dot", back.rectTransform, Color.white, rounded);
        Place(dot.rectTransform, new Vector2(0f, 0.5f), new Vector2(20f, 0f), new Vector2(18f, 18f), new Vector2(0f, 0.5f));
        dot.pixelsPerUnitMultiplier = 6f;
        var name = Label("Name", back.rectTransform, body, "", 23, Light, TextAnchor.MiddleLeft);
        Place(name.rectTransform, new Vector2(0f, 0.5f), new Vector2(50f, 0f), new Vector2(150f, 40f), new Vector2(0f, 0.5f));
        var amount = Label("Amount", back.rectTransform, display, "", 32, Color.white, TextAnchor.MiddleRight);
        Place(amount.rectTransform, new Vector2(1f, 0.5f), new Vector2(-18f, -1f), new Vector2(120f, 40f), new Vector2(1f, 0.5f));

        var view = back.gameObject.AddComponent<WorkbenchCostView>();
        var so = new SerializedObject(view);
        so.FindProperty("dot").objectReferenceValue = dot;
        so.FindProperty("nameLabel").objectReferenceValue = name;
        so.FindProperty("amountLabel").objectReferenceValue = amount;
        so.ApplyModifiedPropertiesWithoutUndo();
        return view;
    }

    // Заливка полосы: дочерняя плашка во всю высоту, ширину по доле задаёт WorkbenchScreen
    static Image FillBar(RectTransform track, Color color)
    {
        var fill = NewImage("Fill", track, color, rounded);
        Stretch(fill.rectTransform);
        fill.pixelsPerUnitMultiplier = 8f;
        return fill;
    }

    static void SetArray<T>(SerializedProperty list, T[] items) where T : Object
    {
        list.arraySize = items.Length;
        for (int i = 0; i < items.Length; i++)
            list.GetArrayElementAtIndex(i).objectReferenceValue = items[i];
    }

    // Наведение светлее, нажатие темнее; выключенная кнопка тусклая (как в окне снаряжения)
    static void Tint(Selectable selectable, Graphic target)
    {
        selectable.targetGraphic = target;
        var colors = selectable.colors;
        colors.colorMultiplier = 1.4f;
        colors.normalColor = new Color(0.715f, 0.715f, 0.715f, 1f);
        colors.highlightedColor = new Color(1f, 1f, 1f, 1f);
        colors.selectedColor = new Color(0.86f, 0.86f, 0.86f, 1f);
        colors.pressedColor = new Color(0.6f, 0.6f, 0.6f, 1f);
        colors.disabledColor = new Color(0.5f, 0.5f, 0.5f, 0.45f);
        colors.fadeDuration = 0.08f;
        selectable.colors = colors;
    }

    // Кнопка: primary — заливка цветом Accent с тёмной надписью, иначе тёмная плашка с рамкой
    static Button NewButton(string name, RectTransform parent, string text, int size, bool primary)
    {
        var back = NewImage(name, parent, primary ? Accent : Card, rounded);
        back.raycastTarget = true;
        var button = back.gameObject.AddComponent<Button>();
        Tint(button, back);
        if (!primary)
        {
            var frame = NewImage("Frame", back.rectTransform, new Color32(0x2B, 0x3B, 0x45, 0xFF), outline);
            Stretch(frame.rectTransform);
        }
        var label = Label("Label", back.rectTransform, display, text, size, primary ? new Color32(0x06, 0x1A, 0x19, 0xFF) : Color.white, TextAnchor.MiddleCenter);
        Stretch(label.rectTransform);
        label.rectTransform.offsetMin = new Vector2(0f, -2f);
        return button;
    }

    static Image NewImage(string name, Transform parent, Color color, Sprite sprite)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var image = go.GetComponent<Image>();
        image.sprite = sprite;
        if (sprite != null && sprite.border != Vector4.zero)
            image.type = Image.Type.Sliced;
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    static Text Label(string name, Transform parent, Font font, string value, int size, Color color, TextAnchor alignment)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Text));
        go.transform.SetParent(parent, false);
        var text = go.GetComponent<Text>();
        text.font = font;
        text.text = value;
        text.fontSize = size;
        text.color = color;
        text.alignment = alignment;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.raycastTarget = false;
        return text;
    }

    // Длинное имя уменьшается, чтобы влезть в одну строку
    static void FitLine(Text text, int minSize)
    {
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Truncate;
        text.resizeTextForBestFit = true;
        text.resizeTextMinSize = minSize;
        text.resizeTextMaxSize = text.fontSize;
    }

    static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
    }

    // Полоса во всю ширину родителя с отступом inset по бокам: верх на top от верха родителя, высота height
    static void TopStretch(RectTransform rect, float top, float height, float inset = 36f)
    {
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.offsetMin = new Vector2(inset, top - height);
        rect.offsetMax = new Vector2(-inset, top);
    }

    // Якорь и опора в одной точке (по умолчанию), сдвиг от якоря и размер
    static void Place(RectTransform rect, Vector2 anchor, Vector2 position, Vector2 size, Vector2? pivot = null)
    {
        rect.anchorMin = rect.anchorMax = anchor;
        rect.pivot = pivot ?? anchor;
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
    }
}
