using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

// Собирает осколки выбитых вокселей:
//  1. материал Assets/VoxelArena/VoxelDebris.mat - URP Particles/Lit, непрозрачный, цвет из цвета частицы;
//  2. в открытой сцене объект «Voxel Debris» под ареной: система частиц (кубы в мировых осях, гравитация,
//     отскок от земли, сжатие в конце жизни) и компонент VoxelDebris со ссылкой на арену;
//  3. сохраняет сцену.
// Повторный запуск пересобирает объект заново.
// Запуск: unity command run_script --file Tools/Unity/SetupVoxelDebris.cs --project-path "D:\Game Tactick"
public static class SetupVoxelDebris
{
    const string MaterialPath = "Assets/VoxelArena/VoxelDebris.mat";
    const string ObjectName = "Voxel Debris";
    const int MaxParticles = 100000;

    public static string Main()
    {
        var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        if (material == null)
        {
            material = new Material(Shader.Find("Universal Render Pipeline/Particles/Lit"));
            AssetDatabase.CreateAsset(material, MaterialPath);
        }
        material.SetFloat("_Surface", 0f);
        material.SetColor("_BaseColor", Color.white);
        material.enableInstancing = true;
        EditorUtility.SetDirty(material);
        AssetDatabase.SaveAssets();

        var scene = EditorSceneManager.GetActiveScene();
        var arena = Object.FindObjectsByType<VoxelArena>(FindObjectsSortMode.None).Single();
        var old = arena.transform.Find(ObjectName);
        if (old != null)
            Object.DestroyImmediate(old.gameObject);

        var go = new GameObject(ObjectName);
        go.transform.SetParent(arena.transform, false);
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = ps.main;
        main.loop = false;
        main.playOnAwake = false;
        main.duration = 1f;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startSize3D = true;
        main.startRotation3D = true;
        main.gravityModifier = 1f;
        main.maxParticles = MaxParticles;
        main.scalingMode = ParticleSystemScalingMode.Shape;

        var emission = ps.emission;
        emission.enabled = false;
        var shape = ps.shape;
        shape.enabled = false;

        // Сопротивление воздуха: осколки быстро гасят скорость и не улетают далеко
        var limit = ps.limitVelocityOverLifetime;
        limit.enabled = true;
        limit.limit = 100f;
        limit.drag = 0.8f;

        // Отскок от земли и стен арены (слой Default)
        var collision = ps.collision;
        collision.enabled = true;
        collision.type = ParticleSystemCollisionType.World;
        collision.mode = ParticleSystemCollisionMode.Collision3D;
        collision.quality = ParticleSystemCollisionQuality.Medium;
        collision.collidesWith = 1;
        collision.bounce = 0.25f;
        collision.dampen = 0.4f;
        collision.lifetimeLoss = 0f;
        collision.radiusScale = 0.5f;

        // Последнюю треть жизни кубик сжимается до нуля
        var size = ps.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(0.65f, 1f), new Keyframe(1f, 0f)));

        var renderer = go.GetComponent<ParticleSystemRenderer>();
        renderer.renderMode = ParticleSystemRenderMode.Mesh;
        renderer.mesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
        renderer.alignment = ParticleSystemRenderSpace.World;
        renderer.sharedMaterial = material;
        renderer.enableGPUInstancing = true;
        renderer.shadowCastingMode = ShadowCastingMode.On;
        renderer.receiveShadows = true;

        var debris = go.AddComponent<VoxelDebris>();
        var so = new SerializedObject(debris);
        so.FindProperty("arena").objectReferenceValue = arena;
        so.ApplyModifiedPropertiesWithoutUndo();

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        return $"{ObjectName} added under {arena.name}, material {MaterialPath} ({material.shader.name})";
    }
}
