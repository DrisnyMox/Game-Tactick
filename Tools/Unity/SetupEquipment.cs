using System.IO;
using System.Linq;
using System.Text;
using Unity.Cinemachine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

// Окно снаряжения в сцене Base:
//  1. слой Preview (7): пока окно открыто, игрок переходит на него, Main Camera его не рисует, рисует Overlay-камера;
//  2. спрайты окна в Assets/Loop/UI/Sprites: скруглённая плашка, рамка, мягкое свечение (рисуются здесь);
//  3. иконки частей в Assets/Loop/UI/Icons (<часть>_<цвет качества>.png): каждая часть с варианта
//     PlayerSurvivor8b8745d5 снимается одна в позе покоя на прозрачном фоне в каждом цвете качества, какой у неё
//     бывает (в пустой сцене, поэтому открытая сцена не должна быть изменённой);
//  4. камеры окна: CinemachineCamera «Outfit Camera» (приоритет выше камеры игры, мгновенный переход
//     в Assets/Loop/UI/OutfitBlends.asset у CinemachineBrain: облёт игрока делает OutfitPreview) с подсветкой и Overlay-камера «Outfit Overlay Camera»
//     в стеке Main Camera;
//  5. Canvas «Equipment Backdrop Canvas» (Screen Space - Camera: тёмный фон рисует Main Camera, игрок поверх него)
//     и Canvas «Equipment Canvas» с EquipmentScreen, связанные с игроком из BaseHub. Сцену сохраняет.
// Повторный запуск ничего не дублирует: камеры и Canvas собираются заново, файлы перезаписываются.
// Запуск: unity command run_script --file Tools/Unity/SetupEquipment.cs
public static class SetupEquipment
{
    const string ScenePath = "Assets/Scenes/Base.unity";
    const string VariantPath = "Assets/Characters/Survivor/PlayerSurvivor8b8745d5.prefab";
    const string SpriteFolder = "Assets/Loop/UI/Sprites/";
    const string IconFolder = "Assets/Loop/UI/Icons/";
    const string DisplayFontPath = "Assets/ThirdParty/Invector/Basic Locomotion/Resources/BebasKai-Regular.otf";
    const string CanvasName = "Equipment Canvas";
    const string BackdropName = "Equipment Backdrop Canvas";
    const string OutfitCameraName = "Outfit Camera";
    const string OverlayCameraName = "Outfit Overlay Camera";
    const string BlendsPath = "Assets/Loop/UI/OutfitBlends.asset";
    // Метка «любая камера» в переходах CinemachineBlenderSettings (у пакета она internal)
    const string AnyCamera = "**ANY CAMERA**";
    const int PreviewLayer = 7;
    const int IconSize = 256;

    static readonly Color Accent = new Color32(0x3C, 0xCF, 0xC4, 0xFF);
    static readonly Color Risk = new(1f, 0.55f, 0.45f);
    static readonly Color Card = new Color32(0x12, 0x1A, 0x20, 0xFF);
    static readonly Color IconBack = new Color32(0x0B, 0x11, 0x15, 0xFF);
    static readonly Color Muted = new Color32(0x8F, 0xA0, 0xA8, 0xFF);
    static readonly Color SlotCaption = new Color32(0x6F, 0xB4, 0xAE, 0xFF);

    static Sprite rounded, outline, glow;
    static Font display, body;

    public static string Main()
    {
        var log = new StringBuilder();
        AddLayer(log);
        MakeSprites();
        var icons = MakeIcons(log);

        var scene = EditorSceneManager.OpenScene(ScenePath);
        foreach (var name in new[] { CanvasName, BackdropName, OutfitCameraName })
        {
            var old = GameObject.Find(name);
            if (old != null)
                Object.DestroyImmediate(old);
        }
        display = AssetDatabase.LoadAssetAtPath<Font>(DisplayFontPath);
        body = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        var hub = Object.FindAnyObjectByType<BaseHub>();
        var player = (GameObject)new SerializedObject(hub).FindProperty("player").objectReferenceValue;
        var cameras = BuildCameras(log);
        BuildCanvas(player, icons, cameras);

        if (Object.FindAnyObjectByType<EventSystem>() == null)
            new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        log.AppendLine($"{CanvasName} в {ScenePath}, игрок {player.name}");
        return log.ToString();
    }

    static void AddLayer(StringBuilder log)
    {
        var tags = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
        var layer = tags.FindProperty("layers").GetArrayElementAtIndex(PreviewLayer);
        if (layer.stringValue == "Preview")
            return;
        if (layer.stringValue != "")
            throw new System.InvalidOperationException($"слой {PreviewLayer} уже занят: {layer.stringValue}");
        layer.stringValue = "Preview";
        tags.ApplyModifiedPropertiesWithoutUndo();
        log.AppendLine($"слой {PreviewLayer} Preview добавлен");
    }

    // ---------- Спрайты ----------

