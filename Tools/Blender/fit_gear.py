"""
Надевает часть снаряжения из генератора на базовое тело Assets/Models/20261121003938_8b8745d5.fbx.

Запуск (из корня проекта):
    "E:\\Software\\Blender\\blender.exe" --background --factory-startup --python Tools/Blender/fit_gear.py -- ^
        --part chest --src "<FBX части из генератора>" --out Assets/Models/Gear/ClothChest.fbx [--blend "<файл .blend>"]

Что делает:
  * импортирует тело со скелетом и часть, сводит куски части в один меш (поворот из FBX запекается в вершины);
  * ставит часть по размеру и положению на тело: масштаб и точка привязки из PARTS ниже, сняты замерами
    сечений тела и части;
  * прижимает к торсу слой части у тела (не дальше max_gap), а вершины, ушедшие внутрь тела или ближе GAP
    к коже, выталкивает наружу по нормали тела. Соседние вершины (в пределах PUSH_RADIUS по расстоянию,
    а не по рёбрам) сдвигаются на ту же величину с плавным спадом: стёжка и заплаты не сглаживаются;
  * убирает изнанку: часть из генератора - толстая оболочка, и к телу прилегала бы внутренняя сторона.
    Изнанка остаётся только там, где её видно снаружи (у горловины, пройм, низа и боковой шнуровки),
    остальная удаляется, и к телу прижимается лицевая сторона;
  * переносит веса: каждой вершине - веса ближайшего места на поверхности тела, затем веса усредняются
    по соседям в пределах WEIGHT_RADIUS. Остаются SKIN_BONES (2) кости на вершину, как в игре;
  * выталкивает часть в позах: в заданных вручную (руки вверх, присед) и во всех позах из игры
    (gear_poses.json, пишет Tools/Unity/ProbeGear.cs) кожа не должна выходить сквозь часть. Рука, зашедшая
    в часть, прижимает её к торсу, но не там, где часть надо выталкивать;
  * считает зону тела под частью: вершина тела прячется, если во всех позах лучи от неё наружу (по нормали
    и под наклоном) упираются в часть, и её главная кость из hide_bones. Затем зона сужается на кольцо
    вершин, чтобы в горловине и проймах не было видно дыры. Положения спрятанных вершин пишутся
    в <out>_BodyMask.json (координаты Blender), Unity (Tools/Unity/SetupGear.cs) находит по ним вершины тела;
  * сохраняет текстуры части в <out>_Textures: цвет, нормали и металл с гладкостью в одной картинке
    (металл в красном, гладкость в альфе, как ждёт URP Lit), размер TEXTURE_SIZE;
  * экспортирует FBX со скелетом тела и тем же поворотом осей, что rig_model_8b8745d5.py.
Координаты как в rig_model_8b8745d5.py: лицом в -Y, левая сторона персонажа на +X, рост тела 1,088.
"""
import argparse
import json
import math
import os
import sys

import bmesh
import bpy
import numpy as np
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree
from mathutils.kdtree import KDTree

sys.dont_write_bytecode = True  # без __pycache__ в Tools/Blender
sys.path.append(os.path.dirname(os.path.abspath(__file__)))
from gear_poses import SKIN_BONES, all_poses, limit_bones, set_pose  # noqa: E402

BODY_PATH = 'Assets/Models/20261121003938_8b8745d5.fbx'

