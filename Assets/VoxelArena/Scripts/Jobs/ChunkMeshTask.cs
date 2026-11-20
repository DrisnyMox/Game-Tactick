using Unity.Mathematics;

/// <summary>Один меш одного чанка: начало чанка в вокселях и вид меша</summary>
public struct ChunkMeshTask
{
    public int3 origin;
    public ChunkMeshKind kind;
}