    static void MakeSprites()
    {
        Directory.CreateDirectory(SpriteFolder);
        rounded = SaveSprite("Rounded", 64, 16, (x, y) => RoundedAlpha(x, y, 64, 12f, 0f));
        outline = SaveSprite("Outline", 64, 16, (x, y) => RoundedAlpha(x, y, 64, 12f, 2f));
        glow = SaveSprite("Glow", 256, 0, (x, y) =>
        {
            float dx = (x + 0.5f) / 128f - 1f, dy = (y + 0.5f) / 128f - 1f;
            float r = Mathf.Clamp01(Mathf.Sqrt(dx * dx + dy * dy));
            float a = 1f - r;
            return a * a * (3f - 2f * a) * a;
        });
    }

    // Скруглённый квадрат со сглаженным краем; stroke > 0 — только рамка такой толщины
    static float RoundedAlpha(int x, int y, int size, float radius, float stroke)
    {
        float half = size * 0.5f;
        float px = Mathf.Abs(x + 0.5f - half), py = Mathf.Abs(y + 0.5f - half);
        float qx = Mathf.Max(px - (half - radius), 0f), qy = Mathf.Max(py - (half - radius), 0f);
        float outside = Mathf.Sqrt(qx * qx + qy * qy) + Mathf.Min(Mathf.Max(px - (half - radius), py - (half - radius)), 0f) - radius;
        float fill = Mathf.Clamp01(0.5f - outside);
        if (stroke <= 0f)
            return fill;
        return fill * Mathf.Clamp01(0.5f + outside + stroke);
    }

    static Sprite SaveSprite(string name, int size, int border, System.Func<int, int, float> alpha)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var pixels = new Color32[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                pixels[y * size + x] = new Color(1f, 1f, 1f, alpha(x, y));
        tex.SetPixels32(pixels);
        var path = SpriteFolder + name + ".png";
        File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        AssetDatabase.ImportAsset(path);
        var importer = (TextureImporter)AssetImporter.GetAtPath(path);
        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.spriteBorder = new Vector4(border, border, border, border);
        importer.mipmapEnabled = false;
        importer.alphaIsTransparency = true;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.SaveAndReimport();
        return AssetDatabase.LoadAssetAtPath<Sprite>(path);
    }

    // ---------- Иконки частей ----------

    // Откуда смотреть на часть (наклон и поворот от взгляда в лицо) и брать ли одну сторону тела (перчатка, ботинок)
    static (float pitch, float yaw, bool oneSide) View(GearKind kind) => kind switch
    {
        GearKind.ClothGloves or GearKind.NomadGloves => (12f, -35f, true),
        GearKind.ClothBoots or GearKind.NomadBoots => (16f, -38f, true),
        GearKind.ClothPantsRope or GearKind.NomadPants => (6f, -22f, false),
        _ => (8f, -24f, false),
    };

    static Sprite[] MakeIcons(StringBuilder log)
    {
        var active = EditorSceneManager.GetActiveScene();
        if (active.isDirty)
            throw new System.InvalidOperationException($"{active.path}: есть несохранённые изменения, иконки снимаются в пустой сцене");
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        Directory.CreateDirectory(IconFolder);

        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.42f, 0.44f, 0.47f);
        RenderSettings.skybox = null;
        Light(new Vector3(32f, 180f + 40f, 0f), 1.25f, new Color(1f, 0.96f, 0.9f));
        Light(new Vector3(8f, 180f - 70f, 0f), 0.45f, new Color(0.8f, 0.9f, 1f));
        Light(new Vector3(18f, 15f, 0f), 0.9f, new Color(0.75f, 1f, 0.97f));