# Подгонка частей. Точка src части (координаты после импорта) встаёт в точку dst на теле, масштаб scale по осям.
# chest: верх лямок части на z 0,916, середина по глубине y 0,024 (замер part_000). Плечи тела сверху на z ~0,91
# у x 0,08, торс под мышками шириной 0,2, глубиной 0,17. Масштаб 0,41: боковая стенка под проймой (x 0,28)
# встаёт на x 0,115 - под мышку, низ (z 0,009) на z 0,55 - на верх бёдер, где кончается пояс штанов.
PARTS = {
    'chest': dict(src=(0.0, 0.024, 0.916), dst=(0.0, 0.006, 0.925), scale=(0.38, 0.38, 0.36), max_gap=0.006,
                  hide_bones={'Hips', 'Spine', 'Chest', 'LeftShoulder', 'RightShoulder'},
                  reassign={'LeftUpperArm': ('LeftShoulder', 0.0), 'RightUpperArm': ('RightShoulder', 0.0),
                            'Neck': ('Chest', 0.0), 'Head': ('Chest', 0.0),
                            # верёвки у низа берут вес бедра и ездят за ногой: нагрудник держит только таз
                            'LeftUpperLeg': ('Hips', 0.0), 'RightUpperLeg': ('Hips', 0.0)}),
}
GAP = 0.004            # зазор от кожи, м (рост тела 1,088; в Unity умножается на 1,662)
POSE_GAP = 0.002       # зазор от кожи в позах из gear_poses.py
LIMB_GAP = 0.003       # зазор от руки или ноги в позах
RECORDED_STEP = 1      # подгонка берёт все позы игры: в стрейфе кожа лезет в одном кадре из трёх
PUSH_RADIUS = 0.025    # радиус, на который сдвиг наружу передаётся соседям
PULL_RADIUS = 0.04     # то же для подтягивания к торсу: шире, чтобы ткань не шла волнами
WEIGHT_RADIUS = 0.02   # радиус усреднения весов
THICKNESS = 0.03       # толщина оболочки части с набивкой: дальше от изнанки лицевая сторона не ищется
VIEW_RAYS = 64         # лучей на проверку, видна ли изнанка сквозь отверстие (в полусферу грани идёт половина)
VIEW_RAY = 0.3         # луч длиннее этого ушёл наружу
VIEW_MIN_COS = 0.42    # лучи почти вдоль грани не в счёт: они уходят в щель у кожи, сквозь которую не заглянуть
VIEW_MIN_Z = -0.5      # камера игрока не опускается ниже 30° под горизонт (ThirdPersonController.BottomClamp -30)
COVER_RAY = 0.05       # дальше этого часть не считается накрывающей тело
COVER_TILT = 35.0      # наклон боковых лучей от нормали, градусы
TEXTURE_SIZE = 2048

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
parser = argparse.ArgumentParser()
parser.add_argument('--part', required=True, choices=sorted(PARTS))
parser.add_argument('--src', required=True)
parser.add_argument('--out', required=True)
parser.add_argument('--blend', default='')
args = parser.parse_args(argv)
cfg = PARTS[args.part]
src, out = os.path.abspath(args.src), os.path.abspath(args.out)
stem = os.path.splitext(out)[0]
name = os.path.basename(stem)

bpy.ops.wm.read_factory_settings(use_empty=True)

# ----------------------------------------------------------------------------------------------
# Тело и часть
# ----------------------------------------------------------------------------------------------
bpy.ops.import_scene.fbx(filepath=os.path.abspath(BODY_PATH))
arm_obj = bpy.data.objects['Armature']
body = bpy.data.objects['Body']
limit_bones(body)
# Импорт FBX ставит кости в позу из узлов файла, она на миллиметры расходится с позой покоя скелета.
# Unity считает кожу от позы покоя (bindpose), поэтому подгонка идёт в ней же.
for pb in arm_obj.pose.bones:
    pb.matrix_basis = Matrix.Identity(4)
bpy.context.view_layer.update()
before = set(bpy.data.objects)
bpy.ops.import_scene.fbx(filepath=src)
pieces = [o for o in bpy.data.objects if o not in before and o.type == 'MESH']
for o in [o for o in bpy.data.objects if o not in before and o.type != 'MESH']:
    bpy.data.objects.remove(o)
for o in pieces:
    o.data.transform(o.matrix_world)
    o.matrix_world = Matrix.Identity(4)
bpy.ops.object.select_all(action='DESELECT')
for o in pieces:
    o.select_set(True)
gear = next(o for o in pieces if o.name.startswith('part_000'))
bpy.context.view_layer.objects.active = gear
bpy.ops.object.join()
gear.name = gear.data.name = name
me = gear.data
if len(me.uv_layers) != 1:
    raise RuntimeError(f'{name}: expected one UV layer after join, got {[uv.name for uv in me.uv_layers]}')
