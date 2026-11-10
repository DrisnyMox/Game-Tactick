using System.Linq;
using System.Text;
using Unity.Cinemachine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Ставит управляемого выжившего в воксельную арену сцены SampleScene:
//  1. размер вокселя арены подбирает так, чтобы рост выжившего был PlayerHeightInVoxels блоков,
//     а клетку раскладки делает из CellVoxels вокселей, чтобы карта в метрах осталась прежней;
//  2. выключает ArenaTestController: он сам ведёт камеру и занимает WASD и ЛКМ;
//  3. вешает на арену VoxelArenaShootable (пули выбивают воксели) и ArenaPlayerSpawn (игрок встаёт на свою базу);
//  4. ставит PlayerSurvivor, на Main Camera вешает CinemachineBrain, ставит PlayerFollowCamera из Starter Assets
//     и направляет её на PlayerCameraRoot игрока;
//  5. сохраняет сцену.
// Если открытая сцена с несохранёнными правками, скрипт ничего не делает: SampleScene открывается вместо неё.
// Запуск через Unity CLI (из корня проекта):
//     unity command run_script --file Tools/Unity/SetupArenaPlayer.cs --project-path "D:\Game Tactick"
public static class SetupArenaPlayer
{
    const string ScenePath = "Assets/Scenes/SampleScene.unity";
    const string PlayerPath = "Assets/Characters/Survivor/PlayerSurvivor.prefab";
    const string FollowCameraPath = "Assets/Starter Assets/Runtime/ThirdPersonController/Prefabs/PlayerFollowCamera.prefab";
    const int PlayerHeightInVoxels = 8;
    // Раскладка арены рассчитана на клетку 0,5 м (танк ~2 м, стены 2 м). Рост 8 вокселей даёт воксель
    // 0,226 м, две таких — 0,45 м: карта в метрах почти прежняя
    const int CellVoxels = 2;
    static readonly Vector3Int ArenaCells = new(400, 48, 400);

    public static string Main()
    {
        var log = new StringBuilder();
        var active = EditorSceneManager.GetActiveScene();
        if (active.path != ScenePath)
        {
            if (active.isDirty)
                return $"scene {active.path} has unsaved changes, save or discard them first";
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }
        var scene = EditorSceneManager.GetActiveScene();

        // 1. Рост выжившего по мешу префаба
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPath);
        var smr = prefab.GetComponentInChildren<SkinnedMeshRenderer>();
        var heights = smr.sharedMesh.vertices.Select(v => smr.transform.TransformPoint(v).y).ToArray();
        float height = heights.Max() - heights.Min();
        float voxelSize = height / PlayerHeightInVoxels;

        var arena = Object.FindObjectsByType<VoxelArena>(FindObjectsSortMode.None).Single();
        var arenaSo = new SerializedObject(arena);
        float oldSize = arenaSo.FindProperty("voxelSize").floatValue;
        arenaSo.FindProperty("voxelSize").floatValue = voxelSize;
        // Раскладка остаётся прежней сеткой ArenaCells, клетка — CellVoxels вокселей по каждой оси
        arenaSo.FindProperty("cellVoxels").intValue = CellVoxels;
        arenaSo.FindProperty("sizeX").intValue = ArenaCells.x * CellVoxels;
        arenaSo.FindProperty("sizeY").intValue = ArenaCells.y * CellVoxels;
        arenaSo.FindProperty("sizeZ").intValue = ArenaCells.z * CellVoxels;
        arenaSo.ApplyModifiedPropertiesWithoutUndo();
        var dims = (Vector3)ArenaCells * CellVoxels;
        log.AppendLine($"player height {height:0.000} m -> voxel size {voxelSize:0.0000} m (was {oldSize}), " +
                       $"arena {dims} voxels = {dims * voxelSize} m, layout cell {voxelSize * CellVoxels:0.000} m");

        // 2. Тестовое управление арены
        var test = arena.GetComponent<ArenaTestController>();
        test.enabled = false;
        log.AppendLine("ArenaTestController disabled");

        // 3-4. Игрок, стрельба по арене, камера
        var player = Object.FindObjectsByType<SurvivorShooter>(FindObjectsSortMode.None).SingleOrDefault()?.gameObject;
        if (player == null)
        {
            player = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            log.AppendLine("PlayerSurvivor placed");
        }
        if (arena.GetComponent<VoxelArenaShootable>() == null)
            arena.gameObject.AddComponent<VoxelArenaShootable>();
        var spawn = arena.GetComponent<ArenaPlayerSpawn>() ?? arena.gameObject.AddComponent<ArenaPlayerSpawn>();
        var spawnSo = new SerializedObject(spawn);
        // CharacterHealth на префаб ставит Tools/Unity/SetupArenaBot.cs, он же ставит бота в поле bot
        spawnSo.FindProperty("player").objectReferenceValue = player.GetComponent<CharacterHealth>();
        spawnSo.ApplyModifiedPropertiesWithoutUndo();

        var mainCamera = GameObject.FindGameObjectWithTag("MainCamera");
        if (mainCamera.GetComponent<CinemachineBrain>() == null)
            mainCamera.AddComponent<CinemachineBrain>();

        var follow = Object.FindObjectsByType<CinemachineThirdPersonFollow>(FindObjectsSortMode.None).SingleOrDefault();
        if (follow == null)
        {
            var go = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(FollowCameraPath), scene);
            follow = go.GetComponent<CinemachineThirdPersonFollow>();
            log.AppendLine("PlayerFollowCamera placed");
        }
        var cameraRoot = player.GetComponent<StarterAssets.ThirdPersonController>().CinemachineCameraTarget.transform;
        var vcam = follow.GetComponent<CinemachineCamera>();
        vcam.Target.TrackingTarget = cameraRoot;
        EditorUtility.SetDirty(vcam);

        var shooterSo = new SerializedObject(player.GetComponent<SurvivorShooter>());
        shooterSo.FindProperty("followCamera").objectReferenceValue = follow;
        shooterSo.ApplyModifiedPropertiesWithoutUndo();

        // 5.
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        log.AppendLine($"camera {follow.name} follows {cameraRoot.name}; scene saved, arena layer {LayerMask.LayerToName(arena.gameObject.layer)}");
        return log.ToString();
    }
}
