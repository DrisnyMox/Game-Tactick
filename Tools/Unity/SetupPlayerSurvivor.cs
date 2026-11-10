using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Делает управляемого выжившего из ассета Starter Assets (Third Person Controller):
//  1. ставит SurvivorRigged.fbx масштаб ImportScale, чтобы рост был как у робота из ассета (1,8 м),
//     и заново собирает аватар Humanoid пунктом SurvivorRiggedAvatarSetup;
//  2. копирует префаб PlayerArmature: контроллер, аниматор с анимациями ассета, ввод, звуки шагов, точка для камеры.
//     Робота (Geometry и Skeleton) заменяет костями и мешем выжившего, аниматору ставит аватар выжившего.
//     Сохраняет как PlayerPrefabPath;
//  3. копирует сцену Playground из ассета (площадка, MainCamera, PlayerFollowCamera) в ScenePath
//     и ставит в неё нового игрока вместо PlayerArmature. Все ссылки сцены на старого игрока
//     (камера следит за PlayerCameraRoot, экранные стики пишут в StarterAssetsInputs) переводит на нового.
// Открытую сцену не трогает: сцена-копия открывается рядом и закрывается после сохранения.
// Запуск через Unity CLI (из корня проекта):
//     unity command run_script --file Tools/Unity/SetupPlayerSurvivor.cs --project-path "D:\Game Tactick"
public static class SetupPlayerSurvivor
{
    const string ModelPath = "Assets/Characters/Survivor/SurvivorRigged.fbx";
    const float ImportScale = 1.6f;
    const string SourcePrefabPath = "Assets/Starter Assets/Runtime/ThirdPersonController/Prefabs/PlayerArmature.prefab";
    const string PlayerPrefabPath = "Assets/Characters/Survivor/PlayerSurvivor.prefab";
    const string SourceScenePath = "Assets/Starter Assets/Sample/ThirdPersonController/Playground.unity";
    const string ScenePath = "Assets/Scenes/SurvivorPlayground.unity";