print(f'[Fit] {name}: {len(pieces)} pieces, verts={len(me.vertices)} polys={len(me.polygons)}')

s = Vector(cfg['scale'])
src_anchor, dst_anchor = Vector(cfg['src']), Vector(cfg['dst'])
for v in me.vertices:
    v.co = (v.co - src_anchor) * s + dst_anchor
me.update()

deps = bpy.context.evaluated_depsgraph_get()
body_bm = bmesh.new()
body_bm.from_object(body, deps)
body_bm.transform(body.matrix_world)
body_bm.normal_update()
body_bvh = BVHTree.FromBMesh(body_bm)


def depth_below(p):
    """Насколько точка p ниже зазора GAP над кожей (>0 - надо выталкивать) и нормаль кожи."""
    loc, normal, _, _ = body_bvh.find_nearest(p, 0.05)
    if loc is None:
        return 0.0, None
    return GAP - (p - loc).dot(normal), normal


def report(stage):
    inside = sum(1 for v in me.vertices if depth_below(v.co)[0] > GAP)
    close = sum(1 for v in me.vertices if depth_below(v.co)[0] > 1e-5)
    zs = [v.co.z for v in me.vertices]
    print(f'[Fit] {stage}: inside body {inside}/{len(me.vertices)}, closer than gap {close}, z[{min(zs):.3f},{max(zs):.3f}]')


report('placed')

# Торс под частью - только грани с главной костью из hide_bones: к руке или шее часть не подтягивается
groups = {g.index: g.name for g in body.vertex_groups}
body_verts = body.data.vertices


def main_bone(i):
    gs = body_verts[i].groups
    return groups[max(gs, key=lambda g: g.weight).group] if gs else ''


torso_bm = body_bm.copy()
torso_bm.verts.ensure_lookup_table()
bmesh.ops.delete(torso_bm, geom=[f for f in torso_bm.faces
                                 if not all(main_bone(v.index) in cfg['hide_bones'] for v in f.verts)],
                 context='FACES')
torso_bvh = BVHTree.FromBMesh(torso_bm)


# ----------------------------------------------------------------------------------------------
# Изнанка. Часть из генератора - толстая оболочка: у спины и груди есть лицевая сторона и изнанка. На теле изнанку
# не видно (кожа под частью спрятана), а из-за неё часть толще и дальше от тела. Изнанка - грань, которая смотрит
# к телу и за которой в пределах THICKNESS (наружу от тела) есть другая грань части. У краёв (горловина, проймы,
# низ, вырезы) изнанку видно сквозь отверстие, там она остаётся.
# ----------------------------------------------------------------------------------------------
golden = math.pi * (3.0 - math.sqrt(5.0))
sphere = [Vector((math.cos(golden * i) * math.sqrt(1 - z * z), math.sin(golden * i) * math.sqrt(1 - z * z), z))
          for i, z in ((i, 1 - 2 * (i + 0.5) / VIEW_RAYS) for i in range(VIEW_RAYS))]


def seen_from_outside(f, part_bvh):
    center = f.calc_center_median()
    for d in sphere:
        if d.dot(f.normal) < VIEW_MIN_COS or d.z < VIEW_MIN_Z:
            continue
        origin = center + d * 0.0005
        if part_bvh.ray_cast(origin, d, VIEW_RAY)[0] is None and body_bvh.ray_cast(origin, d, VIEW_RAY)[0] is None:
            return True
    return False


