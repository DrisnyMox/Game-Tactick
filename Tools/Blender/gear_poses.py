"""
Позы для подгонки и замера снаряжения (fit_gear.py, measure_gear.py): сгибы, на которых кожа лезет сквозь часть.

POSES - заданные вручную: повороты костей вокруг осей мира (лицом в -Y, левая сторона +X), по порядку
от родителя к ребёнку. Руки вверх и присед в анимациях игры нет, но часть должна их выдерживать.
RECORDED - позы из игры: Tools/Unity/ProbeGear.cs в Play записывает кости игрока (анимация, IK рук на винтовке,
наклон корпуса за прицелом) в gear_poses.json рядом с этим файлом: голову кости и поворот от T-позы в осях Blender.
"""
import json
import math
import os

import bpy
from mathutils import Matrix, Vector

POSES = {
    'tpose': [],
    'arms_down': [('LeftUpperArm', 'Y', 70), ('RightUpperArm', 'Y', -70)],
    'arms_up': [('LeftShoulder', 'Y', -15), ('RightShoulder', 'Y', 15),
                ('LeftUpperArm', 'Y', -75), ('RightUpperArm', 'Y', 75)],
    'crouch': [('Spine', 'X', 25), ('Chest', 'X', 10),
               ('LeftUpperLeg', 'X', -100), ('RightUpperLeg', 'X', -80),
               ('LeftLowerLeg', 'X', 120), ('RightLowerLeg', 'X', 110),
               ('LeftUpperArm', 'Y', 60), ('RightUpperArm', 'Y', -60)],
    'twist': [('Spine', 'Z', 25), ('Chest', 'Z', 20), ('Spine', 'Y', 15),
              ('LeftUpperArm', 'Y', 50), ('RightUpperArm', 'Y', -50)],
}

# В игре кожу считают по двум костям на вершину: качество Mobile (WebGL и Editor) - skinWeights 2.
# Тело и часть в подгонке и замере гнутся так же, иначе кожа в игре выезжает из-под части
SKIN_BONES = 2


def limit_bones(obj):
    """Оставляет у вершин obj SKIN_BONES самых весомых костей с суммой 1, как Unity при skinWeights 2."""
    bpy.ops.object.select_all(action='DESELECT')
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.vertex_group_limit_total(group_select_mode='ALL', limit=SKIN_BONES)
    bpy.ops.object.vertex_group_normalize_all(group_select_mode='ALL', lock_active=False)


RECORDED = {}
# Выборка вершин запечённых мешей из игры: поза -> {'body' или имя части: [точки]}
RECORDED_MESH = {}
_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'gear_poses.json')
if os.path.exists(_path):
    with open(_path, encoding='utf-8') as _f:
        _file = json.load(_f)
    for _pose in _file['poses']:
        _d = _pose['data']
        _name = 'play:' + _pose['name']
        RECORDED[_name] = {
            bone: (Vector(_d[i * 12:i * 12 + 3]),
                   Matrix((_d[i * 12 + 3:i * 12 + 6], _d[i * 12 + 6:i * 12 + 9], _d[i * 12 + 9:i * 12 + 12])))
            for i, bone in enumerate(_file['bones'])}
        RECORDED_MESH[_name] = {
            key: [Vector(_pose[obj][k:k + 3]) for k in range(0, len(_pose[obj]), 3)]
            for key, obj in [('body', _file['body'])] + [(p, p) for p in _file['parts']]}


def all_poses(recorded_step=1):
    """Имена поз: заданные вручную и каждая recorded_step-я из игры."""
    return list(POSES) + list(RECORDED)[::recorded_step]


def _ordered(arm_obj):
    return sorted(arm_obj.pose.bones, key=lambda pb: len(pb.bone.parent_recursive))


def recorded_targets(arm_obj, name):
    """Матрицы костей позы из игры в осях скелета; у костей без записи - от родителя в покое."""
    rec = RECORDED[name]
    target = {}
    for pb in _ordered(arm_obj):
        b = pb.bone
        if b.name in rec:
            head, rot = rec[b.name]
            target[b.name] = Matrix.Translation(head) @ (rot @ b.matrix_local.to_3x3()).to_4x4()
        elif b.parent:
            target[b.name] = target[b.parent.name] @ b.parent.matrix_local.inverted() @ b.matrix_local
        else:
            target[b.name] = b.matrix_local.copy()
    return target


def set_pose(arm_obj, name):
    """Ставит скелет в позу name (tpose - поза покоя)."""
    if any(abs(a - b) > 1e-5 for ra, rb in zip(arm_obj.matrix_world, Matrix.Identity(4)) for a, b in zip(ra, rb)):
        raise RuntimeError('armature must stay at the origin without rotation')
    for pb in arm_obj.pose.bones:
        pb.matrix_basis = Matrix.Identity(4)
    bpy.context.view_layer.update()
    if name in POSES:
        for bone, axis, deg in POSES[name]:
            pb = arm_obj.pose.bones[bone]
            head = pb.matrix.translation.copy()
            pb.matrix = (Matrix.Translation(head) @ Matrix.Rotation(math.radians(deg), 4, axis)
                         @ Matrix.Translation(-head) @ pb.matrix)
            bpy.context.view_layer.update()
        return
    target = recorded_targets(arm_obj, name)
    for pb in _ordered(arm_obj):
        b = pb.bone
        if b.parent:
            rest = b.parent.matrix_local.inverted() @ b.matrix_local
            pb.matrix_basis = rest.inverted() @ target[b.parent.name].inverted() @ target[b.name]
        else:
            pb.matrix_basis = b.matrix_local.inverted() @ target[b.name]
    bpy.context.view_layer.update()


def recorded_error(arm_obj, name):
    """Насколько голова кости из записи расходится с головой, которую даёт поворот родителя из той же записи.
    Большое расхождение - оси или повороты переведены из Unity неверно."""
    target = recorded_targets(arm_obj, name)
    worst = 0.0
    for pb in arm_obj.pose.bones:
        b = pb.bone
        if b.parent and b.name in RECORDED[name]:
            predicted = (target[b.parent.name] @ b.parent.matrix_local.inverted() @ b.matrix_local).translation
            worst = max(worst, (predicted - target[b.name].translation).length)
    return worst
