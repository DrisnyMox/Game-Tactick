using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Unity.Cinemachine;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.Rendering;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

// Собирает стрельбу выжившего из ассетов Invector, скопированных в Assets/ThirdParty/Invector
// (их копирует Tools/Unity/CopyAssetsWithDependencies.ps1):
//  1. анимациям Invector ставит аватар "из этой модели": их исходный аватар (vBot) не скопирован;
//  2. материалы Invector переводит на URP: штатным обновлятелем URP, а старые шейдеры частиц - вручную;
//  3. из префабов попаданий и следов убирает компоненты, у которых нет скрипта (скрипты Invector не копировались);
//  4. собирает префаб винтовки SurvivorRifle из винтовки Invector и вешает на него ShooterWeapon;
//  5. копирует контроллер аниматора Starter Assets и добавляет в копию: шаги с прицелом на базовый слой
//     и слой "Rifle" для верха тела (держит, целится, выстрел, перезарядка);
//  6. на PlayerSurvivor ставит новый контроллер и SurvivorShooter. Положение винтовки в правой кисти считает
//     по позе Aiming@Rifle: правая ладонь на рукояти, ствол вперёд по телу;
//  7. в открытой сцене SurvivorPlayground отдаёт SurvivorShooter камеру PlayerFollowCamera.
// Запуск через Unity CLI (из корня проекта):
//     unity command run_script --file Tools/Unity/SetupSurvivorShooter.cs --project-path "D:\Game Tactick"
public static class SetupSurvivorShooter
{
    const string Invector = "Assets/ThirdParty/Invector/Shooter/";
    const string AnimFolder = Invector + "3DModels/Animations/";
    const string SourceRifle = Invector + "Prefabs/Weapon/_weapons_WITHOUT_Inventory/_weaponsPrefabs/vAssaultRifle_NoInventory.prefab";
    const string ImpactFolder = Invector + "Scripts/DecalManager/BulletEffect/Resources/prefabs/";
    const string DecalFolder = Invector + "Scripts/DecalManager/Decals/";
    const string AudioFolder = Invector + "Audio/Weapons/";

    const string SurvivorFolder = "Assets/Characters/Survivor/";
    const string RiflePath = SurvivorFolder + "Weapons/SurvivorRifle.prefab";
    const string MaskPath = SurvivorFolder + "Animations/SurvivorUpperBody.mask";
    const string SourceController = "Assets/Starter Assets/Runtime/ThirdPersonController/Character/Animations/StarterAssetsThirdPerson.controller";
    const string ControllerPath = SurvivorFolder + "Animations/SurvivorShooter.controller";
    const string PlayerPath = SurvivorFolder + "PlayerSurvivor.prefab";
    const string ScenePath = "Assets/Scenes/SurvivorPlayground.unity";

    static StringBuilder log;

