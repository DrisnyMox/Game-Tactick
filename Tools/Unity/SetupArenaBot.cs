using System.Linq;
using System.Reflection;
using System.Text;
using Unity.Cinemachine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;

// Ставит в воксельную арену SampleScene бой 5 на 5: игрок и 4 союзника против 5 врагов.
//  1. заводит слой Hitbox для коллайдеров костей (проверка земли ThirdPersonController смотрит только Default);
//  2. на префаб PlayerSurvivor ставит рэгдолл штатным мастером Unity (Create Ragdoll), кладёт кости на слой Hitbox
//     и вешает CharacterHealth;
//  3. в сцене ставит ботов Ally 1..4 (команда 0, голубоватый материал) и Enemy 1..5 (команда 1, красноватый):
//     экземпляры PlayerSurvivor с выключенным PlayerInput, SurvivorShooter в режиме бота и ShooterBot;
//     игрок получает команду 0;
//  4. ArenaPlayerSpawn получает игрока, камера игрока перестаёт упираться в кости (слой Hitbox);
//  5. ArenaNavMesh строит NavMesh на 2 клетки на воксель вместо 3 (воксель стал вдвое мельче, сборка шла 11 с);
//  6. сохраняет префаб и сцену.
// Скрипт можно запускать повторно: готовое он не трогает.
// Запуск: unity command run_script --file Tools/Unity/SetupArenaBot.cs --project-path "D:\Game Tactick"
public static class SetupArenaBot
{
    const string ScenePath = "Assets/Scenes/SampleScene.unity";
    const string PrefabPath = "Assets/Characters/Survivor/PlayerSurvivor.prefab";
    const string MaterialPath = "Assets/Characters/Survivor/Materials/SurvivorRigged.mat";
    const string EnemyMaterialPath = "Assets/Characters/Survivor/Materials/SurvivorRigged_Enemy.mat";
    const string AllyMaterialPath = "Assets/Characters/Survivor/Materials/SurvivorRigged_Ally.mat";
    static readonly Color EnemyTint = new(1f, 0.45f, 0.4f);
    static readonly Color AllyTint = new(0.55f, 0.7f, 1f);
    const string HitboxLayer = "Hitbox";
    const string AllyName = "Ally";
    const string EnemyName = "Enemy";
    // Команда 5 на 5: игрок и 4 союзника против 5 врагов
    const int AllyCount = 4;
    const int EnemyCount = 5;

    public static string Main()
    {
        var log = new StringBuilder();
        int hitbox = EnsureLayer(HitboxLayer, log);

        // 2. Префаб
        var root = PrefabUtility.LoadPrefabContents(PrefabPath);
        if (root.GetComponentsInChildren<Rigidbody>(true).Length == 0)
        {
            BuildRagdoll(root.GetComponent<Animator>());
            log.AppendLine($"ragdoll built: {root.GetComponentsInChildren<Rigidbody>(true).Length} bodies");
        }
        foreach (var body in root.GetComponentsInChildren<Rigidbody>(true))
            body.gameObject.layer = hitbox;
        CloseNeckGap(root.GetComponent<Animator>(), log);
        if (root.GetComponent<CharacterHealth>() == null)
            root.AddComponent<CharacterHealth>();
        PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        PrefabUtility.UnloadPrefabContents(root);

        // 3. Сцена
        var active = EditorSceneManager.GetActiveScene();
        if (active.path != ScenePath)
        {
            if (active.isDirty)
                return $"scene {active.path} has unsaved changes, save or discard them first";
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }
        var scene = EditorSceneManager.GetActiveScene();

        var shooters = Object.FindObjectsByType<SurvivorShooter>(FindObjectsSortMode.None);
        var player = shooters.Single(s => !new SerializedObject(s).FindProperty("botControlled").boolValue);
        SetTeam(player.gameObject, 0);

        // Бот из первой версии (один противник) становится первым врагом
        var oldBot = shooters.SingleOrDefault(s => s.name == "EnemyBot");
        if (oldBot != null)
            oldBot.name = EnemyName + " 1";

        var arena = Object.FindObjectsByType<VoxelArena>(FindObjectsSortMode.None).Single();
        var navMesh = arena.GetComponent<ArenaNavMesh>();
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        for (int i = 1; i <= AllyCount; i++)
            SetupBot(AllyName + " " + i, 0, TintedMaterial(AllyMaterialPath, AllyTint), prefab, scene, navMesh, log);
        for (int i = 1; i <= EnemyCount; i++)
            SetupBot(EnemyName + " " + i, 1, TintedMaterial(EnemyMaterialPath, EnemyTint), prefab, scene, navMesh, log);

        // 4.
        var spawnSo = new SerializedObject(arena.GetComponent<ArenaPlayerSpawn>());
        spawnSo.FindProperty("player").objectReferenceValue = player.GetComponent<CharacterHealth>();
        spawnSo.ApplyModifiedPropertiesWithoutUndo();

        var follow = Object.FindObjectsByType<CinemachineThirdPersonFollow>(FindObjectsSortMode.None).Single();
        var obstacles = follow.AvoidObstacles;
        obstacles.CollisionFilter &= ~(1 << hitbox);
        follow.AvoidObstacles = obstacles;
        EditorUtility.SetDirty(follow);

        // 5.
        var navSo = new SerializedObject(navMesh);
        navSo.FindProperty("navCellsPerVoxel").intValue = 2;
        navSo.ApplyModifiedPropertiesWithoutUndo();

        // 6.
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        log.AppendLine($"player {player.name}, {AllyCount} allies, {EnemyCount} enemies; camera collision filter {follow.AvoidObstacles.CollisionFilter.value}; scene saved");
        return log.ToString();
    }

