using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

// Вертикальный срез: база → вылазка → эвакуация → база.
//  1. на префаб PlayerSurvivor вешает Backpack (рюкзак есть у всех персонажей);
//  2. материалы в Assets/Loop/Materials: луч и кольцо эвакуации, мешок, ящик, чертёж, остров базы;
//  3. сцена Raid — копия SampleScene (если её ещё нет): без союзников, ArenaPlayerSpawn заменён на RaidDirector,
//     враги стали рейдерами (ShooterBot.raider) с именами Raider 1..5, карта — остров (ArenaShape.Island, биом Tropic);
//  4. сцена Base — копия SampleScene без арены, ботов, экрана загрузки и ленты убийств: игрок с камерой, свет,
//     меню настроек, счётчик FPS. Остров, склад, торговец, верстак и причал собираются заново на каждом запуске
//     (корень Island), BaseHub получает игрока и места;
//  5. в Build Settings первыми идут Base и Raid.
// Скрипт можно запускать повторно. Открытая сцена с несохранёнными правками — ошибка.
// Запуск: unity command run_script --file Tools/Unity/SetupLoop.cs --project-path "<путь к проекту>"
public static class SetupLoop
{
    const string SamplePath = "Assets/Scenes/SampleScene.unity";
    const string RaidPath = "Assets/Scenes/Raid.unity";
    const string BasePath = "Assets/Scenes/Base.unity";
    const string PrefabPath = "Assets/Characters/Survivor/PlayerSurvivor.prefab";
    const string MaterialDir = "Assets/Loop/Materials";
    // Корни SampleScene, которые в срезе не нужны: модели из ранних опытов
    static readonly string[] Leftovers = { "Survivor", "20261110020344_88090e64", "SurvivorRigged" };

    static readonly StringBuilder log = new();

    public static string Main()
    {
        log.Clear();
        var active = EditorSceneManager.GetActiveScene();
        if (active.isDirty)
            throw new System.InvalidOperationException($"В сцене {active.path} есть несохранённые правки: сохраните их и запустите снова");

        SetupPrefab();
        var materials = SetupMaterials();
        SetupRaid(materials);
        SetupBase(materials);
        SetupBuildSettings();
        return log.ToString();
    }

    // ---------- 1. Префаб ----------

    static void SetupPrefab()
    {
        var root = PrefabUtility.LoadPrefabContents(PrefabPath);
        if (root.GetComponent<Backpack>() == null)
        {
            root.AddComponent<Backpack>();
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            log.AppendLine("prefab: Backpack added");
        }
        PrefabUtility.UnloadPrefabContents(root);
    }

    // ---------- 2. Материалы ----------

    static Dictionary<string, Material> SetupMaterials()
    {
        if (!AssetDatabase.IsValidFolder(MaterialDir))
        {
            AssetDatabase.CreateFolder("Assets/Loop", "Materials");
        }
        var m = new Dictionary<string, Material>
        {
            ["Beam"] = Transparent("ExtractionBeam", new Color(0.35f, 1f, 0.45f, 0.35f)),
            ["Ring"] = Transparent("ExtractionRing", new Color(0.45f, 1f, 0.55f, 0.55f)),
            ["Bag"] = Lit("LootBag", new Color(0.32f, 0.36f, 0.22f), 0.2f, null),
            ["Crate"] = Lit("LootCrate", new Color(0.55f, 0.36f, 0.18f), 0.15f, null),
            ["Blueprint"] = Lit("LootBlueprint", new Color(0.25f, 0.55f, 1f), 0.5f, new Color(0.15f, 0.4f, 1f) * 1.5f),
            ["Sand"] = Lit("BaseSand", new Color(0.86f, 0.78f, 0.56f), 0.05f, null),
            ["Sea"] = Lit("BaseSea", new Color(0.12f, 0.45f, 0.55f), 0.9f, null),
            ["Ocean"] = Lit("RaidOcean", new Color(0.2f, 0.7f, 0.76f), 0.9f, null),
            ["Wood"] = Lit("BaseWood", new Color(0.5f, 0.34f, 0.2f), 0.15f, null),
            ["DarkWood"] = Lit("BaseDarkWood", new Color(0.32f, 0.21f, 0.13f), 0.15f, null),
            ["Metal"] = Lit("BaseMetal", new Color(0.45f, 0.48f, 0.5f), 0.6f, null),
            ["Cloth"] = Lit("BaseCloth", new Color(0.75f, 0.25f, 0.2f), 0.1f, null),
            ["Leaves"] = Lit("BaseLeaves", new Color(0.25f, 0.55f, 0.22f), 0.1f, null),
            ["Signal"] = Lit("BaseSignal", new Color(0.35f, 1f, 0.45f), 0.3f, new Color(0.3f, 1f, 0.4f) * 2f),
        };
        AssetDatabase.SaveAssets();
        return m;
    }

