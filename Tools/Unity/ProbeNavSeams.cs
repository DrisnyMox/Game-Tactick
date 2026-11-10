using System.Collections;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.AI;

// Связки на швах между областями ArenaNavMesh. У ворот один берётся ближайший шов слева от области (граница по X):
// где NavMesh ближе всего к шву с каждой стороны, есть ли связка рядом, проходит ли путь через шов.
// Запуск в Play: unity command run_script --file Tools/Unity/ProbeNavSeams.cs --project-path "D:\Game Tactick"
public static class ProbeNavSeams
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static string Main()
    {
        var arena = Object.FindAnyObjectByType<VoxelArena>();
        var nav = Object.FindAnyObjectByType<ArenaNavMesh>();
        int regionChunks = (int)typeof(ArenaNavMesh).GetField("regionChunks", Private).GetValue(nav);
        float region = regionChunks * VoxelArena.ChunkSize * arena.VoxelSize;
        var filter = new NavMeshQueryFilter { agentTypeID = nav.AgentTypeID, areaMask = NavMesh.AllAreas };

        var log = new StringBuilder();
        var origin = arena.transform.position;
        var gate = arena.GateOne;
        float seamX = origin.x + Mathf.Round((gate.x - origin.x) / region) * region;
        log.AppendLine($"gate {gate}, seam x {seamX:0.000}, region {region:0.00} m");

        foreach (float dz in new[] { 0f, 1f, 2f, -1f })
        {
            var p = new Vector3(seamX, gate.y, gate.z + dz);
            log.Append($"z {p.z:0.00}:");
            foreach (float dx in new[] { -1f, -0.3f, -0.1f, 0.1f, 0.3f, 1f })
            {
                if (NavMesh.SamplePosition(p + Vector3.right * dx, out var hit, 0.25f, filter))
                    log.Append($" [{dx:+0.0;-0.0}: {hit.position.x - seamX:+0.000;-0.000}, y {hit.position.y:0.00}]");
                else
                    log.Append($" [{dx:+0.0;-0.0}: нет]");
            }
            var a = p - Vector3.right * 1.5f;
            var b = p + Vector3.right * 1.5f;
            if (NavMesh.SamplePosition(a, out var ha, 1f, filter) && NavMesh.SamplePosition(b, out var hb, 1f, filter))
            {
                var path = new NavMeshPath();
                NavMesh.CalculatePath(ha.position, hb.position, filter, path);
                log.Append($" путь {path.status}, точек {path.corners.Length}");
            }
            log.AppendLine();
        }

        // Связки этого шва из ArenaNavMesh
        int rx = Mathf.RoundToInt((seamX - origin.x) / region);
        int rz = Mathf.FloorToInt((gate.z - origin.z) / region);
        int regionsX = (int)typeof(ArenaNavMesh).GetField("regionsX", Private).GetValue(nav);
        int seam = (rz * regionsX + rx) * 2;
        var layers = (System.Array)typeof(ArenaNavMesh).GetField("seamLinks", Private).GetValue(nav);
        var dict = (IDictionary)layers.GetValue(0);
        if (dict.Contains(seam))
        {
            var links = (IList)dict[seam];
            log.AppendLine($"шов {seam}: связок {links.Count}");
            foreach (var link in links)
            {
                var t = link.GetType();
                var pos = (Vector3)t.GetField("position").GetValue(link);
                var instance = (NavMeshLinkInstance)t.GetField("instance").GetValue(link);
                log.AppendLine($"  {pos} ширина {(float)t.GetField("width").GetValue(link):0.00} подъём {(float)t.GetField("rise").GetValue(link):0.00} " +
                               $"вдоль X {t.GetField("crossX").GetValue(link)}, на навмеше {NavMesh.IsLinkValid(instance)}");
            }
        }
        else
            log.AppendLine($"шов {seam}: связок нет");
        return log.ToString();
    }
}
