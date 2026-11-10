"""
Ставит скелет Unity Humanoid в готовую модель выжившего (один меш из FBX без костей) для Blender 5.x.

Запуск (из корня проекта):
    "D:\\Blender 5.2\\blender.exe" --background --factory-startup --python Tools/Blender/rig_survivor_fbx.py -- ^
        --src "<FBX без костей>" --out "<FBX с костями>" [--blend "<файл .blend>"]

Что делает:
  * импортирует меш и запекает в него поворот из FBX. Персонаж остаётся лицом в -Y, в Unity это +Z;
  * строит скелет с именами костей Unity Humanoid в T-позе; места суставов сняты срезами меша (slice_fbx.py),
    пальцы - срезами кистей (slice_hands.py). Пальцы названы без пробелов: LeftThumbProximal и так далее;
  * веса считает Blender по теплу от костей (Automatic Weights). Вершины, которым тепло не дошло,
    получают вес ближайшей кости, и скрипт пишет в лог, сколько их было;
  * оставляет не больше 4 костей на вершину и экспортирует FBX с текстурами внутри, настройки осей как в create_survivor.py.
Координаты суставов ниже заданы как в исходном FBX: лицом в -Y, левая сторона персонажа на +X.
"""
import argparse
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
# Скелет. Лицом в -Y, левая сторона = +X. Середина тела по X сдвинута на CX.
# ----------------------------------------------------------------------------------------------
CX = 0.01
BONES = []  # (name, head, tail, parent)


def add_bone(name, head, tail, parent=None):
    BONES.append((name, Vector(head), Vector(tail), parent))


add_bone('Hips', (CX, 0, 0.60), (CX, 0, 0.67))
add_bone('Spine', (CX, 0, 0.67), (CX, 0, 0.77), 'Hips')
add_bone('Chest', (CX, 0, 0.77), (CX, 0.01, 0.90), 'Spine')
add_bone('Neck', (CX, 0.01, 0.90), (CX, 0.01, 0.965), 'Chest')
add_bone('Head', (CX, 0.01, 0.965), (CX, 0.01, 1.13), 'Neck')
for _s, _side in ((1, 'Left'), (-1, 'Right')):
    add_bone(f'{_side}Shoulder', (CX + _s * 0.035, 0.025, 0.88), (CX + _s * 0.125, 0.025, 0.88), 'Chest')
    add_bone(f'{_side}UpperArm', (CX + _s * 0.125, 0.025, 0.88), (CX + _s * 0.275, 0.025, 0.873), f'{_side}Shoulder')
    add_bone(f'{_side}LowerArm', (CX + _s * 0.275, 0.025, 0.873), (CX + _s * 0.425, 0.025, 0.865), f'{_side}UpperArm')
    add_bone(f'{_side}Hand', (CX + _s * 0.425, 0.022, 0.866), (CX + _s * 0.47, 0.024, 0.862), f'{_side}LowerArm')
    add_bone(f'{_side}UpperLeg', (CX + _s * 0.075, 0, 0.60), (CX + _s * 0.075, -0.008, 0.33), 'Hips')
    add_bone(f'{_side}LowerLeg', (CX + _s * 0.075, -0.008, 0.33), (CX + _s * 0.075, 0.02, 0.09), f'{_side}UpperLeg')
    add_bone(f'{_side}Foot', (CX + _s * 0.075, 0.02, 0.09), (CX + _s * 0.075, -0.06, 0.03), f'{_side}LowerLeg')
    add_bone(f'{_side}Toes', (CX + _s * 0.075, -0.06, 0.03), (CX + _s * 0.075, -0.11, 0.03), f'{_side}Foot')

# Пальцы: по три кости, места сняты срезами кистей (slice_hands.py). Ладонью вниз, большой палец смотрит в -Y.
# (палец, y, z, начало, конец) - начало и конец считаются от середины тела по X
FINGERS = [('Index', 0.0055, 0.861, 0.470, 0.516), ('Middle', 0.022, 0.863, 0.470, 0.521),
           ('Ring', 0.036, 0.863, 0.470, 0.514), ('Little', 0.050, 0.861, 0.468, 0.502)]
FINGER_SPLIT = (0.0, 0.42, 0.74, 1.0)  # доли длины пальца на суставах
THUMB = [(0.432, 0.000, 0.853), (0.448, -0.010, 0.846), (0.460, -0.018, 0.840), (0.471, -0.024, 0.834)]
PHALANGES = ('Proximal', 'Intermediate', 'Distal')
for _s, _side in ((1, 'Left'), (-1, 'Right')):
    points = [(CX + _s * d, y, z) for d, y, z in THUMB]
    for i, phalanx in enumerate(PHALANGES):
        add_bone(f'{_side}Thumb{phalanx}', points[i], points[i + 1],
                 f'{_side}Thumb{PHALANGES[i - 1]}' if i else f'{_side}Hand')
    for finger, y, z, start, end in FINGERS:
        points = [(CX + _s * (start + (end - start) * t), y, z) for t in FINGER_SPLIT]
        for i, phalanx in enumerate(PHALANGES):
            add_bone(f'{_side}{finger}{phalanx}', points[i], points[i + 1],
                     f'{_side}{finger}{PHALANGES[i - 1]}' if i else f'{_side}Hand')