    static Material Lit(string name, Color color, float smoothness, Color? emission)
    {
        var material = LoadOrCreate(name, "Universal Render Pipeline/Lit");
        material.SetColor("_BaseColor", color);
        material.SetFloat("_Smoothness", smoothness);
        if (emission != null)
        {
            material.EnableKeyword("_EMISSION");
            material.SetColor("_EmissionColor", emission.Value);
            material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
        }
        EditorUtility.SetDirty(material);
        return material;
    }

    // Полупрозрачный Unlit без теней и записи глубины: луч видно издалека сквозь деревья
    static Material Transparent(string name, Color color)
    {
        var material = LoadOrCreate(name, "Universal Render Pipeline/Unlit");
        material.SetColor("_BaseColor", color);
        material.SetFloat("_Surface", 1f);
        material.SetFloat("_Blend", 0f);
        material.SetFloat("_Cull", (float)CullMode.Off);
        material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        material.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
        material.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
        material.SetFloat("_ZWrite", 0f);
        material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        material.SetOverrideTag("RenderType", "Transparent");
        material.renderQueue = (int)RenderQueue.Transparent;
        EditorUtility.SetDirty(material);
        return material;
    }

    static Material LoadOrCreate(string name, string shader)
    {
        string path = $"{MaterialDir}/{name}.mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material != null)
            return material;
        material = new Material(Shader.Find(shader)) { name = name };
        AssetDatabase.CreateAsset(material, path);
        log.AppendLine($"material {path}");
        return material;
    }

    // ---------- 3. Вылазка ----------

    static void SetupRaid(Dictionary<string, Material> materials)
    {
        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(RaidPath) == null)
        {
            AssetDatabase.CopyAsset(SamplePath, RaidPath);
            log.AppendLine($"{RaidPath} copied from {SamplePath}");
        }
        var scene = EditorSceneManager.OpenScene(RaidPath, OpenSceneMode.Single);

        DeleteRoots(r => r.name.StartsWith("Ally") || Leftovers.Contains(r.name));

        var arena = Object.FindObjectsByType<VoxelArena>().Single();
        var spawn = arena.GetComponent<ArenaPlayerSpawn>();
        if (spawn != null)
            Object.DestroyImmediate(spawn);

        var player = FindPlayer();
        var director = arena.GetComponent<RaidDirector>();
        if (director == null)
            director = arena.gameObject.AddComponent<RaidDirector>();
        var so = new SerializedObject(director);
        so.FindProperty("player").objectReferenceValue = player.GetComponent<CharacterHealth>();
        so.FindProperty("beamMaterial").objectReferenceValue = materials["Beam"];
        so.FindProperty("ringMaterial").objectReferenceValue = materials["Ring"];
        so.FindProperty("bagMaterial").objectReferenceValue = materials["Bag"];
        so.FindProperty("crateMaterial").objectReferenceValue = materials["Crate"];
        so.FindProperty("blueprintMaterial").objectReferenceValue = materials["Blueprint"];
        so.FindProperty("oceanMaterial").objectReferenceValue = materials["Ocean"];
        so.ApplyModifiedPropertiesWithoutUndo();

        // Карта вылазки — остров в тропиках (Бирюзовые Пределы)
        var aso = new SerializedObject(arena);
        aso.FindProperty("generation.shape").enumValueIndex = (int)ArenaShape.Island;
        aso.FindProperty("generation.biome").enumValueIndex = (int)ArenaBiomeChoice.Tropic;
        aso.ApplyModifiedPropertiesWithoutUndo();
        so.ApplyModifiedPropertiesWithoutUndo();

