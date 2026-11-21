"""
Ставит скелет Unity Humanoid в модель Assets/Models/20261121003938_8b8745d5.fbx (девушка в купальнике, T-поза).

Запуск (из корня проекта):
    "<blender.exe>" --background --factory-startup --python Tools/Blender/rig_model_8b8745d5.py -- ^
        --src "<FBX без костей>" --out "<FBX с костями>" [--blend "<файл .blend>"]

Что делает:
  * импортирует части модели: part_000 — тело с купальником и верхними ресницами, part_001..007 — зубы, язык,
    глаза, нижние ресницы. Поворот из FBX запекается в вершины, модель разворачивается лицом в -Y
    (в исходном FBX она смотрит в +Y). Лицом в -Y в Blender она смотрит в +Z в Unity, как SurvivorRigged;
  * строит скелет с именами костей Unity Humanoid в T-позе, места суставов сняты замерами сечений меша.
    Пальцы названы без пробелов: LeftThumbProximal и так далее;
  * веса тела считает Blender по теплу от костей (Automatic Weights) на сплошной копии тела, затем они
    переносятся на тело. Глаза, зубы, язык и нижние ресницы целиком на кости Head;
  * веса таза (Hips и UpperLeg) строятся заново по высоте над тазобедренным суставом: тепло ломало бок
    и ягодицу, когда бедро поднимается;
  * вес кисти делится между ладонью и пальцами по расстоянию до костей пальца, как в rig_survivor_fbx.py;
  * оставляет не больше 4 костей на вершину, сводит части в один меш и экспортирует FBX без текстур
    (материал "Material" в Unity заменяется на 20261121003938_8b8745d5.mat).
Координаты суставов ниже — после разворота: лицом в -Y, левая сторона персонажа на +X.
"""
import argparse
import math
import os
import sys

import bpy
from mathutils import Matrix, Vector

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
parser = argparse.ArgumentParser()
parser.add_argument('--src', required=True)
parser.add_argument('--out', required=True)
parser.add_argument('--blend', default='')
args = parser.parse_args(argv)
src, out = os.path.abspath(args.src), os.path.abspath(args.out)

bpy.ops.wm.read_factory_settings(use_empty=True)

# ----------------------------------------------------------------------------------------------
# Скелет. Лицом в -Y, левая сторона = +X, середина тела на x = 0.
# ----------------------------------------------------------------------------------------------
BONES = []  # (name, head, tail, parent)


def add_bone(name, head, tail, parent=None):
    BONES.append((name, Vector(head), Vector(tail), parent))


add_bone('Hips', (0, 0.015, 0.55), (0, 0.012, 0.62))
add_bone('Spine', (0, 0.012, 0.62), (0, 0.010, 0.72), 'Hips')
add_bone('Chest', (0, 0.010, 0.72), (0, 0.015, 0.885), 'Spine')
add_bone('Neck', (0, 0.020, 0.895), (0, 0.015, 0.955), 'Chest')
add_bone('Head', (0, 0.015, 0.955), (0, 0.015, 1.085), 'Neck')
for _s, _side in ((1, 'Left'), (-1, 'Right')):
    # Плечевой сустав над подмышкой, внутри дельты: подмышка кончается на x 0.105, верх плеча на z 0.905,
    # толщина руки 0.055. Сустав на краю дельты (x 0.115) делал плечи квадратными: верх плеча оставался
    # полкой, а рука ломалась углом вниз от его края.
    add_bone(f'{_side}Shoulder', (_s * 0.020, 0.035, 0.880), (_s * 0.092, 0.040, 0.875), 'Chest')
    add_bone(f'{_side}UpperArm', (_s * 0.092, 0.040, 0.875), (_s * 0.270, 0.045, 0.857), f'{_side}Shoulder')
    add_bone(f'{_side}LowerArm', (_s * 0.270, 0.045, 0.857), (_s * 0.415, 0.037, 0.852), f'{_side}UpperArm')
    add_bone(f'{_side}Hand', (_s * 0.415, 0.037, 0.852), (_s * 0.462, 0.035, 0.849), f'{_side}LowerArm')
    add_bone(f'{_side}UpperLeg', (_s * 0.065, 0.020, 0.530), (_s * 0.064, 0.024, 0.300), 'Hips')
    add_bone(f'{_side}LowerLeg', (_s * 0.064, 0.024, 0.300), (_s * 0.062, 0.050, 0.080), f'{_side}UpperLeg')
    add_bone(f'{_side}Foot', (_s * 0.062, 0.050, 0.080), (_s * 0.072, -0.035, 0.025), f'{_side}LowerLeg')
    add_bone(f'{_side}Toes', (_s * 0.072, -0.035, 0.025), (_s * 0.078, -0.070, 0.020), f'{_side}Foot')

