using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Меню Tools/Voxel Arena/Create Test Setup — арена с тестовым управлением
/// (взрывы по клику, агенты, замеры). Запускать в пустой сцене.
/// В исходном проекте здесь был ещё «Create Match Setup» (сцена боя); он завязан на геймплей
/// (RtsCamera, MatchManager, HUD), которого в этом проекте нет, поэтому не переносился.
/// </summary>
public static class VoxelArenaSetup
{
    const string MaterialPath = "Assets/VoxelArena/VoxelArena.mat";

    [MenuItem("Tools/Voxel Arena/Create Test Setup")]
    static void CreateTestSetup()
    {
        if (!CreateArena(out var arena))
            return;

        arena.gameObject.AddComponent<ArenaTestController>();
        EnsureCameraAndLight();
        Finish(arena.gameObject);
    }

    static bool CreateArena(out VoxelArena arena)
    {
        arena = Object.FindAnyObjectByType<VoxelArena>();
        if (arena != null)
        {
            Debug.Log("В сцене уже есть арена. Создайте пустую сцену и запустите меню ещё раз.", arena);
            Selection.activeGameObject = arena.gameObject;
            arena = null;
            return false;
        }

        var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        if (material == null)
        {
            var shader = Shader.Find("NoobGenerals/VoxelArena");
            if (shader == null)
            {
                Debug.LogError("Не найден шейдер NoobGenerals/VoxelArena: проверьте, что Assets/VoxelArena/Shaders/VoxelArena.shader скомпилировался без ошибок.");
                return false;
            }

            material = new Material(shader);
            AssetDatabase.CreateAsset(material, MaterialPath);
        }

        var arenaObject = new GameObject("Voxel Arena");
        arena = arenaObject.AddComponent<VoxelArena>();
        SetReference(arena, "material", material);
        arenaObject.AddComponent<ArenaNavMesh>();
        Undo.RegisterCreatedObjectUndo(arenaObject, "Create Voxel Arena");
        return true;
    }

    static Camera EnsureCameraAndLight()
    {
        var camera = Camera.main;
        if (camera == null)
        {
            var cameraObject = new GameObject("Main Camera") { tag = "MainCamera" };
            camera = cameraObject.AddComponent<Camera>();
            cameraObject.AddComponent<AudioListener>();
            Undo.RegisterCreatedObjectUndo(cameraObject, "Create Camera");
        }

        if (Object.FindAnyObjectByType<Light>() == null)
        {
            var lightObject = new GameObject("Directional Light");
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.shadows = LightShadows.Soft;
            light.intensity = 1.1f;
            lightObject.transform.rotation = Quaternion.Euler(50f, -35f, 0f);
            Undo.RegisterCreatedObjectUndo(lightObject, "Create Light");
        }

        return camera;
    }

    static void SetReference(Object target, string field, Object value)
    {
        var serialized = new SerializedObject(target);
        serialized.FindProperty(field).objectReferenceValue = value;
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }

    static void Finish(GameObject select)
    {
        Selection.activeGameObject = select;
        EditorSceneManager.MarkSceneDirty(select.scene);
    }
}
