using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace VoxelCharacterSystem.Editor
{
    /// <summary>
    /// Ставит модели с ригом из Blender тип Humanoid и сам связывает кости с частями тела по именам.
    /// Автоподбор Unity на SurvivorRigged не находит кость Head, поэтому связка задаётся явно.
    /// Кости в FBX названы как части тела Humanoid (Hips, Spine, Head, LeftUpperArm...), их ставят
    /// Tools/Blender/rig_survivor_fbx.py (SurvivorRigged.fbx) и Tools/Blender/rig_model_8b8745d5.py
    /// (Assets/Models/20261121003938_8b8745d5.fbx).
    /// </summary>
    public static class SurvivorRiggedAvatarSetup
    {
        const string ModelPath = "Assets/Characters/Survivor/SurvivorRigged.fbx";
        const string Model8b8745d5Path = "Assets/Models/20261121003938_8b8745d5.fbx";

        [MenuItem("Tools/Voxel Arena/Survivor Rigged: настроить Humanoid", priority = 22)]
        public static void Setup() => Setup(ModelPath);

        [MenuItem("Tools/Voxel Arena/Модель 20261121003938_8b8745d5: настроить Humanoid", priority = 23)]
        public static void SetupModel8b8745d5() => Setup(Model8b8745d5Path);

        public static void Setup(string modelPath)
        {
            var importer = (ModelImporter)AssetImporter.GetAtPath(modelPath);
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
            var transforms = model.GetComponentsInChildren<Transform>(true);
            var names = new HashSet<string>(transforms.Select(t => t.name));

            // human: какой кости модели соответствует какая часть тела Humanoid.
            // Пальцы Unity называет с пробелами ("Left Thumb Proximal"), кости в FBX названы без них.
            var human = new List<HumanBone>();
            foreach (var humanName in HumanTrait.BoneName)
            {
                var boneName = humanName.Replace(" ", "");
                if (!names.Contains(boneName))
                    continue;
                var bone = new HumanBone { boneName = boneName, humanName = humanName };
                bone.limit.useDefaultValues = true;
                human.Add(bone);
            }

            // skeleton: положение всех узлов модели в T-позе, по нему Unity строит аватар
            var skeleton = transforms.Select(t => new SkeletonBone
            {
                name = t.name,
                position = t.localPosition,
                rotation = t.localRotation,
                scale = t.localScale,
            }).ToArray();

            var description = importer.humanDescription;
            description.human = human.ToArray();
            description.skeleton = skeleton;
            importer.humanDescription = description;
            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.SaveAndReimport();

            var avatar = AssetDatabase.LoadAllAssetsAtPath(modelPath).OfType<Avatar>().FirstOrDefault();
            if (avatar == null || !avatar.isValid || !avatar.isHuman)
            {
                Debug.LogError($"[{modelPath}] Аватар Humanoid не собрался ({human.Count} костей связано). " +
                               "Причину Unity пишет в консоль строкой Rig Error выше.");
                return;
            }
            Debug.Log($"[{modelPath}] Humanoid готов: связано {human.Count} костей " +
                      $"({string.Join(", ", human.Select(h => h.humanName))}).");
        }
    }
}