# Пальцы: по три кости. Ладонью вниз, большой палец ниже остальных и смотрит в -Y.
# (палец, y, z, начало, конец) - начало и конец считаются от середины тела по X
FINGERS = [('Index', 0.018, 0.849, 0.462, 0.511), ('Middle', 0.031, 0.849, 0.462, 0.518),
           ('Ring', 0.045, 0.849, 0.462, 0.512), ('Little', 0.055, 0.849, 0.458, 0.498)]
FINGER_SPLIT = (0.0, 0.42, 0.74, 1.0)  # доли длины пальца на суставах
THUMB = [(0.425, 0.012, 0.842), (0.440, 0.004, 0.836), (0.453, -0.003, 0.831), (0.467, -0.007, 0.826)]
PHALANGES = ('Proximal', 'Intermediate', 'Distal')
for _s, _side in ((1, 'Left'), (-1, 'Right')):
    points = [(_s * d, y, z) for d, y, z in THUMB]
    for i, phalanx in enumerate(PHALANGES):
        add_bone(f'{_side}Thumb{phalanx}', points[i], points[i + 1],
                 f'{_side}Thumb{PHALANGES[i - 1]}' if i else f'{_side}Hand')
    for finger, y, z, start, end in FINGERS:
        points = [(_s * (start + (end - start) * t), y, z) for t in FINGER_SPLIT]
        for i, phalanx in enumerate(PHALANGES):
            add_bone(f'{_side}{finger}{phalanx}', points[i], points[i + 1],
                     f'{_side}{finger}{PHALANGES[i - 1]}' if i else f'{_side}Hand')

BONE_NAMES = [b[0] for b in BONES]
FINGER_CHAINS = {side: {finger: [f'{side}{finger}{p}' for p in PHALANGES]
                        for finger in ['Thumb'] + [f[0] for f in FINGERS]}
                 for side in ('Left', 'Right')}
FINGER_BONES = {name for chains in FINGER_CHAINS.values() for chain in chains.values() for name in chain}

# ----------------------------------------------------------------------------------------------
# Меш: импорт, поворот из FBX и разворот лицом в -Y запекаются в вершины
# ----------------------------------------------------------------------------------------------
bpy.ops.import_scene.fbx(filepath=src)
parts = {o.name: o for o in bpy.context.scene.objects if o.type == 'MESH'}
if 'part_000' not in parts:
    raise RuntimeError(f'part_000 (body) not found in {src}, got {sorted(parts)}')
turn = Matrix.Rotation(math.pi, 4, 'Z')
for o in parts.values():
    o.data.transform(turn @ o.matrix_world)
    o.matrix_world = Matrix.Identity(4)
for o in [o for o in bpy.context.scene.objects if o.type != 'MESH']:
    bpy.data.objects.remove(o)
body = parts['part_000']
head_parts = [o for n, o in parts.items() if n != 'part_000']
print(f'[Rig] body verts={len(body.data.vertices)} polys={len(body.data.polygons)}, '
      f'head parts: {", ".join(o.name for o in head_parts)}')

# ----------------------------------------------------------------------------------------------
# Скелет
# ----------------------------------------------------------------------------------------------
collection = bpy.context.scene.collection
arm_data = bpy.data.armatures.new('Armature')
arm_obj = bpy.data.objects.new('Armature', arm_data)
collection.objects.link(arm_obj)

bpy.context.view_layer.objects.active = arm_obj
arm_obj.select_set(True)
bpy.ops.object.mode_set(mode='EDIT')
edit_bones = {}
for name, head, tail, parent in BONES:
    eb = arm_data.edit_bones.new(name)
    eb.head, eb.tail = head, tail
    edit_bones[name] = eb
