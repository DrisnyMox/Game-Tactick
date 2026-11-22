using UnityEngine;

/// <summary>
/// Подъём после взрыва без анимации подъёма: поза рэгдолла плавно переходит в стойку из аниматора.
/// Capture запоминает, как лежат кости, Apply после аниматора смешивает запомненную позу с его позой.
/// Таз смешивается в мировых координатах (корень персонажа к этому времени уже переставлен к тазу),
/// остальные кости — поворотами относительно родителя. Ведёт CharacterHealth.
/// </summary>
public sealed class RagdollGetUp
{
    readonly Transform hips;
    // Кости скелета Humanoid, кроме таза
    readonly Transform[] bones;
    readonly Quaternion[] lying;
    Vector3 hipsPosition;
    Quaternion hipsRotation;

    public RagdollGetUp(Animator animator)
    {
        hips = animator.GetBoneTransform(HumanBodyBones.Hips);
        var list = new System.Collections.Generic.List<Transform>();
        for (var bone = HumanBodyBones.Hips + 1; bone < HumanBodyBones.LastBone; bone++)
        {
            var transform = animator.GetBoneTransform(bone);
            if (transform != null)
                list.Add(transform);
        }
        bones = list.ToArray();
        lying = new Quaternion[bones.Length];
    }

    /// <summary>Запомнить, как лежит рэгдолл</summary>
    public void Capture()
    {
        hips.GetPositionAndRotation(out hipsPosition, out hipsRotation);
        for (int i = 0; i < bones.Length; i++)
            lying[i] = bones[i].localRotation;
    }

    /// <summary>t от 0 (лежит как запомнено) до 1 (поза аниматора). Звать в LateUpdate, после аниматора</summary>
    public void Apply(float t)
    {
        hips.GetPositionAndRotation(out var position, out var rotation);
        hips.SetPositionAndRotation(Vector3.Lerp(hipsPosition, position, t), Quaternion.Slerp(hipsRotation, rotation, t));
        for (int i = 0; i < bones.Length; i++)
            bones[i].localRotation = Quaternion.Slerp(lying[i], bones[i].localRotation, t);
    }
}