BONE_NAMES = [b[0] for b in BONES]
FINGER_CHAINS = {side: {finger: [f'{side}{finger}{p}' for p in PHALANGES]
                        for finger in ['Thumb'] + [f[0] for f in FINGERS]}
                 for side in ('Left', 'Right')}
FINGER_BONES = {name for chains in FINGER_CHAINS.values() for chain in chains.values() for name in chain}


# Разворота, как flip() в create_survivor.py, здесь нет: с ним этот персонаж в Unity смотрел в -Z,
# и Humanoid играл анимации криво. Лицом в -Y в Blender он смотрит в +Z в Unity.


# ----------------------------------------------------------------------------------------------
# Меш: импорт, поворот из FBX запекается в вершины
# ----------------------------------------------------------------------------------------------
bpy.ops.import_scene.fbx(filepath=src)
meshes = [o for o in bpy.context.scene.objects if o.type == 'MESH']
if len(meshes) != 1:
    raise RuntimeError(f'expected one mesh in {src}, got {[o.name for o in meshes]}')
body = meshes[0]
body.name = 'Survivor_Body'
body.data.name = 'Survivor_Body'
body.data.transform(body.matrix_world)
body.matrix_world = Matrix.Identity(4)
print(f'[Rig] mesh verts={len(body.data.vertices)} polys={len(body.data.polygons)}')

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
# Веса. На исходном меше тепло не считается: меш из генератора с дырами и несвязанными кусками.
# Поэтому Blender считает тепло на сплошной копии (voxel remesh), а веса переходят на исходный меш
# с ближайшей точки поверхности копии. Вершинам, которым вес не дошёл, вес даёт ближайшая кость.
# ----------------------------------------------------------------------------------------------
proxy = body.copy()
proxy.data = body.data.copy()
proxy.name = 'WeightProxy'
collection.objects.link(proxy)
remesh = proxy.modifiers.new('Remesh', 'REMESH')
remesh.mode = 'VOXEL'
remesh.voxel_size = 0.006
bpy.ops.object.select_all(action='DESELECT')
proxy.select_set(True)
bpy.context.view_layer.objects.active = proxy
bpy.ops.object.modifier_apply(modifier=remesh.name)
print(f'[Rig] proxy verts={len(proxy.data.vertices)}')

# Пальцы в тепле не участвуют: на копии из кубиков по 6 мм они слипаются (зазор между ними около 2 мм).
# Вес кисти между ладонью и пальцами делится ниже, по расстоянию до костей одного пальца.
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

body.parent = arm_obj
arm_mod = body.modifiers.new('Armature', 'ARMATURE')
arm_mod.object = arm_obj

groups = {g.index: g.name for g in body.vertex_groups}
by_name = {g.name: g for g in body.vertex_groups}


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


def chain_weights(p, chain):
    """Мягкие веса среди костей одной цепочки: две ближние кости, на суставе пополам."""
    scored = sorted(((dist_point_segment(p, *segments[n]) + 0.004) ** -3, n) for n in chain)[-2:]
    total = sum(w for w, _ in scored)
    return {n: w / total for w, n in scored}


# Вес кисти делится между ладонью и одним пальцем: тем, к чьим костям вершина ближе всего.
# Большой палец растёт из ладони, его вес считается по расстоянию до всей его цепочки.
# Вершина ладони до костяшек остаётся за кистью. Первая фаланга забирает у неё вес только
# в последних PALM_BLEND метрах перед костяшками, у самих костяшек - половину.
PALM_BLEND = 0.008
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
        along = abs(v.co.x - CX)  # расстояние от середины тела вдоль руки
        if finger == 'Thumb':
            weights = chain_weights(v.co, [hand] + chains[finger])
        elif along >= FINGER_START[finger]:
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

# Проверка: у какой кости сколько вершин главные, и где они лежат (лицом в -Y, левая сторона на +X)
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
    print(f'[Rig] bone {name:14s} verts={len(co):5d} x[{lo.x:+.3f},{hi.x:+.3f}] '
          f'y[{lo.y:+.3f},{hi.y:+.3f}] z[{lo.z:+.3f},{hi.z:+.3f}]')

# ----------------------------------------------------------------------------------------------
# Экспорт (оси как в create_survivor.py, текстуры внутри FBX, как в исходном файле)
# ----------------------------------------------------------------------------------------------
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
    path_mode='COPY',
    embed_textures=True,
)
print(f'[Rig] exported {out}')

if args.blend:
    blend = os.path.abspath(args.blend)
    os.makedirs(os.path.dirname(blend), exist_ok=True)
    bpy.context.preferences.filepaths.save_version = 0
    bpy.ops.wm.save_as_mainfile(filepath=blend, compress=True)
    print(f'[Rig] saved {blend}')