for name, head, tail, parent in BONES:
    if parent:
        edit_bones[name].parent = edit_bones[parent]
bpy.ops.object.mode_set(mode='OBJECT')

# Unity строит Humanoid только из одного дерева костей: корень один, Hips, остальные кости под ним
roots = [b.name for b in arm_data.bones if b.parent is None]
if roots != ['Hips']:
    raise RuntimeError(f'skeleton must have one root Hips, got roots {roots}: check parents in BONES')

# ----------------------------------------------------------------------------------------------
# Веса тела. Меш из генератора с дырами и несвязанными кусками, поэтому тепло считается на сплошной
# копии (voxel remesh), а веса переходят на тело с ближайшей точки поверхности копии.
# ----------------------------------------------------------------------------------------------
proxy = body.copy()
proxy.data = body.data.copy()
proxy.name = 'WeightProxy'
collection.objects.link(proxy)
remesh = proxy.modifiers.new('Remesh', 'REMESH')
remesh.mode = 'VOXEL'
remesh.voxel_size = 0.005
bpy.ops.object.select_all(action='DESELECT')
proxy.select_set(True)
bpy.context.view_layer.objects.active = proxy
bpy.ops.object.modifier_apply(modifier=remesh.name)
print(f'[Rig] proxy verts={len(proxy.data.vertices)}')

# Пальцы в тепле не участвуют: на копии из кубиков они слипаются. Вес кисти делится ниже.
for name in FINGER_BONES:
    arm_data.bones[name].use_deform = False
bpy.ops.object.select_all(action='DESELECT')
proxy.select_set(True)
arm_obj.select_set(True)
bpy.context.view_layer.objects.active = arm_obj
bpy.ops.object.parent_set(type='ARMATURE_AUTO')
for name in FINGER_BONES:
    arm_data.bones[name].use_deform = True
proxy_weighted = sum(1 for v in proxy.data.vertices if sum(g.weight for g in v.groups) > 1e-4)
print(f'[Rig] proxy vertices with heat weights: {proxy_weighted}/{len(proxy.data.vertices)}')

for name in BONE_NAMES:
    body.vertex_groups.new(name=name)
transfer = body.modifiers.new('WeightTransfer', 'DATA_TRANSFER')
transfer.object = proxy
transfer.use_vert_data = True
transfer.data_types_verts = {'VGROUP_WEIGHTS'}
transfer.vert_mapping = 'POLYINTERP_NEAREST'
transfer.layers_vgroup_select_src = 'ALL'
transfer.layers_vgroup_select_dst = 'NAME'
bpy.ops.object.select_all(action='DESELECT')
body.select_set(True)
bpy.context.view_layer.objects.active = body
bpy.ops.object.modifier_apply(modifier=transfer.name)
bpy.data.objects.remove(proxy)

by_name = {g.name: g for g in body.vertex_groups}
groups = {g.index: g.name for g in body.vertex_groups}


def dist_point_segment(p, a, b):
    ab = b - a
    t = max(0.0, min(1.0, (p - a).dot(ab) / ab.length_squared))
    return (p - (a + ab * t)).length


segments = {name: (h, t) for name, h, t, _ in BONES}
unweighted = 0
for v in body.data.vertices:
    if sum(g.weight for g in v.groups) > 1e-4:
        continue
    unweighted += 1
    nearest = min((n for n in BONE_NAMES if n not in FINGER_BONES),
                  key=lambda n: dist_point_segment(v.co, *segments[n]))
    by_name[nearest].add([v.index], 1.0, 'REPLACE')
print(f'[Rig] vertices without heat weights, given to nearest bone: {unweighted}/{len(body.data.vertices)}')

