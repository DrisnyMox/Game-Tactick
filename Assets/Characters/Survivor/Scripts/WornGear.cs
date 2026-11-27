using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Надетое снаряжение на модели игрока: каждая часть (нагрудник, штаны, ботинки, перчатки) - свой
/// SkinnedMeshRenderer на скелете тела. Зону тела под надетой частью компонент прячет: без неё кожа
/// проступает сквозь часть на сгибах. На варианте стоят все части, надеты включённые; что надеть, решает
/// профиль (Gear.Dress на базе и в вылазке). Части и их зоны ставит Tools/Unity/SetupGear.cs.
/// </summary>
public class WornGear : MonoBehaviour
{
    [Serializable]
    struct Piece
    {
        public SkinnedMeshRenderer renderer;
        // Вершины тела под частью (номера в меше тела). Прячется треугольник, у которого все вершины под
        // надетыми частями вместе: у щиколотки треугольник кожи одной вершиной под штанами, другой под обувью,
        // и по отдельности его не прятала ни одна часть
        public int[] hiddenVertices;
    }

    [SerializeField] SkinnedMeshRenderer body;
    [SerializeField] Piece[] pieces;

    static readonly int QualityColorId = Shader.PropertyToID("_QualityColor");

    Mesh sourceMesh;
    Mesh maskedMesh;
    MaterialPropertyBlock block;
    readonly List<int> triangles = new();
    bool[] hidden;

    void Awake()
    {
        sourceMesh = body.sharedMesh;
        hidden = new bool[sourceMesh.vertexCount];
        block = new MaterialPropertyBlock();
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

    /// <summary>
    /// Надеть части с этими именами объектов, остальные снять. Цвет — цвет качества части: им шейдер части
    /// (Game Tactick/Gear) красит светлую полосу по маске качества
    /// </summary>
    public void Show(IReadOnlyDictionary<string, Color> colors)
    {
        foreach (var piece in pieces)
        {
            bool wear = colors.TryGetValue(piece.renderer.gameObject.name, out var color);
            piece.renderer.gameObject.SetActive(wear);
            if (!wear)
                continue;
            // Свой блок на часть: материал общий для всех, у кого эта часть, а цвет у каждой надетой свой
            piece.renderer.GetPropertyBlock(block);
            block.SetColor(QualityColorId, color);
            piece.renderer.SetPropertyBlock(block);
        }
        Refresh();
    }

    /// <summary>Прячет тело под надетыми (включёнными) частями. Звать после того, как часть надета или снята.</summary>
    public void Refresh()
    {
        Array.Clear(hidden, 0, hidden.Length);
        bool any = false;
        foreach (var piece in pieces)
            if (piece.renderer.gameObject.activeSelf)
                foreach (int v in piece.hiddenVertices)
                {
                    hidden[v] = true;
                    any = true;
                }
        if (!any)
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
        for (int sub = 0; sub < sourceMesh.subMeshCount; sub++)
        {
            sourceMesh.GetTriangles(triangles, sub);
            int count = triangles.Count / 3;
            int kept = 0;
            for (int t = 0; t < count; t++)
            {
                if (hidden[triangles[t * 3]] && hidden[triangles[t * 3 + 1]] && hidden[triangles[t * 3 + 2]])
                    continue;
                triangles[kept * 3] = triangles[t * 3];
                triangles[kept * 3 + 1] = triangles[t * 3 + 1];
                triangles[kept * 3 + 2] = triangles[t * 3 + 2];
                kept++;
            }
            triangles.RemoveRange(kept * 3, triangles.Count - kept * 3);
            maskedMesh.SetTriangles(triangles, sub, false);
        }
        body.sharedMesh = maskedMesh;
    }
}
