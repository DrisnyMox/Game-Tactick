using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Игрок с моделью Assets/Models/20261121003938_8b8745d5.fbx (риг — Tools/Blender/rig_model_8b8745d5.py).
// Боты и рейдеры остаются на PlayerSurvivor с выжившим, поэтому модель меняется не в нём, а в варианте:
//  1. модели ставится масштаб импорта под рост выжившего (рост решает размер вокселя арены и высоту камеры)
//     и собирается аватар Humanoid;
//  2. вариант PlayerSurvivor (VariantPath): кости выжившего встают в T-позу новой модели (имена костей у обеих
//     моделей одни — Humanoid из скриптов Tools/Blender), меш, материал и аватар — от новой модели.
//     Рэгдолл (коллайдеры и суставы на костях) подгоняется под новое тело мастером Create Ragdoll на копии,
//     смещение винтовки в кисти пересчитывается так, чтобы винтовка лежала в руке как у выжившего;
//  3. в сценах игрок (SurvivorShooter без botControlled) переводится на вариант, ссылки сцены на игрока остаются.
// Скрипт можно запускать повторно. Остальные Setup* цепочки игрока правят PlayerSurvivor, а SetupPlayerSurvivor
// пересобирает его и сцену SurvivorPlayground с нуля, поэтому этот скрипт запускается после них.
public static class SetupPlayerModel
{
    const string ModelPath = "Assets/Models/20261121003938_8b8745d5.fbx";
    const string SurvivorModelPath = "Assets/Characters/Survivor/SurvivorRigged.fbx";
    const string BasePrefabPath = "Assets/Characters/Survivor/PlayerSurvivor.prefab";
    const string VariantPath = "Assets/Characters/Survivor/PlayerSurvivor8b8745d5.prefab";
    static readonly string[] ScenePaths =
    {
        "Assets/Scenes/Base.unity", "Assets/Scenes/Raid.unity", "Assets/Scenes/SampleScene.unity",
        "Assets/Scenes/SurvivorPlayground.unity",
    };

    public static string Main()
    {
        var log = new StringBuilder();

        // 1. Рост и аватар
        var survivor = AssetDatabase.LoadAssetAtPath<GameObject>(SurvivorModelPath).GetComponentInChildren<SkinnedMeshRenderer>();
        float targetHeight = survivor.sharedMesh.bounds.size.y;
        var importer = (ModelImporter)AssetImporter.GetAtPath(ModelPath);
        float rawHeight = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath).GetComponentInChildren<SkinnedMeshRenderer>()
            .sharedMesh.bounds.size.y / importer.globalScale;
        float scale = Mathf.Round(targetHeight / rawHeight * 1000f) / 1000f;
        if (!Mathf.Approximately(importer.globalScale, scale))
        {
            importer.globalScale = scale;
            importer.SaveAndReimport();
        }
        VoxelCharacterSystem.Editor.SurvivorRiggedAvatarSetup.Setup(ModelPath);
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
        var avatar = AssetDatabase.LoadAllAssetsAtPath(ModelPath).OfType<Avatar>().Single();
        log.AppendLine($"model scale {scale}, height {model.GetComponentInChildren<SkinnedMeshRenderer>().sharedMesh.bounds.size.y:0.000} m " +
                       $"(survivor {targetHeight:0.000}), avatar human={avatar.isHuman} valid={avatar.isValid}");