# ----------------------------------------------------------------------------------------------
# Таз. Тепло даёт бедру вес до самой талии (сбоку 0,33 на 0,11 выше сустава), а у сустава сбоку 0,85:
# на приземлении и в прыжке, когда бедро поднимается, талия тянется за ним, а бок и ягодица ломаются углом.
# Здесь доля таза (Hips и обе UpperLeg) делится заново: вес бедра плавно спадает с высотой над тазобедренным
# суставом, полоса перехода зависит от стороны бедра - спереди узкая (складка паха), сзади широкая
# (ягодица делится между тазом и бедром), сбоку средняя. Spine, Chest и колено свои веса сохраняют.
# Левая половина не получает веса правого бедра и наоборот, кроме полосы PELVIS_MID посередине (промежность).
# ----------------------------------------------------------------------------------------------
# (низ, верх) полосы перехода по высоте над суставом: ниже низа вершина целиком за бедром, выше верха - за тазом
PELVIS_BLEND = {'front': (-0.030, 0.035), 'lateral': (-0.040, 0.070), 'back': (-0.065, 0.090),
                'medial': (-0.060, 0.010)}
PELVIS_MID = 0.008
PELVIS_ZONE = (0.38, 0.72)  # по высоте: ниже - бедро, выше - талия, их веса не трогаются


def smoothstep(t):
    t = max(0.0, min(1.0, t))
    return t * t * (3.0 - 2.0 * t)


def leg_share(p, side, s):
    """Доля бедра side в весе таза для точки p по высоте над суставом и стороне бедра."""
    head, tail = segments[f'{side}UpperLeg']
    axis = (head - tail).normalized()  # вверх вдоль бедра
    rel = p - head
    h = rel.dot(axis)
    lat, back = s * rel.x, rel.y
    length = math.hypot(lat, back)
    if length < 1e-6:
        mix = {k: 0.25 for k in PELVIS_BLEND}
    else:
        lat, back = lat / length, back / length
        mix = {'lateral': max(0.0, lat) ** 2, 'medial': max(0.0, -lat) ** 2,
               'back': max(0.0, back) ** 2, 'front': max(0.0, -back) ** 2}
    lo = sum(mix[k] * PELVIS_BLEND[k][0] for k in mix)
    hi = sum(mix[k] * PELVIS_BLEND[k][1] for k in mix)
    return 1.0 - smoothstep((h - lo) / (hi - lo))


PELVIS = ('Hips', 'LeftUpperLeg', 'RightUpperLeg')
reweighted = 0
for v in body.data.vertices:
    if not PELVIS_ZONE[0] <= v.co.z <= PELVIS_ZONE[1]:
        continue
    share = sum(g.weight for g in v.groups if groups[g.group] in PELVIS)
    if share <= 1e-4:
        continue
    left = smoothstep((v.co.x + PELVIS_MID) / (2 * PELVIS_MID))
    weights = {'LeftUpperLeg': left * leg_share(v.co, 'Left', 1),
               'RightUpperLeg': (1.0 - left) * leg_share(v.co, 'Right', -1)}
    weights['Hips'] = 1.0 - weights['LeftUpperLeg'] - weights['RightUpperLeg']
    for name, w in weights.items():
        if w * share > 1e-4:
            by_name[name].add([v.index], w * share, 'REPLACE')
        else:
            by_name[name].remove([v.index])
    reweighted += 1
print(f'[Rig] pelvis: Hips/UpperLeg weights rebuilt on {reweighted} vertices')


def chain_weights(p, chain):
    """Мягкие веса среди костей одной цепочки: две ближние кости, на суставе пополам."""
    scored = sorted(((dist_point_segment(p, *segments[n]) + 0.003) ** -3, n) for n in chain)[-2:]
    total = sum(w for w, _ in scored)
    return {n: w / total for w, n in scored}


# Вес кисти делится между ладонью и одним пальцем: тем, к чьим костям вершина ближе всего.
# Большой палец растёт из ладони, его вес считается по расстоянию до всей его цепочки.
# Вершина ладони до костяшек остаётся за кистью; первая фаланга забирает у неё вес только
# в последних PALM_BLEND метрах перед костяшками, у самих костяшек - половину.
PALM_BLEND = 0.006
FINGER_START = {f[0]: f[3] for f in FINGERS}
for side, chains in FINGER_CHAINS.items():
    hand = f'{side}Hand'
    hand_index = by_name[hand].index
    split = 0
    for v in body.data.vertices:
        w_hand = next((g.weight for g in v.groups if g.group == hand_index), 0.0)
        if w_hand <= 0.0:
            continue
        dist = {f: min(dist_point_segment(v.co, *segments[n]) for n in chain) for f, chain in chains.items()}
        finger = min(dist, key=dist.get)
        along = abs(v.co.x)  # расстояние от середины тела вдоль руки
        if finger == 'Thumb' or along >= FINGER_START[finger]:
            weights = chain_weights(v.co, [hand] + chains[finger])
        else:
            t = 0.5 * max(0.0, min(1.0, (along - FINGER_START[finger] + PALM_BLEND) / PALM_BLEND))
            weights = {hand: 1.0 - t, chains[finger][0]: t}
        if hand in weights:
            by_name[hand].add([v.index], w_hand * weights[hand], 'REPLACE')
        else:
            by_name[hand].remove([v.index])
        for n, w in weights.items():
            if n != hand:
                by_name[n].add([v.index], w_hand * w, 'REPLACE')
        split += 1
    print(f'[Rig] {hand}: weight split between palm and fingers on {split} vertices')

