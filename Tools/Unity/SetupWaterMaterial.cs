using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Материал воды арены:
//  1. материал Assets/VoxelArena/VoxelWater.mat на шейдере NoobGenerals/VoxelWater (если его ещё нет);
//  2. в сценах SampleScene и Voxel Arena ставит его в поле waterMaterial у VoxelArena и сохраняет сцены;
//  3. возвращает в редактор сцену, которая была открыта.
// Открытая сцена с несохранёнными правками — ошибка: скрипт открывает другие сцены и правки бы пропали.
// Запуск: unity command run_script --file Tools/Unity/SetupWaterMaterial.cs --project-path "<путь к проекту>"
public static class SetupWaterMaterial
{
    const string MaterialPath = "Assets/VoxelArena/VoxelWater.mat";
    const string ShaderName = "NoobGenerals/VoxelWater";
    static readonly string[] ScenePaths =
    {
        "Assets/Scenes/SampleScene.unity",
        "Assets/Scenes/Voxel Arena.unity",
    };

    public static string Main()
    {
        var active = EditorSceneManager.GetActiveScene();
        if (active.isDirty)
            throw new System.InvalidOperationException($"В сцене {active.path} есть несохранённые правки: сохраните их и запустите снова");
        string activePath = active.path;

        var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        if (material == null)
        {
            material = new Material(Shader.Find(ShaderName));
            AssetDatabase.CreateAsset(material, MaterialPath);
            AssetDatabase.SaveAssets();
        }

        var report = new StringBuilder($"{MaterialPath} ({material.shader.name})");
        foreach (var path in ScenePaths)
        {
            var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            var arena = Object.FindObjectsByType<VoxelArena>().Single();
            var so = new SerializedObject(arena);
            so.FindProperty("waterMaterial").objectReferenceValue = material;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            report.Append($"; {path}: {arena.name}");
        }

        if (!string.IsNullOrEmpty(activePath))
            EditorSceneManager.OpenScene(activePath, OpenSceneMode.Single);
        return report.ToString();
    }
}