def remove_inner():
    part_bm = bmesh.new()
    part_bm.from_mesh(me)
    part_bm.normal_update()
    part_bvh = BVHTree.FromBMesh(part_bm)
    inner, facing = set(), {}
    for f in part_bm.faces:
        center = f.calc_center_median()
        loc, skin_normal, _, _ = torso_bvh.find_nearest(center, 0.08)
        facing[f] = f.normal.dot(skin_normal) if loc is not None else 1.0
        if facing[f] > -0.3:
            continue
        hit = part_bvh.ray_cast(center - f.normal * 0.0003, -f.normal, THICKNESS)
        if hit[0] is not None and hit[2] != f.index:
            inner.add(f)
    # Изнанку видно, если от неё есть путь наружу: хоть один луч в её полусферу уходит на VIEW_RAY, не задев тело
    # и часть. Такая изнанка (у горловины, пройм, низа, вырезов) остаётся вместе с соседними гранями
    seen = {f for f in inner if seen_from_outside(f, part_bvh)}
    keep = seen | {g for f in seen for e in f.edges for g in e.link_faces if g in inner}
    removed = [f for f in inner if f not in keep]
    bmesh.ops.delete(part_bm, geom=removed, context='FACES')
    part_bm.to_mesh(me)
    me.update()
    print(f'[Fit] inner side: {len(inner)} faces, removed {len(removed)}, kept at edges {len(inner) - len(removed)}; '
          f'verts={len(me.vertices)} polys={len(me.polygons)}')


def too_far(v, bvh):
    """Насколько слой части у тела дальше max_gap от торса (>0 - надо подтянуть) и нормаль кожи.
    Слой у тела - вершина, между которой и кожей нет других граней части: карманы, пряжки и заплаты поверх
    не подтягиваются сами (иначе сплющатся), а идут за слоем под ними."""
    loc, normal, _, dist = torso_bvh.find_nearest(v.co, 0.06)
    if loc is None or dist <= cfg['max_gap'] or (v.co - loc).dot(normal) < 0:
        return 0.0, None
    if bvh.ray_cast(v.co - normal * 0.001, -normal, dist)[0] is not None:
        return 0.0, None
    return dist - cfg['max_gap'], normal


# ----------------------------------------------------------------------------------------------
# Выталкивание наружу и подтягивание к торсу
# ----------------------------------------------------------------------------------------------
def move(pushes, radius):
    """Сдвигает вершины части на сдвиги pushes [(точка, сдвиг)], соседям в радиусе radius - с плавным спадом."""
    kd = KDTree(len(me.vertices))
    for v in me.vertices:
        kd.insert(v.co, v.index)
    kd.balance()
    num = [Vector() for _ in me.vertices]
    den = [0.0] * len(me.vertices)
    top = [0.0] * len(me.vertices)
    for co, disp in pushes:
        for _, i, dist in kd.find_range(co, radius):
            t = 1.0 - dist / radius
            w = t * t * (3.0 - 2.0 * t)
            num[i] += disp * w
            den[i] += w
            top[i] = max(top[i], w)
    for v in me.vertices:
        if den[v.index] > 0.0:
            v.co += num[v.index] / den[v.index] * top[v.index]
    me.update()


def pull_layer():
    """Подтягивает слой части у тела к торсу широким радиусом: форма части гнётся плавно."""
    for rnd in range(8):
        pulls = []
        layer_bm = bmesh.new()
        layer_bm.from_mesh(me)
        layer_bvh = BVHTree.FromBMesh(layer_bm)
        for v in me.vertices:
            d, normal = too_far(v, layer_bvh)
            if d > 1e-4:
                pulls.append((v.co.copy(), -normal * d * 0.6))
        if not pulls:
            break
        move(pulls, PULL_RADIUS)
        print(f'[Fit] pull round {rnd}: {len(pulls)} vertices farther than max_gap')


def push_rest():
    for rnd in range(16):
        pushes = []
        for v in me.vertices:
            d, normal = depth_below(v.co)
            if d > 1e-5:
                pushes.append((v.co.copy(), normal * (d + 0.0005)))
        if not pushes:
            break
        move(pushes, PUSH_RADIUS)
        print(f'[Fit] push round {rnd}: {len(pushes)} vertices below gap')


# Сначала часть прижимается изнанкой: пока она висит свободно, изнанку видно сквозь щель у тела и её не отличить
# от видимой. Потом невидимая изнанка убирается, и к телу прижимается лицевая сторона
pull_layer()
push_rest()
report('pushed')
remove_inner()
pull_layer()
push_rest()
report('pushed without inner side')

# ----------------------------------------------------------------------------------------------
# Веса с тела
# ----------------------------------------------------------------------------------------------
bone_names = [g.name for g in body.vertex_groups]
for n in bone_names:
    gear.vertex_groups.new(name=n)