    // Бот с именем name: экземпляр PlayerSurvivor, PlayerInput выключен, SurvivorShooter в режиме бота, ShooterBot
    static void SetupBot(string name, int team, Material material, GameObject prefab, UnityEngine.SceneManagement.Scene scene,
        ArenaNavMesh navMesh, StringBuilder log)
    {
        var bot = GameObject.Find(name);
        if (bot == null)
        {
            bot = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            bot.name = name;
            log.AppendLine(name + " placed");
        }

        bot.GetComponent<PlayerInput>().enabled = false;
        var shooterSo = new SerializedObject(bot.GetComponent<SurvivorShooter>());
        shooterSo.FindProperty("botControlled").boolValue = true;
        shooterSo.FindProperty("followCamera").objectReferenceValue = null;
        shooterSo.ApplyModifiedPropertiesWithoutUndo();

        var brain = bot.GetComponent<ShooterBot>() ?? bot.AddComponent<ShooterBot>();
        var brainSo = new SerializedObject(brain);
        brainSo.FindProperty("navMesh").objectReferenceValue = navMesh;
        brainSo.ApplyModifiedPropertiesWithoutUndo();

        SetTeam(bot, team);

        var smr = bot.GetComponentInChildren<SkinnedMeshRenderer>();
        smr.sharedMaterials = Enumerable.Repeat(material, smr.sharedMaterials.Length).ToArray();
        EditorUtility.SetDirty(smr);
    }

    static void SetTeam(GameObject character, int team)
    {
        var so = new SerializedObject(character.GetComponent<CharacterHealth>());
        so.FindProperty("team").intValue = team;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    // Мастер рэгдолла оставляет между коробкой груди и шаром головы шею без коллайдера (~12 см):
    // пуля в шею пролетала насквозь. Коробка груди вытягивается вверх до низа шара головы
    static void CloseNeckGap(Animator animator, StringBuilder log)
    {
        var chest = animator.GetBoneTransform(HumanBodyBones.Chest).GetComponent<BoxCollider>();
        var head = animator.GetBoneTransform(HumanBodyBones.Head).GetComponent<SphereCollider>();
        var headBottom = head.transform.TransformPoint(head.center) - Vector3.up * head.radius * head.transform.lossyScale.y;
        float target = chest.transform.InverseTransformPoint(headBottom).y;
        float bottom = chest.center.y - chest.size.y * 0.5f;
        float top = chest.center.y + chest.size.y * 0.5f;
        if (target <= top + 0.001f)
            return;
        chest.size = new Vector3(chest.size.x, target - bottom, chest.size.z);
        chest.center = new Vector3(chest.center.x, (target + bottom) * 0.5f, chest.center.z);
        log.AppendLine($"chest box extended up by {target - top:0.000} m to the head");
    }

    static int EnsureLayer(string name, StringBuilder log)
    {
        int existing = LayerMask.NameToLayer(name);
        if (existing >= 0)
            return existing;

        var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
        var layers = tagManager.FindProperty("layers");
        for (int i = 8; i < layers.arraySize; i++)
        {
            var layer = layers.GetArrayElementAtIndex(i);
            if (!string.IsNullOrEmpty(layer.stringValue))
                continue;
            layer.stringValue = name;
            tagManager.ApplyModifiedPropertiesWithoutUndo();
            log.AppendLine($"layer {name} added at {i}");
            return i;
        }
        throw new System.InvalidOperationException("no free layer for " + name);
    }

    // Мастер GameObject > 3D Object > Ragdoll... без окна: те же поля и та же кнопка Create
    static void BuildRagdoll(Animator animator)
    {
        // Мастер лежит в UnityEditor.PhysicsModule
        var type = System.AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("UnityEditor.RagdollBuilder"))
            .First(t => t != null);
        // В Unity 6 это обычный класс, а не ScriptableWizard
        var wizard = System.Activator.CreateInstance(type, true);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        void Set(string field, HumanBodyBones bone) =>
            type.GetField(field, flags).SetValue(wizard, animator.GetBoneTransform(bone));

        Set("pelvis", HumanBodyBones.Hips);
        Set("leftHips", HumanBodyBones.LeftUpperLeg);
        Set("leftKnee", HumanBodyBones.LeftLowerLeg);
        Set("leftFoot", HumanBodyBones.LeftFoot);
        Set("rightHips", HumanBodyBones.RightUpperLeg);
        Set("rightKnee", HumanBodyBones.RightLowerLeg);
        Set("rightFoot", HumanBodyBones.RightFoot);
        Set("leftArm", HumanBodyBones.LeftUpperArm);
        Set("leftElbow", HumanBodyBones.LeftLowerArm);
        Set("rightArm", HumanBodyBones.RightUpperArm);
        Set("rightElbow", HumanBodyBones.RightLowerArm);
        Set("middleSpine", HumanBodyBones.Chest);
        Set("head", HumanBodyBones.Head);
        type.GetField("totalMass", flags).SetValue(wizard, 70f);

        type.GetMethod("OnWizardUpdate", flags).Invoke(wizard, null);
        type.GetMethod("OnWizardCreate", flags).Invoke(wizard, null);
    }

    // Копия материала выжившего с оттенком tint; готовую копию берёт как есть
    static Material TintedMaterial(string path, Color tint)
    {
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material != null)
            return material;
        AssetDatabase.CopyAsset(MaterialPath, path);
        material = AssetDatabase.LoadAssetAtPath<Material>(path);
        material.SetColor("_BaseColor", tint);
        EditorUtility.SetDirty(material);
        AssetDatabase.SaveAssets();
        return material;
    }
}