bpy.ops.object.select_all(action='DESELECT')
body.select_set(True)
bpy.context.view_layer.objects.active = body
bpy.ops.object.vertex_group_limit_total(group_select_mode='ALL', limit=4)
bpy.ops.object.vertex_group_normalize_all(group_select_mode='ALL', lock_active=False)

# Проверка: у какой кости сколько вершин главные, и где они лежат
owners = {n: [] for n in BONE_NAMES}
for v in body.data.vertices:
    best = max(v.groups, key=lambda g: g.weight)
    owners[groups[best.group]].append(v.co)
for name in BONE_NAMES:
    co = owners[name]
    if not co:
        print(f'[Rig] bone {name}: no vertices')
        continue
    lo = Vector((min(c.x for c in co), min(c.y for c in co), min(c.z for c in co)))
    hi = Vector((max(c.x for c in co), max(c.y for c in co), max(c.z for c in co)))
    print(f'[Rig] bone {name:24s} verts={len(co):5d} x[{lo.x:+.3f},{hi.x:+.3f}] '
          f'y[{lo.y:+.3f},{hi.y:+.3f}] z[{lo.z:+.3f},{hi.z:+.3f}]')

# Глаза, зубы, язык и нижние ресницы двигаются вместе с головой
for o in head_parts:
    g = o.vertex_groups.new(name='Head')
    g.add([v.index for v in o.data.vertices], 1.0, 'REPLACE')

# Все части в один меш (группы вершин сливаются по именам костей)
bpy.ops.object.select_all(action='DESELECT')
for o in head_parts:
    o.select_set(True)
body.select_set(True)
bpy.context.view_layer.objects.active = body
bpy.ops.object.join()
body.name = 'Body'
body.data.name = 'Body'
print(f'[Rig] joined mesh verts={len(body.data.vertices)} polys={len(body.data.polygons)}')

body.parent = arm_obj
arm_mod = body.modifiers.new('Armature', 'ARMATURE')
arm_mod.object = arm_obj

# ----------------------------------------------------------------------------------------------
# Экспорт (оси как в rig_survivor_fbx.py). Текстуры не нужны: материал подставляет Unity.
# ----------------------------------------------------------------------------------------------
for m in bpy.data.materials:
    m.use_nodes = False
bpy.ops.object.select_all(action='DESELECT')
arm_obj.select_set(True)
body.select_set(True)
bpy.context.view_layer.objects.active = arm_obj

os.makedirs(os.path.dirname(out), exist_ok=True)
bpy.ops.export_scene.fbx(
    filepath=out,
    use_selection=True,
    object_types={'ARMATURE', 'MESH'},
    use_mesh_modifiers=False,
    add_leaf_bones=False,
    use_armature_deform_only=True,
    bake_anim=False,
    apply_unit_scale=True,
    apply_scale_options='FBX_SCALE_ALL',
    bake_space_transform=True,
    axis_forward='-Z',
    axis_up='Y',
    path_mode='STRIP',
    embed_textures=False,
)
print(f'[Rig] exported {out}')

if args.blend:
    blend = os.path.abspath(args.blend)
    os.makedirs(os.path.dirname(blend), exist_ok=True)
    bpy.context.preferences.filepaths.save_version = 0
    bpy.ops.wm.save_as_mainfile(filepath=blend, compress=True)
    print(f'[Rig] saved {blend}')