transfer = gear.modifiers.new('WeightTransfer', 'DATA_TRANSFER')
transfer.object = body
transfer.use_vert_data = True
transfer.data_types_verts = {'VGROUP_WEIGHTS'}
transfer.vert_mapping = 'POLYINTERP_NEAREST'
transfer.layers_vgroup_select_src = 'ALL'
transfer.layers_vgroup_select_dst = 'NAME'
bpy.ops.object.select_all(action='DESELECT')
gear.select_set(True)
bpy.context.view_layer.objects.active = gear
bpy.ops.object.modifier_apply(modifier=transfer.name)

nv, ng = len(me.vertices), len(gear.vertex_groups)
W = np.zeros((nv, ng))
for v in me.vertices:
    for g in v.groups:
        W[v.index, g.group] = g.weight
# Кости, которые часть слушает слабо, отдают долю веса родителю (у нагрудника рука - ключице): с полным весом
# руки плечо нагрудника отгибается, когда рука опущена, а без него дельта проходит сквозь край проймы
gi = {g.name: g.index for g in gear.vertex_groups}
for bone, (parent, keep) in cfg.get('reassign', {}).items():
    W[:, gi[parent]] += W[:, gi[bone]] * (1.0 - keep)
    W[:, gi[bone]] *= keep
kd = KDTree(nv)
for v in me.vertices:
    kd.insert(v.co, v.index)
kd.balance()
neigh = []
for v in me.vertices:
    found = kd.find_range(v.co, WEIGHT_RADIUS)
    idx = np.array([i for _, i, _ in found])
    w = np.array([math.exp(-(d / (WEIGHT_RADIUS * 0.5)) ** 2) for _, _, d in found])
    neigh.append((idx, w / w.sum()))
for _ in range(2):
    W = np.array([W[idx].T @ w for idx, w in neigh])
# не больше SKIN_BONES костей на вершину, сумма 1
for i in range(nv):
    row = W[i]
    keep = np.argsort(row)[-SKIN_BONES:]
    mask = np.zeros(ng, bool)
    mask[keep] = True
    row[~mask] = 0.0
    total = row.sum()
    if total <= 0.0:
        raise RuntimeError(f'{name}: vertex {i} got no weights from body')
    W[i] = row / total
for gi, vg in enumerate(gear.vertex_groups):
    # веса от переноса стираются целиком: иначе у вершины остаётся третья кость, которой в игре нет
    vg.remove(list(range(nv)))
    used = np.nonzero(W[:, gi] > 1e-4)[0]
    for i in used:
        vg.add([int(i)], float(W[i, gi]), 'REPLACE')
main = {}
for i in range(nv):
    n = gear.vertex_groups[int(np.argmax(W[i]))].name
    main[n] = main.get(n, 0) + 1
print('[Fit] main bones: ' + ', '.join(f'{n} {c}' for n, c in sorted(main.items(), key=lambda x: -x[1])))

# ----------------------------------------------------------------------------------------------
# Выталкивание в позах: в позе со сгибом (рука опущена, присед) кожа уходит за часть. Сдвиг, нужный в позе,
# переводится в позу покоя обратным скиннингом вершины, и часть сдвигается в покое, как при выталкивании выше.
# ----------------------------------------------------------------------------------------------
arm_mod = gear.modifiers.new('Armature', 'ARMATURE')
arm_mod.object = arm_obj
A = arm_obj.matrix_world.to_3x3()
A_inv = A.inverted()
bone_of = {vg.index: vg.name for vg in gear.vertex_groups}


def posed(o):
    bm = bmesh.new()
    bm.from_object(o, bpy.context.evaluated_depsgraph_get())
    bm.transform(o.matrix_world)
    bm.normal_update()
    return bm


# Тело делится на то, от чего часть выталкивается (торс под частью, шея, голова), и на руки и ноги. Рука или бедро,
# зашедшие в часть, не выталкивают её наружу (иначе бок нагрудника обходит опущенную руку снаружи и рука
# оказывается внутри), а прижимают к торсу, но не ближе POSE_GAP.
push_bones = cfg['hide_bones'] | {'Neck', 'Head'}
body_bm.faces.ensure_lookup_table()
torso_faces = {f.index for f in body_bm.faces if all(main_bone(v.index) in push_bones for v in f.verts)}
limb_faces = {f.index for f in body_bm.faces if not any(main_bone(v.index) in push_bones for v in f.verts)}


