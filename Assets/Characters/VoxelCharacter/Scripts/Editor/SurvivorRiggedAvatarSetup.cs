using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace VoxelCharacterSystem.Editor
{
    /// <summary>
    /// Ставит SurvivorRigged.fbx тип Humanoid и сам связывает кости с частями тела по именам.
    /// Автоподбор Unity на этой модели не находит кость Head, поэтому связка задаётся явно.
    /// Кости в FBX названы как части тела Humanoid (Hips, Spine, Head, LeftUpperArm...), их ставит
    /// Tools/Blender/rig_survivor_fbx.py.
    /// </summary>
    public static class SurvivorRiggedAvatarSetup
    {
        const string ModelPath = "Assets/Characters/Survivor/SurvivorRigged.fbx";

        [MenuItem("Tools/Voxel Arena/Survivor Rigged: настроить Humanoid", priority = 22)]
        public static void Setup()
        {
            var importer = (ModelImporter)AssetImporter.GetAtPath(ModelPath);
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
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

            var avatar = AssetDatabase.LoadAllAssetsAtPath(ModelPath).OfType<Avatar>().FirstOrDefault();
            if (avatar == null || !avatar.isValid || !avatar.isHuman)
            {
                Debug.LogError($"[SurvivorRigged] Аватар Humanoid не собрался ({human.Count} костей связано). " +
                               "Причину Unity пишет в консоль строкой Rig Error выше.");
                return;
            }
            Debug.Log($"[SurvivorRigged] Humanoid готов: связано {human.Count} костей " +
                      $"({string.Join(", ", human.Select(h => h.humanName))}).");
        }
    }
}