        var character = (GameObject)Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(VariantPath));
        foreach (var r in character.GetComponentsInChildren<Renderer>(true))
            r.enabled = false;

        var camera = new GameObject("Icon Camera").AddComponent<Camera>();
        camera.orthographic = true;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
        camera.allowHDR = false;
        camera.nearClipPlane = 0.01f;
        camera.farClipPlane = 20f;
        var cameraData = UnityEngine.Rendering.Universal.CameraExtensions.GetUniversalAdditionalCameraData(camera);
        cameraData.renderPostProcessing = false;
        cameraData.renderShadows = false;

        var big = new RenderTexture(IconSize * 2, IconSize * 2, 24, RenderTextureFormat.ARGB32) { antiAliasing = 8 };
        var small = new RenderTexture(IconSize, IconSize, 0, RenderTextureFormat.ARGB32);
        camera.targetTexture = big;

        var sprites = new Sprite[Gear.All.Length * Gear.QualityCount];
        var baked = new Mesh();
        var block = new MaterialPropertyBlock();
        foreach (var kind in Gear.All)
        {
            var smr = character.transform.Find(Gear.Model(kind)).GetComponent<SkinnedMeshRenderer>();
            var (pitch, yaw, oneSide) = View(kind);
            // Часть в позе покоя переводится в обычный меш в координатах мира: у перчатки и ботинка остаются
            // треугольники одной стороны тела, вторая в кадр не попадает
            smr.gameObject.SetActive(true);
            // С useScale масштаб объекта уже в вершинах: в мир — только сдвиг и поворот
            smr.BakeMesh(baked, true);
            var toWorld = Matrix4x4.TRS(smr.transform.position, smr.transform.rotation, Vector3.one);
            var points = baked.vertices.Select(v => toWorld.MultiplyPoint3x4(v)).ToArray();
            var right = character.transform.right;
            var origin = character.transform.position;
            bool Keep(int i) => !oneSide || Vector3.Dot(points[i] - origin, right) > 0f;

            var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.vertices = points;
            mesh.normals = baked.normals.Select(n => toWorld.MultiplyVector(n).normalized).ToArray();
            mesh.tangents = baked.tangents.Select(t =>
            {
                var d = toWorld.MultiplyVector(t).normalized;
                return new Vector4(d.x, d.y, d.z, t.w);
            }).ToArray();
            mesh.uv = baked.uv;
            mesh.subMeshCount = baked.subMeshCount;
            for (int sub = 0; sub < baked.subMeshCount; sub++)
            {
                var tris = baked.GetTriangles(sub);
                var kept = Enumerable.Range(0, tris.Length / 3)
                    .Where(t => Keep(tris[t * 3]) && Keep(tris[t * 3 + 1]) && Keep(tris[t * 3 + 2]))
                    .SelectMany(t => new[] { tris[t * 3], tris[t * 3 + 1], tris[t * 3 + 2] }).ToArray();
                mesh.SetTriangles(kept, sub);
            }
            mesh.RecalculateBounds();
            var shown = new GameObject("Icon " + kind, typeof(MeshFilter), typeof(MeshRenderer));
            shown.GetComponent<MeshFilter>().sharedMesh = mesh;
            shown.GetComponent<MeshRenderer>().sharedMaterials = smr.sharedMaterials;

            var rotation = Quaternion.Euler(pitch, character.transform.eulerAngles.y + 180f + yaw, 0f);
            var inverse = Quaternion.Inverse(rotation);
            var local = Enumerable.Range(0, points.Length).Where(Keep).Select(i => inverse * points[i]).ToArray();
            var min = local.Aggregate(Vector3.positiveInfinity, Vector3.Min);
            var max = local.Aggregate(Vector3.negativeInfinity, Vector3.Max);
            var mid = rotation * ((min + max) * 0.5f);
            float extent = Mathf.Max(max.x - min.x, max.y - min.y);
            camera.orthographicSize = extent * 0.5f * 1.1f;
            camera.transform.SetPositionAndRotation(mid - rotation * Vector3.forward * 5f, rotation);
            // Своя иконка на каждый цвет качества: полоса части на иконке того же цвета, что на игроке
            var renderer = shown.GetComponent<MeshRenderer>();
            int count = 0;
            for (int q = 0; q < Gear.QualityCount; q++)
            {
                var quality = (GearQuality)q;
                if (!Gear.HasQuality(kind, quality))
                    continue;
                block.SetColor("_QualityColor", Gear.QualityColor(quality));
                renderer.SetPropertyBlock(block);
                camera.Render();
                Graphics.Blit(big, small);
                sprites[(int)kind * Gear.QualityCount + q] = SaveIcon($"{Gear.Model(kind)}_{quality}", small);
                count++;
            }
            Object.DestroyImmediate(shown);
            Object.DestroyImmediate(mesh);
            log.AppendLine($"иконки {Gear.Model(kind)}: {extent:0.000} м, цветов {count}");
        }
        Object.DestroyImmediate(baked);
        camera.targetTexture = null;
        big.Release();
        small.Release();
        return sprites;
    }

    static void Light(Vector3 euler, float intensity, Color color)
    {
        var light = new GameObject("Icon Light").AddComponent<Light>();
        light.type = LightType.Directional;
        light.transform.rotation = Quaternion.Euler(euler);
        light.intensity = intensity;
        light.color = color;
        light.shadows = LightShadows.None;
    }

    static Sprite SaveIcon(string name, RenderTexture source)
    {
        var previous = RenderTexture.active;
        RenderTexture.active = source;
        var tex = new Texture2D(IconSize, IconSize, TextureFormat.RGBA32, false);
        tex.ReadPixels(new Rect(0, 0, IconSize, IconSize), 0, 0);
        RenderTexture.active = previous;
        // Край сглажен по чёрному фону: цвет делится на прозрачность, иначе вокруг части тёмная кайма
        var pixels = tex.GetPixels();
        for (int i = 0; i < pixels.Length; i++)
        {
            var c = pixels[i];
            if (c.a > 0.004f && c.a < 1f)
                pixels[i] = new Color(Mathf.Clamp01(c.r / c.a), Mathf.Clamp01(c.g / c.a), Mathf.Clamp01(c.b / c.a), c.a);
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
        // Иконка на экране в 2–3 раза меньше файла: без мип-уровней мелкие детали рябят
        importer.mipmapEnabled = true;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.maxTextureSize = IconSize;
        importer.SaveAndReimport();
        return AssetDatabase.LoadAssetAtPath<Sprite>(path);
    }

    // ---------- Canvas ----------

    // ---------- Камеры ----------

    static (CinemachineCamera outfit, Camera overlay, Light fill) BuildCameras(StringBuilder log)
    {
        var main = Camera.main;
        var outfit = new GameObject(OutfitCameraName).AddComponent<CinemachineCamera>();
        outfit.Priority = 100;
        var lens = outfit.Lens;
        lens.FieldOfView = 18f;
        lens.NearClipPlane = 0.1f;
        lens.FarClipPlane = 200f;
        outfit.Lens = lens;
        outfit.enabled = false;

        var fill = new GameObject("Outfit Fill").AddComponent<Light>();
        fill.transform.SetParent(outfit.transform, false);
        fill.transform.localPosition = new Vector3(-1.2f, 1.2f, 0f);
        fill.type = LightType.Point;
        fill.range = 12f;
        fill.intensity = 1.2f;
        fill.color = new Color(1f, 0.95f, 0.88f);
        fill.shadows = LightShadows.None;
        fill.enabled = false;

        // Overlay-камера — дочерняя Main Camera: стоит там же, куда Cinemachine ставит Main Camera
        var stale = main.transform.Find(OverlayCameraName);
        if (stale != null)
            Object.DestroyImmediate(stale.gameObject);
        var overlay = new GameObject(OverlayCameraName).AddComponent<Camera>();
        overlay.transform.SetParent(main.transform, false);
        overlay.cullingMask = 1 << PreviewLayer;
        overlay.fieldOfView = main.fieldOfView;
        var overlayData = overlay.GetUniversalAdditionalCameraData();
        overlayData.renderType = CameraRenderType.Overlay;
        // Глубину мира Overlay-камера стирает (clearDepth, так по умолчанию): игрока не закрывает то, что
        // в мире стоит между ним и камерой окна
        overlayData.renderShadows = false;
        overlay.enabled = false;
        var mainData = main.GetUniversalAdditionalCameraData();
        mainData.cameraStack.RemoveAll(c => c == null);
        mainData.cameraStack.Add(overlay);

        var blends = AssetDatabase.LoadAssetAtPath<CinemachineBlenderSettings>(BlendsPath);
        if (blends == null)
        {
            blends = ScriptableObject.CreateInstance<CinemachineBlenderSettings>();
            AssetDatabase.CreateAsset(blends, BlendsPath);
        }
        // Переход к камере окна и обратно мгновенный: облёт игрока делает OutfitPreview, камера окна в начале
        // и в конце стоит там же, где камера игры. Переход Cinemachine (по умолчанию 2 с) вёл камеру по прямой
        // почти сквозь игрока, и игрок выпадал из кадра
        var blend = new CinemachineBlendDefinition(CinemachineBlendDefinition.Styles.Cut, 0f);
        blends.CustomBlends = new[]
        {
            new CinemachineBlenderSettings.CustomBlend { From = AnyCamera, To = OutfitCameraName, Blend = blend },
            new CinemachineBlenderSettings.CustomBlend { From = OutfitCameraName, To = AnyCamera, Blend = blend },
        };
        EditorUtility.SetDirty(blends);
        AssetDatabase.SaveAssets();
        var brain = main.GetComponent<CinemachineBrain>();
        brain.CustomBlends = blends;
        log.AppendLine($"камеры окна: {OutfitCameraName}, {OverlayCameraName} в стеке {main.name}, переход Cinemachine мгновенный");
        return (outfit, overlay, fill);
    }

    // ---------- Canvas ----------

    static void BuildCanvas(GameObject player, Sprite[] icons, (CinemachineCamera outfit, Camera overlay, Light fill) cameras)
    {
        var root = new GameObject(CanvasName, typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster), typeof(CanvasGroup));
        var canvas = root.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        // Settings Canvas — 80, Loading Canvas — 90, FPS Canvas — 100
        canvas.sortingOrder = 70;
        Scale(root);
        var rootRect = (RectTransform)root.transform;

        // Фон — на своём Canvas в режиме Screen Space - Camera: его рисует Main Camera вместе с миром, и игрок
        // с Overlay-камеры ложится поверх. Overlay-Canvas рисуется последним и закрыл бы игрока
        var backdropRoot = new GameObject(BackdropName, typeof(Canvas), typeof(CanvasScaler), typeof(CanvasGroup));
        var backdropCanvas = backdropRoot.GetComponent<Canvas>();
        backdropCanvas.renderMode = RenderMode.ScreenSpaceCamera;
        backdropCanvas.worldCamera = Camera.main;
        backdropCanvas.planeDistance = 0.5f;
        // Позже прозрачного в мире (вода): фон закрывает и его
        backdropCanvas.sortingOrder = 100;
        Scale(backdropRoot);
        var backdropRect = (RectTransform)backdropRoot.transform;
        // Остров за игроком просвечивает: видно, что игрок стоит на базе. В HDR Main Camera небо и песок на солнце
        // ярче единицы, поэтому при 0,94 их видно заметно сильнее, чем было бы на Overlay-канвасе
        var backdrop = NewImage("Backdrop", backdropRect, new Color(0.035f, 0.055f, 0.07f, 0.94f), null);
        Stretch(backdrop.rectTransform);
        var halo = NewImage("Glow", backdropRect, new Color(Accent.r, Accent.g, Accent.b, 0.13f), glow);
        Place(halo.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0f, 20f), new Vector2(1100f, 1100f));

        var window = new GameObject("Window", typeof(RectTransform)).GetComponent<RectTransform>();
        window.SetParent(rootRect, false);
        Stretch(window);

        // Заголовок
        var bar = NewImage("Accent", window, Accent, rounded);
        Place(bar.rectTransform, new Vector2(0f, 1f), new Vector2(98f, -74f), new Vector2(56f, 5f));
        var title = Label("Title", window, display, "СНАРЯЖЕНИЕ", 88, Color.white, TextAnchor.UpperLeft);
        Place(title.rectTransform, new Vector2(0f, 1f), new Vector2(96f, -82f), new Vector2(900f, 96f));
        var subtitle = Label("Subtitle", window, body, "Что надето, то уходит с тобой в вылазку", 26, Muted, TextAnchor.UpperLeft);
        Place(subtitle.rectTransform, new Vector2(0f, 1f), new Vector2(98f, -176f), new Vector2(900f, 36f));

        var close = NewButton("Close Button", window, "ЗАКРЫТЬ   I", 30, false);
        Place((RectTransform)close.transform, new Vector2(1f, 1f), new Vector2(-96f, -80f), new Vector2(230f, 60f));

        // Слоты
        var slotColumn = new GameObject("Slots", typeof(RectTransform), typeof(VerticalLayoutGroup)).GetComponent<RectTransform>();
        slotColumn.SetParent(window, false);
        Place(slotColumn, new Vector2(0f, 1f), new Vector2(96f, -240f), new Vector2(440f, 616f));
        var slotLayout = slotColumn.GetComponent<VerticalLayoutGroup>();
        slotLayout.spacing = 14f;
        slotLayout.childControlWidth = slotLayout.childControlHeight = false;
        slotLayout.childForceExpandWidth = slotLayout.childForceExpandHeight = false;
        var slots = new GearSlotView[Gear.SlotCount];
        for (int i = 0; i < slots.Length; i++)
            slots[i] = BuildSlot(slotColumn, (GearSlot)i);

        var riskSummary = Label("Risk Summary", window, body, "", 22, Risk, TextAnchor.UpperLeft);
        Place(riskSummary.rectTransform, new Vector2(0f, 1f), new Vector2(98f, -876f), new Vector2(436f, 90f));
        riskSummary.horizontalOverflow = HorizontalWrapMode.Wrap;
        var footer = Label("Footer", window, body, "I  или  Esc  —  закрыть", 22, Muted, TextAnchor.LowerLeft);
        Place(footer.rectTransform, new Vector2(0f, 0f), new Vector2(98f, 44f), new Vector2(600f, 30f));

        // Середина окна: игрок виден сквозь неё, прозрачная плашка ловит перетаскивание и колесо
        var previewImage = NewImage("Preview", window, new Color(0f, 0f, 0f, 0f), null);
        previewImage.raycastTarget = true;
        previewImage.transform.SetSiblingIndex(0);
        var previewRect = previewImage.rectTransform;
        previewRect.anchorMin = Vector2.zero;
        previewRect.anchorMax = Vector2.one;
        previewRect.offsetMin = new Vector2(540f, 0f);
        previewRect.offsetMax = new Vector2(-650f, 0f);
        var preview = previewImage.gameObject.AddComponent<OutfitPreview>();
        var previewSo = new SerializedObject(preview);
        previewSo.FindProperty("target").objectReferenceValue = player.transform;
        previewSo.FindProperty("animator").objectReferenceValue = player.GetComponentInChildren<Animator>();
        previewSo.FindProperty("outfitCamera").objectReferenceValue = cameras.outfit;
        previewSo.FindProperty("overlayCamera").objectReferenceValue = cameras.overlay;
        previewSo.FindProperty("fill").objectReferenceValue = cameras.fill;
        previewSo.FindProperty("previewLayer").intValue = PreviewLayer;
        previewSo.ApplyModifiedPropertiesWithoutUndo();
        var fullBody = NewButton("Full Body Button", previewRect, "ВЕСЬ РОСТ", 24, false);
        Place((RectTransform)fullBody.transform, new Vector2(0.5f, 0f), new Vector2(0f, 56f), new Vector2(170f, 44f), new Vector2(0.5f, 0f));
        var hint = Label("Hint", previewRect, body, "Перетащи — повернуть   ·   колесо — ближе", 21, Muted, TextAnchor.LowerCenter);
        Place(hint.rectTransform, new Vector2(0.5f, 0f), new Vector2(0f, 18f), new Vector2(640f, 30f), new Vector2(0.5f, 0f));
        // Подсказка ложится на персонажа, когда камера близко: тень отделяет её от одежды
        hint.gameObject.AddComponent<Shadow>().effectColor = new Color(0f, 0f, 0f, 0.8f);

        // Список частей слота
        var listTitle = Label("List Title", window, display, "НАГРУДНИК", 44, Color.white, TextAnchor.LowerLeft);
        Place(listTitle.rectTransform, new Vector2(1f, 1f), new Vector2(-96f - 540f, -240f), new Vector2(360f, 56f), new Vector2(0f, 1f));
        var listCount = Label("List Count", window, body, "", 22, Muted, TextAnchor.LowerRight);
        Place(listCount.rectTransform, new Vector2(1f, 1f), new Vector2(-96f, -242f), new Vector2(200f, 50f));
        var divider = NewImage("Divider", window, new Color32(0x24, 0x31, 0x3A, 0xFF), null);
        Place(divider.rectTransform, new Vector2(1f, 1f), new Vector2(-96f, -304f), new Vector2(540f, 2f));

        var scroll = new GameObject("List", typeof(RectTransform), typeof(ScrollRect)).GetComponent<ScrollRect>();
        scroll.transform.SetParent(window, false);
        var scrollRect = (RectTransform)scroll.transform;
        scrollRect.anchorMin = new Vector2(1f, 0f);
        scrollRect.anchorMax = new Vector2(1f, 1f);
        scrollRect.pivot = new Vector2(1f, 1f);
        scrollRect.offsetMin = new Vector2(-96f - 540f, 110f + 330f + 22f);
        scrollRect.offsetMax = new Vector2(-96f, -320f);
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
        contentLayout.spacing = 12f;
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
        var card = BuildCard(content);
        var emptyNote = Label("Empty Note", scrollRect, body, "Для этого слота пока ничего нет: шлемов в игре ещё нет.", 23, Muted, TextAnchor.UpperLeft);
        Place(emptyNote.rectTransform, new Vector2(0f, 1f), new Vector2(4f, -8f), new Vector2(520f, 70f));
        emptyNote.horizontalOverflow = HorizontalWrapMode.Wrap;

        // Выбранная часть
        var details = NewImage("Details", window, new Color32(0x0F, 0x16, 0x1B, 0xFF), rounded);
        Place(details.rectTransform, new Vector2(1f, 0f), new Vector2(-96f, 110f), new Vector2(540f, 330f));
        var detailsFrame = NewImage("Frame", details.rectTransform, new Color32(0x22, 0x30, 0x38, 0xFF), outline);
        Stretch(detailsFrame.rectTransform);
        var detailIconBack = NewImage("Icon Frame", details.rectTransform, IconBack, rounded);
        Place(detailIconBack.rectTransform, new Vector2(0f, 1f), new Vector2(24f, -24f), new Vector2(110f, 110f));
        var detailIcon = NewImage("Icon", detailIconBack.rectTransform, Color.white, null);
        Place(detailIcon.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(102f, 102f));
        detailIcon.preserveAspect = true;
        var detailName = Label("Name", details.rectTransform, display, "", 40, Color.white, TextAnchor.UpperLeft);
        Place(detailName.rectTransform, new Vector2(0f, 1f), new Vector2(152f, -24f), new Vector2(366f, 46f));
        FitLine(detailName, 26);
        var detailSet = Label("Set", details.rectTransform, body, "", 22, Color.white, TextAnchor.UpperLeft);
        Place(detailSet.rectTransform, new Vector2(0f, 1f), new Vector2(152f, -72f), new Vector2(366f, 30f));
        var detailRisk = Label("Risk", details.rectTransform, body, "", 20, Risk, TextAnchor.UpperLeft);
        Place(detailRisk.rectTransform, new Vector2(0f, 1f), new Vector2(152f, -104f), new Vector2(366f, 30f));
        detailRisk.horizontalOverflow = HorizontalWrapMode.Wrap;
        var detailAbout = Label("About", details.rectTransform, body, "", 23, new Color32(0xC9, 0xD3, 0xD8, 0xFF), TextAnchor.UpperLeft);
        Place(detailAbout.rectTransform, new Vector2(0f, 1f), new Vector2(24f, -146f), new Vector2(492f, 92f));
        detailAbout.horizontalOverflow = HorizontalWrapMode.Wrap;
        var wear = NewButton("Wear Button", details.rectTransform, "НАДЕТЬ", 32, true);
        Place((RectTransform)wear.transform, new Vector2(0f, 0f), new Vector2(24f, 24f), new Vector2(300f, 62f));
        var takeOff = NewButton("Take Off Button", details.rectTransform, "СНЯТЬ", 30, false);
        Place((RectTransform)takeOff.transform, new Vector2(1f, 0f), new Vector2(-24f, 24f), new Vector2(180f, 62f));

        var screen = root.AddComponent<EquipmentScreen>();
        var so = new SerializedObject(screen);
        so.FindProperty("canvas").objectReferenceValue = canvas;
        so.FindProperty("group").objectReferenceValue = root.GetComponent<CanvasGroup>();
        so.FindProperty("backdrop").objectReferenceValue = backdropCanvas;
        so.FindProperty("backdropGroup").objectReferenceValue = backdropRoot.GetComponent<CanvasGroup>();
        so.FindProperty("window").objectReferenceValue = window;
        so.FindProperty("player").objectReferenceValue = player;
        so.FindProperty("worn").objectReferenceValue = player.GetComponent<WornGear>();
        so.FindProperty("preview").objectReferenceValue = preview;
        var slotList = so.FindProperty("slots");
        slotList.arraySize = slots.Length;
        for (int i = 0; i < slots.Length; i++)
            slotList.GetArrayElementAtIndex(i).objectReferenceValue = slots[i];
        so.FindProperty("cardTemplate").objectReferenceValue = card;
        so.FindProperty("listTitle").objectReferenceValue = listTitle;
        so.FindProperty("listCount").objectReferenceValue = listCount;
        so.FindProperty("emptyNote").objectReferenceValue = emptyNote.gameObject;
        so.FindProperty("details").objectReferenceValue = details.gameObject;
        so.FindProperty("detailIcon").objectReferenceValue = detailIcon;
        so.FindProperty("detailName").objectReferenceValue = detailName;
        so.FindProperty("detailSet").objectReferenceValue = detailSet;
        so.FindProperty("detailAbout").objectReferenceValue = detailAbout;
        so.FindProperty("detailRisk").objectReferenceValue = detailRisk;
        so.FindProperty("wearButton").objectReferenceValue = wear;
        so.FindProperty("wearLabel").objectReferenceValue = wear.GetComponentInChildren<Text>();
        so.FindProperty("takeOffButton").objectReferenceValue = takeOff;
        so.FindProperty("closeButton").objectReferenceValue = close;
        so.FindProperty("fullBodyButton").objectReferenceValue = fullBody;
        so.FindProperty("riskSummary").objectReferenceValue = riskSummary;
        var iconList = so.FindProperty("icons");
        iconList.arraySize = icons.Length;
        for (int i = 0; i < icons.Length; i++)
            iconList.GetArrayElementAtIndex(i).objectReferenceValue = icons[i];
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    static GearSlotView BuildSlot(RectTransform parent, GearSlot slot)
    {
        var back = NewImage(slot + " Slot", parent, Card, rounded);
        back.rectTransform.sizeDelta = new Vector2(440f, 112f);
        back.raycastTarget = true;
        var button = back.gameObject.AddComponent<Button>();
        Tint(button, back);
        var selection = NewImage("Selection", back.rectTransform, Accent, outline);
        Stretch(selection.rectTransform);
        selection.enabled = false;
        var quality = QualityBar(back.rectTransform);
        var iconBack = NewImage("Icon Frame", back.rectTransform, IconBack, rounded);
        Place(iconBack.rectTransform, new Vector2(0f, 0.5f), new Vector2(24f, 0f), new Vector2(88f, 88f), new Vector2(0f, 0.5f));
        var icon = NewImage("Icon", iconBack.rectTransform, Color.white, null);
        Place(icon.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(82f, 82f));
        icon.preserveAspect = true;
        var caption = Label("Slot", back.rectTransform, display, Gear.SlotName(slot).ToUpper(), 24, SlotCaption, TextAnchor.LowerLeft);
        Place(caption.rectTransform, new Vector2(0f, 0.5f), new Vector2(130f, 2f), new Vector2(200f, 30f), new Vector2(0f, 0f));
        var item = Label("Item", back.rectTransform, body, "", 27, Color.white, TextAnchor.UpperLeft);
        Place(item.rectTransform, new Vector2(0f, 0.5f), new Vector2(130f, -2f), new Vector2(296f, 34f), new Vector2(0f, 1f));
        FitLine(item, 19);
        var risk = Chip("Risk", back.rectTransform, "ТЕРЯЕТСЯ", Risk, 112f);
        Place((RectTransform)risk.transform, new Vector2(1f, 0.5f), new Vector2(-16f, 22f), new Vector2(112f, 28f), new Vector2(1f, 0.5f));

        var view = back.gameObject.AddComponent<GearSlotView>();
        var so = new SerializedObject(view);
        so.FindProperty("button").objectReferenceValue = button;
        so.FindProperty("icon").objectReferenceValue = icon;
        so.FindProperty("slotLabel").objectReferenceValue = caption;
        so.FindProperty("itemLabel").objectReferenceValue = item;
        so.FindProperty("qualityBar").objectReferenceValue = quality;
        so.FindProperty("selection").objectReferenceValue = selection;
        so.FindProperty("riskMark").objectReferenceValue = risk;
        so.ApplyModifiedPropertiesWithoutUndo();
        return view;
    }

    static GearCardView BuildCard(RectTransform parent)
    {
        var back = NewImage("Card", parent, Card, rounded);
        back.rectTransform.sizeDelta = new Vector2(540f, 104f);
        back.raycastTarget = true;
        var button = back.gameObject.AddComponent<Button>();
        Tint(button, back);
        var selection = NewImage("Selection", back.rectTransform, Accent, outline);
        Stretch(selection.rectTransform);
        selection.enabled = false;
        var quality = QualityBar(back.rectTransform);
        var iconBack = NewImage("Icon Frame", back.rectTransform, IconBack, rounded);
        Place(iconBack.rectTransform, new Vector2(0f, 0.5f), new Vector2(24f, 0f), new Vector2(80f, 80f), new Vector2(0f, 0.5f));
        var icon = NewImage("Icon", iconBack.rectTransform, Color.white, null);
        Place(icon.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(74f, 74f));
        icon.preserveAspect = true;
        var name = Label("Name", back.rectTransform, body, "", 27, Color.white, TextAnchor.LowerLeft);
        Place(name.rectTransform, new Vector2(0f, 0.5f), new Vector2(122f, 0f), new Vector2(290f, 36f), new Vector2(0f, 0f));
        FitLine(name, 19);
        var set = Label("Set", back.rectTransform, body, "", 21, Muted, TextAnchor.UpperLeft);
        Place(set.rectTransform, new Vector2(0f, 0.5f), new Vector2(122f, -4f), new Vector2(290f, 30f), new Vector2(0f, 1f));
        var count = Label("Count", back.rectTransform, display, "", 30, Muted, TextAnchor.MiddleRight);
        Place(count.rectTransform, new Vector2(1f, 0.5f), new Vector2(-18f, -18f), new Vector2(80f, 34f), new Vector2(1f, 0.5f));
        var badge = Chip("Worn", back.rectTransform, "НАДЕТО", Accent, 100f);
        Place((RectTransform)badge.transform, new Vector2(1f, 0.5f), new Vector2(-18f, 20f), new Vector2(100f, 28f), new Vector2(1f, 0.5f));

        var view = back.gameObject.AddComponent<GearCardView>();
        var so = new SerializedObject(view);
        so.FindProperty("button").objectReferenceValue = button;
        so.FindProperty("icon").objectReferenceValue = icon;
        so.FindProperty("nameLabel").objectReferenceValue = name;
        so.FindProperty("setLabel").objectReferenceValue = set;
        so.FindProperty("countLabel").objectReferenceValue = count;
        so.FindProperty("qualityBar").objectReferenceValue = quality;
        so.FindProperty("selection").objectReferenceValue = selection;
        so.FindProperty("wornBadge").objectReferenceValue = badge;
        so.ApplyModifiedPropertiesWithoutUndo();
        return view;
    }

    // Полоса цвета качества по левому краю плашки
    static Image QualityBar(RectTransform parent)
    {
        var bar = NewImage("Quality", parent, Color.white, rounded);
        var rect = bar.rectTransform;
        rect.anchorMin = new Vector2(0f, 0f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 0.5f);
        rect.offsetMin = new Vector2(9f, 20f);
        rect.offsetMax = new Vector2(14f, -20f);
        bar.pixelsPerUnitMultiplier = 6f;
        return bar;
    }

    // Плашка-отметка: бледная подложка цвета color и надпись тем же цветом
    static GameObject Chip(string name, RectTransform parent, string text, Color color, float width)
    {
        var back = NewImage(name, parent, new Color(color.r, color.g, color.b, 0.16f), rounded);
        back.rectTransform.sizeDelta = new Vector2(width, 26f);
        back.pixelsPerUnitMultiplier = 2f;
        var label = Label("Label", back.rectTransform, display, text, 21, color, TextAnchor.MiddleCenter);
        Stretch(label.rectTransform);
        label.rectTransform.offsetMin = new Vector2(0f, -1f);
        return back.gameObject;
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

    // Наведение светлее, нажатие темнее; выключенная кнопка тусклая
    static void Tint(Selectable selectable, Graphic target)
    {
        selectable.targetGraphic = target;
        var colors = selectable.colors;
        colors.colorMultiplier = 1.4f;
        colors.normalColor = new Color(0.715f, 0.715f, 0.715f, 1f);
        colors.highlightedColor = new Color(1f, 1f, 1f, 1f);
        colors.selectedColor = new Color(0.86f, 0.86f, 0.86f, 1f);
        colors.pressedColor = new Color(0.6f, 0.6f, 0.6f, 1f);
        colors.disabledColor = new Color(0.5f, 0.5f, 0.5f, 0.55f);
        colors.fadeDuration = 0.08f;
        selectable.colors = colors;
    }

    static Image NewImage(string name, Transform parent, Color color, Sprite sprite)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var image = go.GetComponent<Image>();
        image.sprite = sprite;
        if (sprite != null && sprite.border != Vector4.zero)
            image.type = UnityEngine.UI.Image.Type.Sliced;
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

    // Длинное имя («Штаны с верёвочным поясом») уменьшается, чтобы влезть в одну строку
    static void FitLine(Text text, int minSize)
    {
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Truncate;
        text.resizeTextForBestFit = true;
        text.resizeTextMinSize = minSize;
        text.resizeTextMaxSize = text.fontSize;
    }

    static void Scale(GameObject canvas)
    {
        var scaler = canvas.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        // Поровну по ширине и высоте: на узком экране середина с персонажем не сжимается в полоску
        scaler.matchWidthOrHeight = 0.5f;
    }

    static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
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
