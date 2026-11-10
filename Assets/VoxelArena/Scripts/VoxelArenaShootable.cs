using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Пуля выбивает воксели арены шаром в месте попадания. Коллайдеры чанков лежат под объектом арены,
/// поэтому SurvivorShooter находит этот компонент от любого чанка (GetComponentInParent).
/// Воронка от пули пересобирает в ArenaNavMesh только область NavMesh вокруг неё.
/// </summary>
[RequireComponent(typeof(VoxelArena))]
public class VoxelArenaShootable : MonoBehaviour, IShootable
{
    [Tooltip("Радиус шара, который выбивает одна пуля, в вокселях")]
    [SerializeField] float craterRadiusInVoxels = 1.3f;

    VoxelArena arena;

    void Awake()
    {
        arena = GetComponent<VoxelArena>();
    }

    public HitSurface SurfaceAt(RaycastHit hit)
    {
        var cell = (int3)math.floor(arena.WorldToVoxel(hit.point - hit.normal * (arena.VoxelSize * 0.5f)));
        switch (arena.GetBlock(cell))
        {
            case VoxelBlocks.Sand:
            case VoxelBlocks.Dirt:
            case VoxelBlocks.Grass:
            case VoxelBlocks.Snow:
                return HitSurface.Soft;
            case VoxelBlocks.Wood:
            case VoxelBlocks.Leaves:
            case VoxelBlocks.LeavesAlt:
                return HitSurface.Wood;
            default:
                return HitSurface.Hard;
        }
    }

    public bool OnShot(RaycastHit hit, Vector3 direction, float damage, CharacterHealth attacker)
    {
        // Центр чуть внутри поверхности, чтобы пуля выбивала грань, в которую попала, а не воздух перед ней
        var center = hit.point - hit.normal * (arena.VoxelSize * 0.5f);
        arena.Explode(center, craterRadiusInVoxels * arena.VoxelSize);
        return false;
    }
}
