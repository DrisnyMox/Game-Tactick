using System.Diagnostics;
using System.Text;
using UnityEngine;
// Play Mode: генерирует арену с сидами и считает хэш всех мешей узлов (вершины, нормали, цвета, индексы)
// и мешей коллайдеров — по нему сверяется, что правка построения мешей не меняет геометрию
public static class ProbeMeshHash
{
    public static string Main()
    {
        var arena = Object.FindAnyObjectByType<VoxelArena>();
        var sb = new StringBuilder();
        foreach (int seed in new[] { 1, 42, 1, 42 })
        {
            arena.Seed = seed;
            var t = Stopwatch.StartNew();
            arena.Generate();
            long ms = t.ElapsedMilliseconds;

            ulong h = 1469598103934665603UL;
            void Mix(long v) { h ^= (ulong)v; h *= 1099511628211UL; }
            foreach (var f in arena.GetComponentsInChildren<MeshFilter>(true))
            {
                var m = f.sharedMesh;
                Mix(m.vertexCount);
                foreach (var v in m.vertices) { Mix(Mathf.RoundToInt(v.x * 4)); Mix(Mathf.RoundToInt(v.y * 4)); Mix(Mathf.RoundToInt(v.z * 4)); }
                foreach (var c in m.colors32) { Mix(c.r); Mix(c.g); }
                foreach (var n in m.normals) { Mix(Mathf.RoundToInt(n.x)); Mix(Mathf.RoundToInt(n.y)); Mix(Mathf.RoundToInt(n.z)); }
                foreach (var i in m.triangles) Mix(i);
            }
            ulong hc = 1469598103934665603UL;
            int colliders = 0;
            foreach (var c in arena.GetComponentsInChildren<MeshCollider>(false))
            {
                if (!c.enabled || c.sharedMesh == null) continue;
                colliders++;
                var m = c.sharedMesh;
                hc ^= (ulong)m.vertexCount; hc *= 1099511628211UL;
                foreach (var i in m.triangles) { hc ^= (ulong)i; hc *= 1099511628211UL; }
            }
            sb.AppendLine($"seed {seed}: {ms} ms (layout+vox {arena.LastGenerateMs - arena.LastMeshMs - arena.LastApplyMs - arena.LastCombineMs:0}, mesh {arena.LastMeshMs:0}, apply {arena.LastApplyMs:0}, combine {arena.LastCombineMs:0}), tris {arena.TotalTriangles}/{arena.TotalShapeTriangles}, nodes {h:X16}, colliders {colliders} {hc:X16}");
        }
        return sb.ToString();
    }
}