        // 2. Вариант префаба
        if (AssetDatabase.LoadAssetAtPath<GameObject>(VariantPath) == null)
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(BasePrefabPath));
            PrefabUtility.SaveAsPrefabAsset(instance, VariantPath);
            Object.DestroyImmediate(instance);
            log.AppendLine($"variant {VariantPath} created");
        }
        var root = PrefabUtility.LoadPrefabContents(VariantPath);
        BuildVariant(root, model, avatar, log);
        PrefabUtility.SaveAsPrefabAsset(root, VariantPath);
        PrefabUtility.UnloadPrefabContents(root);
        var variant = AssetDatabase.LoadAssetAtPath<GameObject>(VariantPath);

        // 3. Сцены
        foreach (var path in ScenePaths)
        {
            var scene = SceneManager.GetSceneByPath(path);
            bool wasOpen = scene.isLoaded;
            if (wasOpen && scene.isDirty)
            {
                log.AppendLine($"scene {path} has unsaved changes, skipped");
                continue;
            }
            if (!wasOpen)
                scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            foreach (var shooter in scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<SurvivorShooter>(true)).ToList())
            {
                if (new SerializedObject(shooter).FindProperty("botControlled").boolValue)
                    continue;
                var player = PrefabUtility.GetOutermostPrefabInstanceRoot(shooter.gameObject);
                var source = PrefabUtility.GetCorrespondingObjectFromSource(player);
                if (source != variant && AssetDatabase.GetAssetPath(source) != BasePrefabPath)
                    throw new System.InvalidOperationException($"{path}: игрок {player.name} — экземпляр {AssetDatabase.GetAssetPath(source)}, не PlayerSurvivor");
                if (source != variant)
                {
                    PrefabUtility.ReplacePrefabAssetOfPrefabInstance(player, variant,
                        new PrefabReplacingSettings { objectMatchMode = ObjectMatchMode.ByHierarchy },
                        InteractionMode.AutomatedAction);
                    log.AppendLine($"scene {path}: {player.name} -> {variant.name}");
                }
                // Пробы (ProbeHitFeedback и другие) ищут игрока по имени
                player.name = "PlayerSurvivor";
                // Переопределения сцены на меше, материале, аватаре и винтовке взяли бы своё поверх варианта
                RevertOverrides(player.GetComponentInChildren<SkinnedMeshRenderer>(), "m_Mesh", "m_Materials", "m_Bones", "m_RootBone", "m_AABB");
                RevertOverrides(player.GetComponent<Animator>(), "m_Avatar");
                RevertOverrides(player.GetComponent<SurvivorShooter>(), "weaponLocalPosition", "weaponLocalEuler");
                log.AppendLine($"scene {path}: player mesh {player.GetComponentInChildren<SkinnedMeshRenderer>().sharedMesh.name}, " +
                               $"avatar {player.GetComponent<Animator>().avatar.name}");
            }
            EditorSceneManager.SaveScene(scene);
            if (!wasOpen)
                EditorSceneManager.CloseScene(scene, true);
        }
        return log.ToString();
    }

    static void BuildVariant(GameObject root, GameObject model, Avatar avatar, StringBuilder log)
    {
        var animator = root.GetComponent<Animator>();
        var oldHand = animator.GetBoneTransform(HumanBodyBones.RightHand);
        Quaternion oldHandRotation = Quaternion.Inverse(root.transform.rotation) * oldHand.rotation;
        // смещение винтовки берётся у основы: у варианта оно уже могло быть пересчитано прошлым запуском
        var baseShooter = new SerializedObject(AssetDatabase.LoadAssetAtPath<GameObject>(BasePrefabPath).GetComponent<SurvivorShooter>());
        Vector3 basePosition = baseShooter.FindProperty("weaponLocalPosition").vector3Value;
        Vector3 baseEuler = baseShooter.FindProperty("weaponLocalEuler").vector3Value;
        var baseHand = AssetDatabase.LoadAssetAtPath<GameObject>(BasePrefabPath).GetComponent<Animator>().GetBoneTransform(HumanBodyBones.RightHand);
        Quaternion baseHandRotation = Quaternion.Inverse(baseHand.root.rotation) * baseHand.rotation;

        // Кости: T-поза новой модели. Узлы с теми же именами есть в обеих моделях.
        var own = root.GetComponentsInChildren<Transform>(true).GroupBy(t => t.name).ToDictionary(g => g.Key, g => g.First());
        var src = model.GetComponentsInChildren<Transform>(true).Where(t => t != model.transform).ToList();
        var smrSource = model.GetComponentInChildren<SkinnedMeshRenderer>();
        int moved = 0;
        foreach (var t in src)
        {
            if (t == smrSource.transform || !own.TryGetValue(t.name, out var target))
                continue;
            target.SetLocalPositionAndRotation(t.localPosition, t.localRotation);
            target.localScale = t.localScale;
            moved++;
        }
        log.AppendLine($"bones placed: {moved}");

        // Меш, материал, кости меша
        var smr = root.GetComponentInChildren<SkinnedMeshRenderer>();
        smr.transform.SetLocalPositionAndRotation(smrSource.transform.localPosition, smrSource.transform.localRotation);
        smr.sharedMesh = smrSource.sharedMesh;
        smr.sharedMaterials = smrSource.sharedMaterials;
        smr.bones = smrSource.bones.Select(b => own[b.name]).ToArray();
        smr.rootBone = own[smrSource.rootBone.name];
        smr.localBounds = smrSource.localBounds;
        animator.avatar = avatar;
        log.AppendLine($"mesh {smr.sharedMesh.name}, materials {string.Join(", ", smr.sharedMaterials.Select(m => m.name))}");

        // Винтовка: тот же поворот и сдвиг относительно тела в T-позе, что у выжившего.
        // Сдвиг идёт через мировой вектор: масштаб костей у моделей разный.
        var newHand = animator.GetBoneTransform(HumanBodyBones.RightHand);
        Quaternion newHandRotation = Quaternion.Inverse(root.transform.rotation) * newHand.rotation;
        Quaternion delta = Quaternion.Inverse(newHandRotation) * baseHandRotation;
        Vector3 offset = Quaternion.Inverse(baseHand.root.rotation) * baseHand.TransformVector(basePosition);
        Vector3 position = newHand.InverseTransformVector(root.transform.rotation * offset);
        Vector3 euler = (delta * Quaternion.Euler(baseEuler)).eulerAngles;
        var shooter = new SerializedObject(root.GetComponent<SurvivorShooter>());
        shooter.FindProperty("weaponLocalPosition").vector3Value = position;
        shooter.FindProperty("weaponLocalEuler").vector3Value = euler;
        shooter.ApplyModifiedPropertiesWithoutUndo();
        log.AppendLine($"weapon in hand: {basePosition} {baseEuler} -> {position} {euler}" +
                       $" (hand turn {Quaternion.Angle(oldHandRotation, newHandRotation):0.0} deg, hand scale " +
                       $"{baseHand.lossyScale.x:0.###} -> {newHand.lossyScale.x:0.###})");

        FitRagdoll(root, log);
    }

    // Мастер рэгдолла строит тела на копии под новые кости; размеры коллайдеров и оси суставов переносятся
    // на компоненты варианта (сами компоненты остаются те же, что у основы: на них слой Hitbox и ссылки)
    static void FitRagdoll(GameObject root, StringBuilder log)
    {
        // копия рядом с корнем, в сцене префаба: в открытой сцене Editor она пометила бы сцену изменённой
        var copy = Object.Instantiate(root, root.transform);
        copy.transform.SetParent(null, false);
        copy.transform.SetPositionAndRotation(root.transform.position, root.transform.rotation);
        foreach (var j in copy.GetComponentsInChildren<CharacterJoint>(true)) Object.DestroyImmediate(j);
        foreach (var b in copy.GetComponentsInChildren<Rigidbody>(true)) Object.DestroyImmediate(b);
        foreach (var c in copy.GetComponentsInChildren<Collider>(true).Where(c => !(c is CharacterController))) Object.DestroyImmediate(c);
        BuildRagdoll(copy.GetComponent<Animator>());
        CloseNeckGap(copy.GetComponent<Animator>());

        var own = root.GetComponentsInChildren<Transform>(true).GroupBy(t => t.name).ToDictionary(g => g.Key, g => g.First());
        int fitted = 0;
        foreach (var body in copy.GetComponentsInChildren<Rigidbody>(true))
        {
            var target = own[body.name];
            target.GetComponent<Rigidbody>().mass = body.mass;
            switch (body.GetComponent<Collider>())
            {
                case BoxCollider box:
                    var tb = target.GetComponent<BoxCollider>();
                    tb.center = box.center;
                    tb.size = box.size;
                    break;
                case CapsuleCollider cap:
                    var tc = target.GetComponent<CapsuleCollider>();
                    tc.center = cap.center;
                    tc.radius = cap.radius;
                    tc.height = cap.height;
                    tc.direction = cap.direction;
                    break;
                case SphereCollider sphere:
                    var ts = target.GetComponent<SphereCollider>();
                    ts.center = sphere.center;
                    ts.radius = sphere.radius;
                    break;
            }
            var joint = body.GetComponent<CharacterJoint>();
            if (joint != null)
            {
                var tj = target.GetComponent<CharacterJoint>();
                tj.autoConfigureConnectedAnchor = joint.autoConfigureConnectedAnchor;
                tj.anchor = joint.anchor;
                tj.connectedAnchor = joint.connectedAnchor;
                tj.axis = joint.axis;
                tj.swingAxis = joint.swingAxis;
                tj.lowTwistLimit = joint.lowTwistLimit;
                tj.highTwistLimit = joint.highTwistLimit;
                tj.swing1Limit = joint.swing1Limit;
                tj.swing2Limit = joint.swing2Limit;
            }
            fitted++;
        }
        Object.DestroyImmediate(copy);
        log.AppendLine($"ragdoll fitted: {fitted} bodies");
    }

    static void RevertOverrides(Object component, params string[] properties)
    {
        var so = new SerializedObject(component);
        foreach (var name in properties)
        {
            var property = so.FindProperty(name);
            if (property != null && property.prefabOverride)
                PrefabUtility.RevertPropertyOverride(property, InteractionMode.AutomatedAction);
        }
    }

    // Как в SetupArenaBot.cs: между коробкой груди и шаром головы мастер оставляет шею без коллайдера
    static void CloseNeckGap(Animator animator)
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
    }

    // Как в SetupArenaBot.cs: мастер GameObject > 3D Object > Ragdoll... без окна
    static void BuildRagdoll(Animator animator)
    {
        var type = System.AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("UnityEditor.RagdollBuilder"))
            .First(t => t != null);
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
}
