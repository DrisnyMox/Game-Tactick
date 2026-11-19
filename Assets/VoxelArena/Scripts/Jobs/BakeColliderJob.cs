using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

/// <summary>
/// Готовит данные PhysX для MeshCollider чанков в рабочих потоках. Коллайдер, которому потом назначают
/// такой меш с теми же настройками, берёт их готовыми и не готовит сам в главном потоке.
/// Без Burst: Physics.BakeMesh — вызов движка.
/// </summary>
public struct BakeColliderJob : IJobParallelFor
{
    [ReadOnly] public NativeArray<EntityId> meshes;
    public MeshColliderCookingOptions options;

    public void Execute(int index) => Physics.BakeMesh(meshes[index], false, options);
}
