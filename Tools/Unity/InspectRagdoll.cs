using System.Text;
using UnityEditor;
using UnityEngine;

// Печатает коллайдеры рэгдолла префаба PlayerSurvivor: кость, тип, размер, мировые границы,
// и где стоит голова по Humanoid.
// Запуск: unity command run_script --file Tools/Unity/InspectRagdoll.cs --project-path "D:\Game Tactick"
public static class InspectRagdoll
{
    public static string Main()
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Characters/Survivor/PlayerSurvivor.prefab");
        var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        var log = new StringBuilder();
        var animator = instance.GetComponent<Animator>();
        var head = animator.GetBoneTransform(HumanBodyBones.Head);
        log.AppendLine($"head bone {head.name} at {head.position}, neck at {animator.GetBoneTransform(HumanBodyBones.Neck).position}");
        foreach (var body in instance.GetComponentsInChildren<Rigidbody>(true))
        {
            foreach (var c in body.GetComponents<Collider>())
            {
                string shape = c switch
                {
                    SphereCollider s => $"sphere r {s.radius:0.000} center {s.center}",
                    CapsuleCollider p => $"capsule r {p.radius:0.000} h {p.height:0.000} dir {p.direction} center {p.center}",
                    BoxCollider b => $"box size {b.size} center {b.center}",
                    _ => c.GetType().Name,
                };
                log.AppendLine($"{body.name}: {shape}, bounds {c.bounds.min} - {c.bounds.max}, bone scale {body.transform.lossyScale}");
            }
        }
        Object.DestroyImmediate(instance);
        return log.ToString();
    }
}
