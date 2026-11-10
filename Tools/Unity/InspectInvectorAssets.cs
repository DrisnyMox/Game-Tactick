using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

// Отчёт о скопированных ассетах Invector: дерево префаба винтовки с компонентами и шейдерами,
// клипы анимаций (длина, Humanoid ли), компоненты префабов попаданий.
// Запуск через Unity CLI (из корня проекта):
//     unity command run_script --file Tools/Unity/InspectInvectorAssets.cs --project-path "D:\Game Tactick"
public static class InspectInvectorAssets
{
    const string Root = "Assets/ThirdParty/Invector/Shooter/";

    public static string Main()
    {
        var s = new StringBuilder();
        var rifle = AssetDatabase.LoadAssetAtPath<GameObject>(Root + "Prefabs/Weapon/_weapons_WITHOUT_Inventory/_weaponsPrefabs/vAssaultRifle_NoInventory.prefab");
        Dump(rifle.transform, 0, s);

        foreach (var fbx in new[] { "Shooter_UpperBodyPoses", "Shooter_Shot&Reload", "Shooter_StrafeMoveset" })
        {
            var path = Root + "3DModels/Animations/" + fbx + ".fbx";
            var importer = (ModelImporter)AssetImporter.GetAtPath(path);
            s.AppendLine($"{fbx}: type={importer.animationType} avatarSetup={importer.avatarSetup} source={(importer.sourceAvatar ? importer.sourceAvatar.name : "-")}");
            foreach (var clip in AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().Where(c => !c.name.StartsWith("__preview")))
                s.AppendLine($"  clip {clip.name} len={clip.length:0.00} human={clip.humanMotion} loop={clip.isLooping}");
        }

        foreach (var name in new[] { "impactConcrete", "impactMetal", "impactWood", "impactDirt" })
        {
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(Root + "Scripts/DecalManager/BulletEffect/Resources/prefabs/" + name + ".prefab");
            s.AppendLine($"{name}: " + string.Join(", ", go.GetComponentsInChildren<Component>(true).Select(c => c == null ? "MISSING" : c.GetType().Name).Distinct()));
        }
        var decal = AssetDatabase.LoadAssetAtPath<GameObject>(Root + "Scripts/DecalManager/Decals/vDecal_ConcreteA.prefab");
        Dump(decal.transform, 0, s);
        return s.ToString();
    }

    static void Dump(Transform t, int depth, StringBuilder s)
    {
        var comps = t.GetComponents<Component>().Where(c => !(c is Transform)).Select(c =>
        {
            if (c == null) return "MISSING";
            if (c is Renderer r) return $"{c.GetType().Name}[{string.Join("/", r.sharedMaterials.Select(m => m ? m.name + ":" + m.shader.name : "null"))}]";
            if (c is MeshFilter mf) return $"MeshFilter[{(mf.sharedMesh ? mf.sharedMesh.name + " " + mf.sharedMesh.bounds.size.ToString("F2") : "null")}]";
            return c.GetType().Name;
        });
        s.AppendLine($"{new string(' ', depth * 2)}{t.name} {(t.gameObject.activeSelf ? "" : "(off) ")}pos={t.localPosition:F3} rot={t.localEulerAngles:F0} [{string.Join(", ", comps)}]");
        foreach (Transform c in t)
            Dump(c, depth + 1, s);
    }
}