        int n = 0;
        foreach (var bot in Object.FindObjectsByType<ShooterBot>().OrderBy(b => b.name))
        {
            bot.name = $"Raider {++n}";
            var bso = new SerializedObject(bot);
            bso.FindProperty("raider").boolValue = true;
            bso.ApplyModifiedPropertiesWithoutUndo();
        }

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        log.AppendLine($"raid: {n} raiders, director on {arena.name}");
    }

    // ---------- 4. База ----------

    static void SetupBase(Dictionary<string, Material> materials)
    {
        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(BasePath) == null)
        {
            AssetDatabase.CopyAsset(SamplePath, BasePath);
            log.AppendLine($"{BasePath} copied from {SamplePath}");
        }
        var scene = EditorSceneManager.OpenScene(BasePath, OpenSceneMode.Single);

        DeleteRoots(r => r.name.StartsWith("Ally") || r.name.StartsWith("Enemy") || Leftovers.Contains(r.name) ||
                         r.name is "Voxel Arena" or "Loading Canvas" or "Combat HUD" or "Island" or "Base Hub");

        var player = FindPlayer();
        player.transform.SetPositionAndRotation(new Vector3(0f, 0.1f, 3f), Quaternion.identity);
        EditorUtility.SetDirty(player.transform);

        var island = new GameObject("Island").transform;
        var stations = BuildIsland(island, materials);

        var hub = new GameObject("Base Hub").AddComponent<BaseHub>();
        var so = new SerializedObject(hub);
        so.FindProperty("player").objectReferenceValue = player;
        var list = so.FindProperty("stations");
        list.arraySize = stations.Count;
        for (int i = 0; i < stations.Count; i++)
            list.GetArrayElementAtIndex(i).objectReferenceValue = stations[i];
        so.ApplyModifiedPropertiesWithoutUndo();

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        log.AppendLine($"base: {stations.Count} stations");
    }

    // Остров 36×36 м в море: склад слева, торговец справа, верстак сзади, причал с лодкой впереди (+z), пальмы
    static List<BaseStation> BuildIsland(Transform root, Dictionary<string, Material> m)
    {
        Box(root, "Sand", m["Sand"], new Vector3(0f, -1f, 0f), new Vector3(36f, 2f, 36f));
        Box(root, "Shallows", m["Sand"], new Vector3(0f, -1.25f, 0f), new Vector3(40f, 2f, 40f));
        var sea = Box(root, "Sea", m["Sea"], new Vector3(0f, -0.45f, 0f), new Vector3(600f, 0.1f, 600f));
        Object.DestroyImmediate(sea.GetComponent<Collider>());

        var stations = new List<BaseStation>();

        // Склад: штабель ящиков и железный сундук
        var stash = Station(root, "Stash", BaseStation.StationKind.Stash, new Vector3(-8f, 0f, 0f), 90f);
        Box(stash, "Crate A", m["Wood"], new Vector3(-0.8f, 0.6f, 1.2f), Vector3.one * 1.2f);
        Box(stash, "Crate B", m["DarkWood"], new Vector3(0.6f, 0.5f, 1.4f), Vector3.one * 1f);
        Box(stash, "Crate C", m["Wood"], new Vector3(-0.6f, 1.7f, 1.3f), Vector3.one * 0.9f, 15f);
        Box(stash, "Chest", m["Metal"], new Vector3(0.2f, 0.35f, 0.4f), new Vector3(1.4f, 0.7f, 0.8f));
        stations.Add(stash.GetComponent<BaseStation>());

        // Торговец: прилавок под навесом
        var trader = Station(root, "Trader", BaseStation.StationKind.Trader, new Vector3(8f, 0f, 0f), -90f);
        Box(trader, "Counter", m["Wood"], new Vector3(0f, 0.5f, 1.2f), new Vector3(3f, 1f, 1f));
        Box(trader, "Roof", m["Cloth"], new Vector3(0f, 2.5f, 1.6f), new Vector3(3.6f, 0.12f, 2.4f));
        foreach (float x in new[] { -1.6f, 1.6f })
            foreach (float z in new[] { 0.6f, 2.6f })
                Box(trader, "Pole", m["DarkWood"], new Vector3(x, 1.25f, z), new Vector3(0.15f, 2.5f, 0.15f));
        Box(trader, "Goods", m["Crate"], new Vector3(-0.7f, 1.2f, 1.2f), Vector3.one * 0.4f);
        Box(trader, "Goods", m["Metal"], new Vector3(0.6f, 1.15f, 1.3f), new Vector3(0.6f, 0.3f, 0.3f));
        stations.Add(trader.GetComponent<BaseStation>());

        // Верстак: стол, тиски, наковальня
        var bench = Station(root, "Workbench", BaseStation.StationKind.Workbench, new Vector3(0f, 0f, -8f), 0f);
        Box(bench, "Table", m["Wood"], new Vector3(0f, 0.45f, -1.2f), new Vector3(2.6f, 0.9f, 1.2f));
        Box(bench, "Vise", m["Metal"], new Vector3(-0.8f, 1.05f, -1.2f), new Vector3(0.3f, 0.3f, 0.5f));
        Box(bench, "Anvil", m["Metal"], new Vector3(1.9f, 0.4f, -1.1f), new Vector3(0.5f, 0.8f, 0.9f));
        Box(bench, "Board", m["DarkWood"], new Vector3(0f, 1.6f, -1.8f), new Vector3(2.6f, 1.4f, 0.1f));
        stations.Add(bench.GetComponent<BaseStation>());

        // Причал с лодкой и зелёным огнём: выход в вылазку
        Box(root, "Pier", m["Wood"], new Vector3(0f, -0.15f, 25f), new Vector3(3f, 0.3f, 14f));
        foreach (float z in new[] { 20f, 25f, 30f })
            foreach (float x in new[] { -1.4f, 1.4f })
                Box(root, "Pile", m["DarkWood"], new Vector3(x, -0.6f, z), new Vector3(0.25f, 1.2f, 0.25f));
        var exit = Station(root, "Exit", BaseStation.StationKind.Exit, new Vector3(0f, 0f, 30f), 0f);
        var boat = Box(exit, "Boat", m["DarkWood"], new Vector3(2.8f, -0.3f, 0f), new Vector3(1.8f, 0.7f, 4.5f));
        Object.DestroyImmediate(boat.GetComponent<Collider>());
        Box(exit, "Mast", m["DarkWood"], new Vector3(-1.2f, 1.2f, 1.5f), new Vector3(0.12f, 2.4f, 0.12f));
        Box(exit, "Signal", m["Signal"], new Vector3(-1.2f, 2.5f, 1.5f), Vector3.one * 0.35f);
        stations.Add(exit.GetComponent<BaseStation>());

        // Пальмы по краю острова
        var palms = new[] { new Vector3(-13f, 0f, -12f), new Vector3(12f, 0f, -13f), new Vector3(-14f, 0f, 11f), new Vector3(13f, 0f, 12f), new Vector3(-4f, 0f, -15f) };
        foreach (var p in palms)
        {
            Box(root, "Palm", m["DarkWood"], p + new Vector3(0f, 2.5f, 0f), new Vector3(0.4f, 5f, 0.4f), 0f, 6f);
            Box(root, "Leaves", m["Leaves"], p + new Vector3(0f, 5.1f, 0f), new Vector3(3.2f, 0.25f, 1f), 20f);
            Box(root, "Leaves", m["Leaves"], p + new Vector3(0f, 5.1f, 0f), new Vector3(1f, 0.25f, 3.2f), -25f);
        }
        return stations;
    }

    static Transform Station(Transform root, string name, BaseStation.StationKind kind, Vector3 position, float yaw)
    {
        var station = new GameObject(name).AddComponent<BaseStation>();
        var so = new SerializedObject(station);
        so.FindProperty("kind").enumValueIndex = (int)kind;
        so.FindProperty("reach").floatValue = 3f;
        so.ApplyModifiedPropertiesWithoutUndo();
        station.transform.SetParent(root, false);
        station.transform.SetLocalPositionAndRotation(position, Quaternion.Euler(0f, yaw, 0f));
        return station.transform;
    }

    static GameObject Box(Transform parent, string name, Material material, Vector3 position, Vector3 scale, float yaw = 0f, float tilt = 0f)
    {
        var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        box.name = name;
        box.transform.SetParent(parent, false);
        box.transform.SetLocalPositionAndRotation(position, Quaternion.Euler(tilt, yaw, 0f));
        box.transform.localScale = scale;
        box.GetComponent<MeshRenderer>().sharedMaterial = material;
        box.isStatic = true;
        return box;
    }

    // ---------- Общее ----------

    // Игрок — стрелок без бота
    static GameObject FindPlayer() =>
        Object.FindObjectsByType<SurvivorShooter>().Single(s => !s.BotControlled).gameObject;

    static void DeleteRoots(System.Func<GameObject, bool> match)
    {
        foreach (var root in EditorSceneManager.GetActiveScene().GetRootGameObjects())
            if (match(root))
                Object.DestroyImmediate(root);
    }

    static void SetupBuildSettings()
    {
        var scenes = EditorBuildSettings.scenes.Where(s => s.path != BasePath && s.path != RaidPath).ToList();
        scenes.Insert(0, new EditorBuildSettingsScene(RaidPath, true));
        scenes.Insert(0, new EditorBuildSettingsScene(BasePath, true));
        EditorBuildSettings.scenes = scenes.ToArray();
        log.AppendLine("build: " + string.Join(", ", scenes.Select(s => s.path)));
    }
}
