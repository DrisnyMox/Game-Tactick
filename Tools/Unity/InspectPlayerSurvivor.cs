using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

// Печатает то, что важно для бота и рэгдолла: слой префаба PlayerSurvivor, CharacterController,
// коллайдеры и Rigidbody внутри, компоненты корня, слои проекта, настройки NavMesh пехоты.
// Запуск: unity command run_script --file Tools/Unity/InspectPlayerSurvivor.cs --project-path "D:\Game Tactick"
public static class InspectPlayerSurvivor
{
    public static string Main()
    {
        var log = new StringBuilder();
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Characters/Survivor/PlayerSurvivor.prefab");
        log.AppendLine($"layer {LayerMask.LayerToName(prefab.layer)}, variant {PrefabUtility.GetPrefabAssetType(prefab)}");
        var cc = prefab.GetComponent<CharacterController>();
        log.AppendLine($"cc height {cc.height} radius {cc.radius} center {cc.center} step {cc.stepOffset} slope {cc.slopeLimit}");
        log.AppendLine("root: " + string.Join(", ", prefab.GetComponents<Component>().Select(c => c.GetType().Name)));
        log.AppendLine($"colliders {prefab.GetComponentsInChildren<Collider>(true).Length}, rigidbodies {prefab.GetComponentsInChildren<Rigidbody>(true).Length}");
        var tpc = prefab.GetComponent<StarterAssets.ThirdPersonController>();
        log.AppendLine($"tpc move {tpc.MoveSpeed} sprint {tpc.SprintSpeed} ground mask {tpc.GroundLayers.value}");
        for (int i = 0; i < 32; i++)
            if (!string.IsNullOrEmpty(LayerMask.LayerToName(i)))
                log.Append($"{i}:{LayerMask.LayerToName(i)} ");
        log.AppendLine();
        var nav = UnityEngine.AI.NavMesh.GetSettingsByID(0);
        log.AppendLine($"nav agent 0: radius {nav.agentRadius} height {nav.agentHeight} climb {nav.agentClimb} slope {nav.agentSlope}");
        var smr = prefab.GetComponentInChildren<SkinnedMeshRenderer>();
        log.AppendLine($"materials: {string.Join(", ", smr.sharedMaterials.Select(m => AssetDatabase.GetAssetPath(m)))}");
        return log.ToString();
    }
}