    public static string Main()
    {
        log = new StringBuilder();
        EnsureFolder(SurvivorFolder + "Weapons");
        EnsureFolder(SurvivorFolder + "Animations");

        FixAnimationAvatars();
        ConvertMaterials();
        StripMissingScripts(new[] { "impactConcrete", "impactMetal", "impactWood", "impactDirt" }.Select(n => ImpactFolder + n + ".prefab")
            .Concat(new[] { DecalFolder + "vDecal_ConcreteA.prefab" }));
        BuildRifle();
        var mask = BuildMask();
        var controller = BuildController(mask);
        SetupPlayer(controller);
        SetupScene();
        AssetDatabase.SaveAssets();
        return log.ToString();
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path))
            return;
        var parent = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
        AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(path));
    }

    static AnimationClip Clip(string fbx, string name)
    {
        return AssetDatabase.LoadAllAssetsAtPath(AnimFolder + fbx + ".fbx").OfType<AnimationClip>().Single(c => c.name == name);
    }

    // 1 ------------------------------------------------------------------------------------------
    static void FixAnimationAvatars()
    {
        foreach (var fbx in new[] { "Shooter_UpperBodyPoses", "Shooter_Shot&Reload", "Shooter_StrafeMoveset" })
        {
            var path = AnimFolder + fbx + ".fbx";
            var importer = (ModelImporter)AssetImporter.GetAtPath(path);
            if (importer.avatarSetup != ModelImporterAvatarSetup.CreateFromThisModel)
            {
                importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                importer.SaveAndReimport();
            }
            var avatar = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Avatar>().Single();
            log.AppendLine($"anim {fbx}: avatar human={avatar.isHuman} valid={avatar.isValid}");
        }
    }

    // 2 ------------------------------------------------------------------------------------------
    static void ConvertMaterials()
    {
        // Список обновлятелей URP закрыт, берётся через рефлексию (в URP 17 он в BuiltInToURP3DMaterialUpgrader)
        var urpUpgrader = Type.GetType("UnityEditor.Rendering.Universal.BuiltInToURP3DMaterialUpgrader, Unity.RenderPipelines.Universal.Editor");
        var fetch = urpUpgrader.GetMethod("FetchMaterialUpgraders", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        var upgraders = (List<MaterialUpgrader>)fetch.Invoke(null, null);

        var particlesUnlit = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { "Assets/ThirdParty/Invector" }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            var before = mat.shader.name;
            if (before.StartsWith("Universal Render Pipeline"))
                continue;

            if (before.StartsWith("Legacy Shaders/Particles/"))
                ConvertLegacyParticle(mat, particlesUnlit, before.EndsWith("Additive"));
            else if (upgraders.Any(u => u.OldShaderPath == before))
                MaterialUpgrader.Upgrade(mat, upgraders, MaterialUpgrader.UpgradeFlags.None);
            EditorUtility.SetDirty(mat);
            log.AppendLine($"material {mat.name}: {before} -> {mat.shader.name}");
        }
    }

    // Старые шейдеры частиц URP не рисует. Перевод на Particles/Unlit: прозрачный, смешение как было
    static void ConvertLegacyParticle(Material mat, Shader shader, bool additive)
    {
        var tex = mat.GetTexture("_MainTex");
        var tint = mat.HasProperty("_TintColor") ? mat.GetColor("_TintColor") : Color.white;
        mat.shader = shader;
        mat.SetTexture("_BaseMap", tex);
        // Старый Additive умножал цвет на 2
        mat.SetColor("_BaseColor", additive ? tint * 2f : tint);
        mat.SetFloat("_Surface", 1f);
        mat.SetFloat("_Blend", additive ? 2f : 0f);
        mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        mat.SetFloat("_DstBlend", (float)(additive ? UnityEngine.Rendering.BlendMode.One : UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha));
        mat.SetFloat("_SrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
        mat.SetFloat("_DstBlendAlpha", (float)(additive ? UnityEngine.Rendering.BlendMode.One : UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha));
        mat.SetFloat("_ZWrite", 0f);
        mat.SetOverrideTag("RenderType", "Transparent");
        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
    }

    // 3 ------------------------------------------------------------------------------------------
    static void StripMissingScripts(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            var root = PrefabUtility.LoadPrefabContents(path);
            int removed = root.GetComponentsInChildren<Transform>(true).Sum(t => GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject));
            PrefabUtility.SaveAsPrefabAsset(root, path);
            PrefabUtility.UnloadPrefabContents(root);
            log.AppendLine($"prefab {System.IO.Path.GetFileName(path)}: removed {removed} missing scripts");
        }
    }

    // 4 ------------------------------------------------------------------------------------------
    static void BuildRifle()
    {
        var source = PrefabUtility.LoadPrefabContents(SourceRifle);
        var renderer = source.transform.Find("renderer");
        var rifle = new GameObject("SurvivorRifle");
        // Локальное положение модели относительно корня - как у Invector: корень там висит на кисти
        renderer.SetParent(rifle.transform, false);
        renderer.name = "Model";
        PrefabUtility.UnloadPrefabContents(source);

        Object.DestroyImmediate(renderer.Find("ScopeTarget").gameObject);
        foreach (var t in rifle.GetComponentsInChildren<Transform>(true))
            GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);

        var particles = renderer.Find("Particles");
        foreach (var ps in particles.GetComponentsInChildren<ParticleSystem>(true))
        {
            var main = ps.main;
            main.playOnAwake = false;
            // Дым и гильзы остаются в мире, а не летят вместе с винтовкой
            if (ps.name.StartsWith("Smoke") || ps.name.StartsWith("Capsule"))
                main.simulationSpace = ParticleSystemSimulationSpace.World;
        }

        var weapon = rifle.AddComponent<ShooterWeapon>();
        var so = new SerializedObject(weapon);
        so.FindProperty("Muzzle").objectReferenceValue = renderer.Find("muzzle");
        so.FindProperty("LeftHandGrip").objectReferenceValue = renderer.Find("leftHandIK");
        var muzzleParticles = particles.GetComponentsInChildren<ParticleSystem>(true).Where(p => !p.name.StartsWith("Capsule")).ToArray();
        var list = so.FindProperty("muzzleParticles");
        list.arraySize = muzzleParticles.Length;
        for (int i = 0; i < muzzleParticles.Length; i++)
            list.GetArrayElementAtIndex(i).objectReferenceValue = muzzleParticles[i];
        so.FindProperty("shellParticles").objectReferenceValue = particles.Find("Capsule_rif").GetComponent<ParticleSystem>();
        so.FindProperty("flashLight").objectReferenceValue = renderer.Find("Point light").GetComponent<Light>();
        so.FindProperty("audioSource").objectReferenceValue = renderer.Find("AudioSource").GetComponent<AudioSource>();
        so.FindProperty("fireClip").objectReferenceValue = AssetDatabase.LoadAssetAtPath<AudioClip>(AudioFolder + "Rifle@Fire.wav");
        so.FindProperty("reloadClip").objectReferenceValue = AssetDatabase.LoadAssetAtPath<AudioClip>(AudioFolder + "Rifle@Reload.wav");
        so.FindProperty("emptyClip").objectReferenceValue = AssetDatabase.LoadAssetAtPath<AudioClip>(AudioFolder + "Rifle@EmptyClip.wav");
        so.ApplyModifiedPropertiesWithoutUndo();

        PrefabUtility.SaveAsPrefabAsset(rifle, RiflePath);
        Object.DestroyImmediate(rifle);
        log.AppendLine($"rifle {RiflePath}: muzzle particles {muzzleParticles.Length}");
    }

    // 5 ------------------------------------------------------------------------------------------
    static AvatarMask BuildMask()
    {
        var mask = new AvatarMask();
        for (int i = 0; i < (int)AvatarMaskBodyPart.LastBodyPart; i++)
        {
            var part = (AvatarMaskBodyPart)i;
            bool upper = part is AvatarMaskBodyPart.Body or AvatarMaskBodyPart.Head or AvatarMaskBodyPart.LeftArm or AvatarMaskBodyPart.RightArm
                or AvatarMaskBodyPart.LeftFingers or AvatarMaskBodyPart.RightFingers or AvatarMaskBodyPart.LeftHandIK or AvatarMaskBodyPart.RightHandIK;
            mask.SetHumanoidBodyPartActive(part, upper);
        }
        AssetDatabase.DeleteAsset(MaskPath);
        AssetDatabase.CreateAsset(mask, MaskPath);
        return mask;
    }

    static AnimatorController BuildController(AvatarMask mask)
    {
        AssetDatabase.DeleteAsset(ControllerPath);
        AssetDatabase.CopyAsset(SourceController, ControllerPath);
        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
        controller.AddParameter("Aiming", AnimatorControllerParameterType.Bool);
        controller.AddParameter("Shoot", AnimatorControllerParameterType.Trigger);
        controller.AddParameter("Reload", AnimatorControllerParameterType.Trigger);
        controller.AddParameter("AimX", AnimatorControllerParameterType.Float);
        controller.AddParameter("AimY", AnimatorControllerParameterType.Float);

        // Базовый слой: шаги с прицелом
        var baseSm = controller.layers[0].stateMachine;
        var locomotionChild = baseSm.states.Single(s => s.state.name == "Idle Walk Run Blend");
        var locomotion = locomotionChild.state;
        var strafe = baseSm.AddState("Aim Strafe", locomotionChild.position + new Vector3(0, 120));
        var tree = new BlendTree { name = "Aim Strafe", blendType = BlendTreeType.SimpleDirectional2D, blendParameter = "AimX", blendParameterY = "AimY", hideFlags = HideFlags.HideInHierarchy };
        AssetDatabase.AddObjectToAsset(tree, controller);
        tree.AddChild(Clip("Shooter_StrafeMoveset", "AimedIdle"), Vector2.zero);
        tree.AddChild(Clip("Shooter_StrafeMoveset", "AimedWalk"), new Vector2(0, 1));
        tree.AddChild(Clip("Shooter_StrafeMoveset", "AimedWalkBack"), new Vector2(0, -1));
        tree.AddChild(Clip("Shooter_StrafeMoveset", "AimedWalkLeft"), new Vector2(-1, 0));
        tree.AddChild(Clip("Shooter_StrafeMoveset", "AimedWalkRight"), new Vector2(1, 0));
        strafe.motion = tree;

        // Прыжок и падение из шагов с прицелом - как из обычной ходьбы
        foreach (var source in locomotion.transitions)
        {
            var copy = strafe.AddTransition(source.destinationState);
            copy.hasExitTime = source.hasExitTime;
            copy.exitTime = source.exitTime;
            copy.duration = source.duration;
            foreach (var c in source.conditions)
                copy.AddCondition(c.mode, c.threshold, c.parameter);
        }
        var toStrafe = locomotion.AddTransition(strafe);
        toStrafe.hasExitTime = false;
        toStrafe.duration = 0.15f;
        toStrafe.AddCondition(AnimatorConditionMode.If, 0, "Aiming");
        var fromStrafe = strafe.AddTransition(locomotion);
        fromStrafe.hasExitTime = false;
        fromStrafe.duration = 0.2f;
        fromStrafe.AddCondition(AnimatorConditionMode.IfNot, 0, "Aiming");

        // Слой верха тела
        controller.AddLayer("Rifle");
        var layers = controller.layers;
        var rifleLayer = layers[^1];
        rifleLayer.defaultWeight = 1f;
        rifleLayer.avatarMask = mask;
        rifleLayer.blendingMode = AnimatorLayerBlendingMode.Override;
        controller.layers = layers;
        var sm = rifleLayer.stateMachine;

        var hold = sm.AddState("Hold", new Vector3(300, 0));
        hold.motion = Clip("Shooter_UpperBodyPoses", "Idle@Rifle");
        var aim = sm.AddState("Aim", new Vector3(300, 120));
        aim.motion = Clip("Shooter_UpperBodyPoses", "Aiming@Rifle");
        var reload = sm.AddState("Reload", new Vector3(550, 0));
        reload.motion = Clip("Shooter_Shot&Reload", "Reload_Rifle");
        sm.defaultState = hold;

        Transition(hold, aim, 0.1f).AddCondition(AnimatorConditionMode.If, 0, "Aiming");
        Transition(aim, hold, 0.15f).AddCondition(AnimatorConditionMode.IfNot, 0, "Aiming");
        var toReload = sm.AddAnyStateTransition(reload);
        toReload.hasExitTime = false;
        toReload.duration = 0.1f;
        toReload.canTransitionToSelf = false;
        toReload.AddCondition(AnimatorConditionMode.If, 0, "Reload");
        var reloadBack = reload.AddTransition(hold);
        reloadBack.hasExitTime = true;
        reloadBack.exitTime = 0.92f;
        reloadBack.duration = 0.2f;

        // Отдача - отдельный слой Additive, как у Invector (маска ShotAdditive): Shot_Rifle целиком
        // опускает винтовку, а сложение даёт только толчок поверх позы прицела
        controller.AddLayer("Rifle Shot");
        layers = controller.layers;
        var shotLayer = layers[^1];
        shotLayer.defaultWeight = 1f;
        shotLayer.avatarMask = mask;
        shotLayer.blendingMode = AnimatorLayerBlendingMode.Additive;
        controller.layers = layers;
        var shotSm = shotLayer.stateMachine;
        var idle = shotSm.AddState("Idle", new Vector3(300, 0));
        var shot = shotSm.AddState("Shot", new Vector3(300, 120));
        shot.motion = Clip("Shooter_Shot&Reload", "Shot_Rifle");
        shotSm.defaultState = idle;
        Transition(idle, shot, 0.02f).AddCondition(AnimatorConditionMode.If, 0, "Shoot");
        Transition(shot, shot, 0.02f).AddCondition(AnimatorConditionMode.If, 0, "Shoot");
        var shotBack = shot.AddTransition(idle);
        shotBack.hasExitTime = true;
        shotBack.exitTime = 0.9f;
        shotBack.duration = 0.05f;

        EditorUtility.SetDirty(controller);
        log.AppendLine($"controller {ControllerPath}: layers {string.Join(", ", controller.layers.Select(l => l.name))}");
        return controller;
    }

    static AnimatorStateTransition Transition(AnimatorState from, AnimatorState to, float duration)
    {
        var t = from.AddTransition(to);
        t.hasExitTime = false;
        t.duration = duration;
        return t;
    }

    // 6 ------------------------------------------------------------------------------------------
    static void SetupPlayer(AnimatorController controller)
    {
        var rifle = AssetDatabase.LoadAssetAtPath<GameObject>(RiflePath);
        var root = PrefabUtility.LoadPrefabContents(PlayerPath);
        root.GetComponent<Animator>().runtimeAnimatorController = controller;
        var shooter = root.GetComponent<SurvivorShooter>() ?? root.AddComponent<SurvivorShooter>();

        ComputeGrip(root, rifle, out var localPos, out var localEuler);

        var so = new SerializedObject(shooter);
        so.FindProperty("weaponPrefab").objectReferenceValue = rifle.GetComponent<ShooterWeapon>();
        so.FindProperty("weaponLocalPosition").vector3Value = localPos;
        so.FindProperty("weaponLocalEuler").vector3Value = localEuler;
        so.FindProperty("impactPrefab").objectReferenceValue = AssetDatabase.LoadAssetAtPath<GameObject>(ImpactFolder + "impactConcrete.prefab");
        so.FindProperty("decalPrefab").objectReferenceValue = AssetDatabase.LoadAssetAtPath<GameObject>(DecalFolder + "vDecal_ConcreteA.prefab");
        so.ApplyModifiedPropertiesWithoutUndo();

        PrefabUtility.SaveAsPrefabAsset(root, PlayerPath);
        PrefabUtility.UnloadPrefabContents(root);
        log.AppendLine($"player: weapon in right hand pos={localPos:F3} euler={localEuler:F1}");
    }

    // Винтовка в позе Aiming@Rifle: правая ладонь на рукояти, ствол смотрит вперёд по телу, верх винтовки вверх.
    // Рукоять лежит на линии ствола позади точки leftHandIK на расстояние между ладонями в позе
    static void ComputeGrip(GameObject player, GameObject riflePrefab, out Vector3 localPos, out Vector3 localEuler)
    {
        var body = Object.Instantiate(player);
        var animator = body.GetComponent<Animator>();
        Clip("Shooter_UpperBodyPoses", "Aiming@Rifle").SampleAnimation(body, 0f);
        var rightHand = animator.GetBoneTransform(HumanBodyBones.RightHand);
        Vector3 Palm(HumanBodyBones hand, HumanBodyBones middle) =>
            Vector3.Lerp(animator.GetBoneTransform(hand).position, animator.GetBoneTransform(middle).position, 0.6f);
        var rightPalm = Palm(HumanBodyBones.RightHand, HumanBodyBones.RightMiddleProximal);
        var leftPalm = Palm(HumanBodyBones.LeftHand, HumanBodyBones.LeftMiddleProximal);

        var rifle = Object.Instantiate(riflePrefab);
        var weapon = rifle.GetComponent<ShooterWeapon>();
        var model = rifle.transform.Find("Model");
        var grip = rifle.transform.InverseTransformPoint(weapon.LeftHandGrip.position);
        var bore = (rifle.transform.InverseTransformPoint(weapon.Muzzle.position)
                    - rifle.transform.InverseTransformPoint(model.Find("AimReference").position)).normalized;
        var up = rifle.transform.InverseTransformDirection(model.up);
        float handsApart = Vector3.Distance(rightPalm, leftPalm);
        var rightGrip = grip - bore * handsApart;

        // Ствол строго вперёд по телу. Линия от правой ладони к левой у выжившего уходит вниз и влево:
        // руки у него другой длины, чем у vBot, под которого сделана поза. Левую ладонь дотянет IK
        var dir = body.transform.forward;
        var rotation = Quaternion.LookRotation(dir, body.transform.up) * Quaternion.Inverse(Quaternion.LookRotation(bore, up));
        var position = rightPalm - rotation * rightGrip;

        localPos = rightHand.InverseTransformPoint(position);
        localEuler = (Quaternion.Inverse(rightHand.rotation) * rotation).eulerAngles;
        log.AppendLine($"grip: hands apart {handsApart:0.000} m, right hand lossyScale {rightHand.lossyScale:F3}, " +
                       $"barrel dir in body space {body.transform.InverseTransformDirection(dir):F2}");
        Object.DestroyImmediate(rifle);
        Object.DestroyImmediate(body);
    }

    // 7 ------------------------------------------------------------------------------------------
    static void SetupScene()
    {
        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != ScenePath)
        {
            log.AppendLine($"scene: active scene is {scene.path}, open {ScenePath} and run again to link the camera");
            return;
        }
        var shooter = Object.FindObjectsByType<SurvivorShooter>(FindObjectsSortMode.None).Single();
        var follow = Object.FindObjectsByType<CinemachineThirdPersonFollow>(FindObjectsSortMode.None).Single();
        var so = new SerializedObject(shooter);
        so.FindProperty("followCamera").objectReferenceValue = follow;
        so.ApplyModifiedPropertiesWithoutUndo();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        log.AppendLine($"scene: {shooter.name}.followCamera = {follow.name}");
    }
}
