using Unity.Mathematics;

/// <summary>Меш чанка в узле: номер в chunkMeshes и сдвиг его вершин внутри узла (в вокселях)</summary>
public struct NodePart
{
    public int mesh;
    public float3 offset;
}