    public static string Main()
    {
        var log = new StringBuilder();

        // 1. Рост и аватар
        var importer = (ModelImporter)AssetImporter.GetAtPath(ModelPath);
        if (!Mathf.Approximately(importer.globalScale, ImportScale))
        {
            importer.globalScale = ImportScale;
            importer.SaveAndReimport();
        }
        VoxelCharacterSystem.Editor.SurvivorRiggedAvatarSetup.Setup();
        var avatar = AssetDatabase.LoadAllAssetsAtPath(ModelPath).OfType<Avatar>().Single();
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
        var bounds = model.GetComponentInChildren<SkinnedMeshRenderer>().bounds;
        log.AppendLine($"avatar human={avatar.isHuman} valid={avatar.isValid}, height={bounds.size.y:0.00} m");

        // 2. Префаб игрока
        var player = (GameObject)Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(SourcePrefabPath));
        player.name = "PlayerSurvivor";
        Object.DestroyImmediate(player.transform.Find("Geometry").gameObject);
        Object.DestroyImmediate(player.transform.Find("Skeleton").gameObject);
        var shell = (GameObject)Object.Instantiate(model);
        foreach (var child in shell.transform.Cast<Transform>().ToList())
            child.SetParent(player.transform, false);
        Object.DestroyImmediate(shell);
        player.GetComponent<Animator>().avatar = avatar;
        PrefabUtility.SaveAsPrefabAsset(player, PlayerPrefabPath);
        Object.DestroyImmediate(player);
        log.AppendLine($"prefab {PlayerPrefabPath}: " +
                       string.Join(", ", AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath).transform.Cast<Transform>().Select(t => t.name)));

        // 3. Сцена
        // Сцена собирается заново: ссылки старой копии на прежний префаб после пересохранения не годятся.
        // Если её открыл автор, правки в ней пропадают: сцену целиком делает этот скрипт.
        bool wasOpen = SceneManager.GetSceneByPath(ScenePath).isLoaded;
        if (wasOpen)
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        AssetDatabase.DeleteAsset(ScenePath);
        AssetDatabase.CopyAsset(SourceScenePath, ScenePath);
        var scene = EditorSceneManager.OpenScene(ScenePath, wasOpen ? OpenSceneMode.Single : OpenSceneMode.Additive);
        var oldPlayer = scene.GetRootGameObjects().Single(g => g.name == "PlayerArmature");
        var newPlayer = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath), scene);
        newPlayer.transform.SetPositionAndRotation(oldPlayer.transform.position, oldPlayer.transform.rotation);

        var remap = BuildRemap(oldPlayer, newPlayer);
        foreach (var component in scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Component>(true)))
        {
            if (component == null || component.transform.IsChildOf(oldPlayer.transform) || component.transform.IsChildOf(newPlayer.transform))
                continue;
            var so = new SerializedObject(component);
            var it = so.GetIterator();
            bool changed = false;
            while (it.Next(true))
            {
                if (it.propertyType == SerializedPropertyType.ObjectReference && it.objectReferenceValue != null
                    && remap.TryGetValue(it.objectReferenceValue, out var replacement))
                {
                    it.objectReferenceValue = replacement;
                    changed = true;
                    log.AppendLine($"relinked {component.gameObject.name}.{component.GetType().Name}.{it.propertyPath} -> {replacement.name}");
                }
            }
            if (changed)
                so.ApplyModifiedPropertiesWithoutUndo();
        }
        Object.DestroyImmediate(oldPlayer);
        CheckPose(newPlayer, log);
        EditorSceneManager.SaveScene(scene);
        if (!wasOpen)
            EditorSceneManager.CloseScene(scene, true);
        log.AppendLine($"scene {ScenePath} saved");
        return log.ToString();
    }

    // Ставит игрока в позы из анимаций ассета и пишет в отчёт, куда смотрят носки и где руки.
    // Персонаж смотрит вперёд, если носки дальше пяток по оси вперёд игрока. В стойке кисти ниже плеч.
    // Позу ставит на отдельной копии, чтобы в сцену не попала поза из середины шага.
    static void CheckPose(GameObject scenePlayer, StringBuilder log)
    {
        var player = Object.Instantiate(scenePlayer);
        var animator = player.GetComponent<Animator>();
        var root = player.transform;
        foreach (var clip in animator.runtimeAnimatorController.animationClips
                     .Where(c => c.name.Contains("Idle") || c.name.Contains("Walk_N")).Distinct())
        {
            clip.SampleAnimation(player, clip.length * 0.25f);
            Vector3 Local(HumanBodyBones b) => root.InverseTransformPoint(animator.GetBoneTransform(b).position);
            log.AppendLine($"pose {clip.name}: toes-foot forward L={Local(HumanBodyBones.LeftToes).z - Local(HumanBodyBones.LeftFoot).z:+0.000;-0.000} " +
                           $"R={Local(HumanBodyBones.RightToes).z - Local(HumanBodyBones.RightFoot).z:+0.000;-0.000}, " +
                           $"hand y L={Local(HumanBodyBones.LeftHand).y:0.00} R={Local(HumanBodyBones.RightHand).y:0.00}, " +
                           $"shoulder y={Local(HumanBodyBones.LeftUpperArm).y:0.00}, head y={Local(HumanBodyBones.Head).y:0.00}, " +
                           $"left hand x={Local(HumanBodyBones.LeftHand).x:+0.00;-0.00}");
        }
        Object.DestroyImmediate(player);
    }

    // Старый объект или компонент игрока -> такой же по пути и типу у нового игрока
    static Dictionary<Object, Object> BuildRemap(GameObject oldRoot, GameObject newRoot)
    {
        var map = new Dictionary<Object, Object>();
        foreach (var oldT in oldRoot.GetComponentsInChildren<Transform>(true))
        {
            var path = AnimationUtility.CalculateTransformPath(oldT, oldRoot.transform);
            var newT = path.Length == 0 ? newRoot.transform : newRoot.transform.Find(path);
            if (newT == null)
                continue;
            map[oldT.gameObject] = newT.gameObject;
            foreach (var c in oldT.GetComponents<Component>())
            {
                var match = newT.GetComponent(c.GetType());
                if (match != null)
                    map[c] = match;
            }
        }
        return map;
    }
}
