using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Надетое снаряжение на модели игрока: каждая часть (нагрудник, штаны, ботинки, перчатки) - свой
/// SkinnedMeshRenderer на скелете тела. Зону тела под надетой частью компонент прячет: без неё кожа
/// проступает сквозь часть на сгибах. Части и их зоны ставит Tools/Unity/SetupGear.cs.
/// </summary>
public class WornGear : MonoBehaviour
{
    [Serializable]
    struct Piece
    {
        public SkinnedMeshRenderer renderer;
        // Треугольники тела под частью: номер по всем подмешам тела подряд
        public int[] hiddenTriangles;
    }

    [SerializeField] SkinnedMeshRenderer body;
    [SerializeField] Piece[] pieces;

    Mesh sourceMesh;
    Mesh maskedMesh;
    readonly List<int> triangles = new();
    readonly HashSet<int> hidden = new();

    void Awake()
    {
        sourceMesh = body.sharedMesh;
    }

    void OnEnable()
    {
        Refresh();
    }

    void OnDestroy()
    {
        if (maskedMesh != null)
            Destroy(maskedMesh);
    }

    /// <summary>Прячет тело под надетыми (включёнными) частями. Звать после того, как часть надета или снята.</summary>
    public void Refresh()
    {
        hidden.Clear();
        foreach (var piece in pieces)
            if (piece.renderer.gameObject.activeSelf)
                hidden.UnionWith(piece.hiddenTriangles);
        if (hidden.Count == 0)
        {
            body.sharedMesh = sourceMesh;
            return;
        }
        // Копия меша тела: вершины и веса те же, меняются только списки треугольников.
        // Исходный меш - общий ассет модели, его трогать нельзя
        if (maskedMesh == null)
        {
            maskedMesh = Instantiate(sourceMesh);
            maskedMesh.name = sourceMesh.name + " (WornGear)";
        }
        int first = 0;
        for (int sub = 0; sub < sourceMesh.subMeshCount; sub++)
        {
            sourceMesh.GetTriangles(triangles, sub);
            int count = triangles.Count / 3;
            int kept = 0;
            for (int t = 0; t < count; t++)
            {
                if (hidden.Contains(first + t))
                    continue;
                triangles[kept * 3] = triangles[t * 3];
                triangles[kept * 3 + 1] = triangles[t * 3 + 1];
                triangles[kept * 3 + 2] = triangles[t * 3 + 2];
                kept++;
            }
            triangles.RemoveRange(kept * 3, triangles.Count - kept * 3);
            maskedMesh.SetTriangles(triangles, sub, false);
            first += count;
        }
        body.sharedMesh = maskedMesh;
    }
}