def split(bm, keep):
    bm = bm.copy()
    bm.faces.ensure_lookup_table()
    bmesh.ops.delete(bm, geom=[f for f in bm.faces if f.index not in keep], context='FACES')
    return BVHTree.FromBMesh(bm)


for rnd in range(10):
    best = {}
    for pose in all_poses(RECORDED_STEP):
        if pose == 'tpose':
            continue
        set_pose(arm_obj, pose)
        body_posed = posed(body)
        torso_posed, limb_posed = split(body_posed, torso_faces), split(body_posed, limb_faces)
        skin = {pb.name: A @ (pb.matrix @ pb.bone.matrix_local.inverted()).to_3x3() @ A_inv
                for pb in arm_obj.pose.bones}
        gear_posed = posed(gear)
        gear_posed.verts.ensure_lookup_table()
        pushed = squeezed = 0
        for v in gear_posed.verts:
            loc, normal, _, _ = torso_posed.find_nearest(v.co, 0.08)
            if loc is None:
                continue
            above = (v.co - loc).dot(normal)
            if above < POSE_GAP - 1e-5:
                need = normal * (POSE_GAP - above + 0.0005)
                pushed += 1
            else:
                limb_loc, limb_normal, _, _ = limb_posed.find_nearest(v.co, 0.03)
                if limb_loc is None:
                    continue
                into = LIMB_GAP - (v.co - limb_loc).dot(limb_normal)
                amount = min(into + 0.0005, above - POSE_GAP)
                if into <= 1e-5 or amount <= 1e-5:
                    continue
                need = -normal * amount
                squeezed += 1
            L = sum((skin[bone_of[g.group]] * g.weight for g in me.vertices[v.index].groups), Matrix.Diagonal((0, 0, 0)))
            # У вершины на двух костях с сильным сгибом между ними (таз и бедро в приседе) смесь поворотов почти
            # вырождена, и обратный скиннинг даёт сдвиг в полметра: там сдвиг берётся как есть
            disp = L.inverted() @ need if L.determinant() > 0.3 else need.copy()
            if disp.length > 2.0 * need.length:
                disp *= 2.0 * need.length / disp.length
            # Выталкивание из торса важнее прижатия рукой: прижатие в одной позе не должно загонять вершину
            # под кожу в другой, иначе вершина скачет между позами и кожа проступает
            key = (need.dot(normal) > 0, disp.length)
            if v.index not in best or key > best[v.index][0]:
                best[v.index] = (key, disp)
        print(f'[Fit] pose round {rnd} {pose}: {pushed} below pose gap over torso, {squeezed} under arms or legs')
    set_pose(arm_obj, 'tpose')
    if not best:
        break
    # Сдвиг расходится на соседей в PUSH_RADIUS, поэтому прижатие рядом с вершиной, которую надо вытолкнуть,
    # тянет её обратно под кожу. Там прижатие отменяется
    near_push = KDTree(len(best))
    for i, (key, _) in best.items():
        if key[0]:
            near_push.insert(me.vertices[i].co, i)
    near_push.balance()
    best = {i: (key, d) for i, (key, d) in best.items()
            if key[0] or not near_push.find_range(me.vertices[i].co, PUSH_RADIUS)}
    move([(me.vertices[i].co.copy(), d) for i, (_, d) in best.items()], PUSH_RADIUS)
    push_rest()
report('pushed in poses')

# ----------------------------------------------------------------------------------------------
# Зона тела под частью
# ----------------------------------------------------------------------------------------------
def covered(bvh, p, n):
    t1 = n.orthogonal().normalized()
    t2 = n.cross(t1)
    tilt = math.radians(COVER_TILT)
    dirs = [n] + [(n * math.cos(tilt) + t * math.sin(tilt)).normalized() for t in (t1, -t1, t2, -t2)]
    return all(bvh.ray_cast(p + d * 0.0005, d, COVER_RAY)[0] is not None for d in dirs)


# Кожа прячется, только если часть накрывает её во всех позах: в позе кожа под лямкой или у горловины
# может выйти из-под части, и тогда на её месте видна дыра
cover = {i for i in range(len(body_bm.verts)) if main_bone(i) in cfg['hide_bones']}
for pose in all_poses():
    set_pose(arm_obj, pose)
    body_posed = posed(body)
    body_posed.verts.ensure_lookup_table()
    gear_bvh = BVHTree.FromBMesh(posed(gear))
    before_pose = len(cover)
    cover = {i for i in cover if covered(gear_bvh, body_posed.verts[i].co, body_posed.verts[i].normal)}
    if len(cover) < before_pose:
        print(f'[Fit] cover {pose}: {len(cover)} (-{before_pose - len(cover)})')
set_pose(arm_obj, 'tpose')
body_bm.verts.ensure_lookup_table()
hidden = {i for i in cover if all(e.other_vert(body_bm.verts[i]).index in cover for e in body_bm.verts[i].link_edges)}
print(f'[Fit] body: covered {len(cover)}, hidden after one ring shrink {len(hidden)} of {len(body_bm.verts)}')
os.makedirs(os.path.dirname(out), exist_ok=True)
with open(stem + '_BodyMask.json', 'w', encoding='utf-8') as f:
    json.dump({'part': args.part, 'bodyVertices': len(body_bm.verts),
               'hidden': [round(c, 6) for i in sorted(hidden) for c in body_bm.verts[i].co]}, f)
print(f'[Fit] mask {stem}_BodyMask.json')

# ----------------------------------------------------------------------------------------------
# Текстуры
# ----------------------------------------------------------------------------------------------
images = {}
for node in me.materials[0].node_tree.nodes:
    if node.type == 'TEX_IMAGE' and node.image:
        images[node.image.name] = node.image
base = next(i for n, i in images.items() if not any(k in n for k in ('normal', 'roughness', 'metallic')))
normal = next(i for n, i in images.items() if 'normal' in n)
metallic = next(i for n, i in images.items() if 'metallic' in n)
roughness = next(i for n, i in images.items() if 'roughness' in n)
tex_dir = stem + '_Textures'
os.makedirs(tex_dir, exist_ok=True)


def pixels(img):
    img.scale(TEXTURE_SIZE, TEXTURE_SIZE)
    a = np.empty(TEXTURE_SIZE * TEXTURE_SIZE * 4, np.float32)
    img.pixels.foreach_get(a)
    return a.reshape(-1, 4)


def save(a, file_name, colorspace):
    img = bpy.data.images.new(file_name, TEXTURE_SIZE, TEXTURE_SIZE, alpha=True)
    img.colorspace_settings.name = colorspace
    img.pixels.foreach_set(a.ravel())
    img.filepath_raw = os.path.join(tex_dir, file_name)
    img.file_format = 'PNG'
    img.save()
    print(f'[Fit] texture {img.filepath_raw}')


save(pixels(base), f'{name}.png', 'sRGB')
save(pixels(normal), f'{name}_normal.png', 'Non-Color')
ms = pixels(metallic)
ms[:, 3] = 1.0 - pixels(roughness)[:, 0]
save(ms, f'{name}_metallic_smoothness.png', 'Non-Color')

# ----------------------------------------------------------------------------------------------
# Экспорт: часть на скелете тела, без тела и без текстур (материал собирает Unity)
# ----------------------------------------------------------------------------------------------
gear.parent = arm_obj
gear.matrix_world = Matrix.Identity(4)
me.materials[0].name = 'Material'
for m in bpy.data.materials:
    m.use_nodes = False
bpy.ops.object.select_all(action='DESELECT')
arm_obj.select_set(True)
gear.select_set(True)
bpy.context.view_layer.objects.active = arm_obj
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
print(f'[Fit] exported {out}')

if args.blend:
    bpy.context.preferences.filepaths.save_version = 0
    bpy.ops.wm.save_as_mainfile(filepath=os.path.abspath(args.blend), compress=True)
    print(f'[Fit] saved {args.blend}')
