"""
Надевает меш штанов на тело Assets/Models/20261121003938_8b8745d5.fbx. Первые - тканевые штаны ClothPantsRope
с верёвочным поясом.

Запуск (из корня проекта):
    "E:\\Software\\Blender\\blender.exe" --background --factory-startup --python Tools/Blender/place_pants.py -- ^
        --src "<FBX штанов>" --out Assets/Models/Gear/ClothPantsRope.fbx [--png "<папка>"]

Источник - штаны, вырезанные пользователем в 3ds Max из модели генератора персонажа в штанах
(20261121192122_1a909266.fbx). Вершины в FBX - в координатах модели генератора (масштаб 2,54 и сдвиг объекта
от экспорта Max отбрасываются). Ставится сам меш штанов с его развёрткой и текстурами; вершины по одной
не двигаются (на нагруднике ClothChest это смяло ремни и шнуровку), меняются только срезы по высоте целиком:
  * у модели таз на 3 см впереди тела и на 1,8 см уже, ноги совпадают лучше. Каждый срез штанов сдвигается
    на центр среза тела (ниже шага - каждой ноги, выше - таза) и расширяется, если тело ближе GAP к ткани.
    Сдвиг и расширение плавно меняются по высоте, у шага ноги плавно переходят в таз;
  * верх пояса над краем нагрудника ClothChest прижимается внутрь нагрудника (на CHEST_CLEAR под его тканью),
    ниже края - плавно на RAMP: иначе пояс вылезает сквозь нагрудник;
  * низ штанин (ниже чаши голенища ClothBoots на 135 мм) сжимается к голени до ANKLE_GAP над кожей, выше чаши
    плавно возвращается к ширине модели: штанина заправлена в ботинок и не торчит сквозь голенище;
  * веса - с ближайшей кожи тела, SKIN_BONES кости на вершину;
  * под штанами прячется вся кожа таза и ног до края пояса: <out>_BodyMask.json. Треугольники тела на краю
    пояса, у которых часть вершин под нагрудником, тоже прячутся штанами (WornGear прячет треугольник,
    только если все три вершины в маске одной части);
  * текстуры модели уменьшаются до TEXTURE_SIZE, гладкость - из шероховатости, металла у модели нет.
С --png рисует модель и штаны на теле (спереди, сзади, сбоку) в T-позе и в позах игры.
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

sys.dont_write_bytecode = True  # без __pycache__ в Tools/Blender
sys.path.append(os.path.dirname(os.path.abspath(__file__)))
from gear_poses import RECORDED, SKIN_BONES, limit_bones, set_pose  # noqa: E402

BODY_PATH = 'Assets/Models/20261121003938_8b8745d5.fbx'
CHEST_PATH = 'Assets/Models/Gear/ClothChest.fbx'
BOOTS_PATH = 'Assets/Models/Gear/ClothBoots.fbx'
ZONE = {'Hips', 'Spine', 'LeftUpperLeg', 'RightUpperLeg', 'LeftLowerLeg', 'RightLowerLeg'}
GAP = 0.003            # ткань не ближе к коже, м (рост тела 1,088; в Unity умножается на 1,662)
CROTCH = (0.46, 0.52)  # ниже - срезы по ногам, выше - по тазу, между - плавный переход
Z_RANGE = (0.12, 0.62)  # срезы модели выше и ниже рваные (край пояса, завязки у щиколотки): там - крайние значения
CHEST_CLEAR = 0.0015   # верх пояса под тканью нагрудника на столько
RAMP = 0.02            # ниже края нагрудника пояс возвращается к своей ширине на этой высоте
CAP_Z = 0.135          # чаша голенища ClothBoots
ANKLE_GAP = 0.002      # низ штанин в голенище - над кожей на столько (голенище от голени не ближе 4 мм)
ANKLE_TOP = 0.165      # выше этой высоты штанина своей ширины
TEXTURE_SIZE = 2048    # штаны на экране крупнее ботинок и перчаток
STEP = 0.01            # шаг срезов по высоте
LEG_REACH = 0.085      # луч от центра ноги дальше этого уходит к другой ноге
SECTORS = 24           # расширение среза задаётся по секторам угла
MAX_WIDEN = 1.25       # больше срез не растягивается
SMOOTH = 1.5           # сглаживание сдвига и расширения по высоте, шагов

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
parser = argparse.ArgumentParser()
parser.add_argument('--src', required=True)
parser.add_argument('--out', required=True)
parser.add_argument('--png', default='')
args = parser.parse_args(argv)
src, out = os.path.abspath(args.src), os.path.abspath(args.out)
stem = os.path.splitext(out)[0]
name = os.path.basename(stem)

bpy.ops.wm.read_factory_settings(use_empty=True)


def import_new(path):
    before = set(bpy.data.objects)
    bpy.ops.import_scene.fbx(filepath=os.path.abspath(path))
    return [o for o in bpy.data.objects if o not in before]


def smoothstep(e0, e1, x):
    t = np.clip((np.asarray(x, float) - e0) / (e1 - e0), 0.0, 1.0)
    return t * t * (3 - 2 * t)


# ----------------------------------------------------------------------------------------------
# Тело, нагрудник, ботинки и штаны
# ----------------------------------------------------------------------------------------------
import_new(BODY_PATH)
arm_obj = bpy.data.objects['Armature']
body = bpy.data.objects['Body']
limit_bones(body)
set_pose(arm_obj, 'tpose')
body_rest = np.array([tuple(body.matrix_world @ v.co) for v in body.data.vertices])
group_names = {g.index: g.name for g in body.vertex_groups}
main_bone = [group_names[max(v.groups, key=lambda g: g.weight).group] if v.groups else '' for v in body.data.vertices]
body_edges = np.array([tuple(e.vertices) for e in body.data.edges])


def worn(path):
    """Надетая часть в T-позе на скелете тела (свой скелет из FBX удаляется)."""
    new = import_new(path)
    mesh = next(o for o in new if o.type == 'MESH')
    mw = mesh.matrix_world.copy()
    mesh.parent = arm_obj
    mesh.matrix_world = mw
    mesh.modifiers[0].object = arm_obj
    for o in new:
        if o.type == 'ARMATURE':
            bpy.data.objects.remove(o)
    return mesh


def world_bm(o):
    bm = bmesh.new()
    bm.from_object(o, bpy.context.evaluated_depsgraph_get())
    bm.transform(o.matrix_world)
    bm.normal_update()
    return bm


chest = worn(CHEST_PATH)
boots = worn(BOOTS_PATH)
cbm = world_bm(chest)
chest_bvh = BVHTree.FromBMesh(cbm)
chest_bottom = np.array([tuple(v.co) for e in cbm.edges if len(e.link_faces) == 1 for v in e.verts if v.co.z < 0.66])
cbm.free()
with open(os.path.splitext(os.path.abspath(CHEST_PATH))[0] + '_BodyMask.json', encoding='utf-8') as f:
    flat = json.load(f)['hidden']
chest_pts = np.array(flat).reshape(-1, 3)

# Вершины штанов - в координатах модели (Y вверх) -> Z вверх Blender, как у тела
pieces = [o for o in import_new(src) if o.type == 'MESH']
for o in [o for o in bpy.data.objects if o.type == 'EMPTY']:
    bpy.data.objects.remove(o)
for o in pieces:
    o.parent = None
    o.data.transform(Matrix.Rotation(math.radians(90), 4, 'X'))
    o.matrix_world = Matrix.Identity(4)
main_piece = next(o for o in pieces if o.name == 'part_000')
main_edges = np.array([tuple(e.vertices) for e in main_piece.data.edges])
main_co = np.array([tuple(v.co) for v in main_piece.data.vertices])
bpy.ops.object.select_all(action='DESELECT')
for o in pieces:
    o.select_set(True)
bpy.context.view_layer.objects.active = main_piece
bpy.ops.object.join()
pants = main_piece
pants.name = name
me = pants.data
gen_view = pants.copy()  # модель как есть - для снимков рядом
gen_view.data = me.copy()
bpy.context.scene.collection.objects.link(gen_view)
src_co = np.array([tuple(v.co) for v in me.vertices])
print(f'[Pants] generator: {len(pieces)} pieces, verts {len(src_co)}')


def cut(V, E, z):
    """Точки пересечения рёбер с плоскостью на высоте z."""
    a, b = V[E[:, 0]], V[E[:, 1]]
    m = (a[:, 2] - z) * (b[:, 2] - z) < 0
    t = ((z - a[m, 2]) / (b[m, 2] - a[m, 2]))[:, None]
    return a[m] + (b[m] - a[m]) * t


GROUPS = {'L': lambda x: x > 0, 'R': lambda x: x < 0, 'T': lambda x: np.ones_like(x, bool)}
levels = np.arange(0.06, 0.70 + 1e-6, STEP)



def mid(P):
    return (P[:, :2].min(0) + P[:, :2].max(0)) / 2


def table(values):
    """Значения по уровням levels (None - нет данных) -> заполнены крайними и сглажены по высоте."""
    vals = np.array([np.nan if v is None else v for v in values], float)
    good = ~np.isnan(vals)
    vals = np.interp(np.arange(len(vals)), np.flatnonzero(good), vals[good])
    k = np.arange(-4, 5)
    w = np.exp(-0.5 * (k / SMOOTH) ** 2)
    pad = np.pad(vals, 4, mode='edge')
    return np.convolve(pad, w / w.sum(), mode='valid')


# ----------------------------------------------------------------------------------------------
# Сдвиг срезов на центр тела
# ----------------------------------------------------------------------------------------------
center_b, shift = {}, {}
for g, pick in GROUPS.items():
    cb, sx, sy = [], [], []
    for z in levels:
        use = (z >= CROTCH[0]) if g == 'T' else (z <= CROTCH[1])
        zz = min(max(z, Z_RANGE[0]), Z_RANGE[1])
        b = cut(body_rest, body_edges, z)
        p = cut(main_co, main_edges, zz)
        b, p = b[pick(b[:, 0])], p[pick(p[:, 0])]
        if not use or len(b) < 4 or len(p) < 4:
            cb.append(None), sx.append(None), sy.append(None)
            continue
        mb, mp = mid(b), mid(p)
        cb.append(mb)
        sx.append(mb[0] - mp[0])
        sy.append(mb[1] - mp[1])
    cbx = table([c[0] if c is not None else None for c in cb])
    cby = table([c[1] if c is not None else None for c in cb])
    center_b[g] = (cbx, cby)
    shift[g] = (table(sx), table(sy))
    marks = [i for i, z in enumerate(levels) if round(z * 100) % 10 == 0]
    print(f'[Pants] {g} shift (mm) by height: ' +
          ', '.join(f'{levels[i]:.1f}:({shift[g][0][i] * 1000:+.0f} {shift[g][1][i] * 1000:+.0f})' for i in marks))


def at(arr, z):
    return np.interp(z, levels, arr)


def blend_weights(P):
    """Доли (правая нога, левая нога, таз) для точек P: ноги плавно по x у шага, таз плавно по высоте."""
    wt = smoothstep(CROTCH[0], CROTCH[1], P[:, 2])
    wl = smoothstep(-0.02, 0.02, P[:, 0])
    return (1 - wt) * (1 - wl), (1 - wt) * wl, wt


def transform(P, scale=None):
    out = np.zeros_like(P)
    for g, w in zip('RLT', blend_weights(P)):
        z = P[:, 2]
        q = P.copy()
        q[:, 0] += at(shift[g][0], z)
        q[:, 1] += at(shift[g][1], z)
        if scale is not None:
            c = np.stack([at(center_b[g][0], z), at(center_b[g][1], z)], 1)
            d = q[:, :2] - c
            s = lookup(scale[g], z, np.arctan2(d[:, 0], -d[:, 1]))[:, None]
            q[:, :2] = c + d * s
        out += q * w[:, None]
    return out


def lookup(tab, z, ang):
    """Расширение из таблицы (уровень по высоте, сектор по углу от -Y к +X): линейно по высоте и по кругу."""
    fi = np.clip((z - levels[0]) / STEP, 0, len(levels) - 1.001)
    i0 = fi.astype(int)
    ti = fi - i0
    fk = (ang + np.pi) / (2 * np.pi) * SECTORS - 0.5
    k0 = np.floor(fk).astype(int)
    tk = fk - k0
    k0, k1 = k0 % SECTORS, (k0 + 1) % SECTORS
    row = lambda i: tab[i, k0] * (1 - tk) + tab[i, k1] * tk
    return row(i0) * (1 - ti) + row(i0 + 1) * ti


def circular_smooth(row):
    k = np.arange(-2, 3)
    w = np.exp(-0.5 * k ** 2)
    w /= w.sum()
    return sum(np.roll(row, -j) * wj for j, wj in zip(k, w))


# ----------------------------------------------------------------------------------------------
# Расширение: где кожа ближе GAP к ткани, срез растягивается от центра в эту сторону. Таблица по высоте
# и по сектору угла, плавно по обоим: отдельные вершины не двигаются, только срез целиком. Одно расширение
# на весь срез раздуло бёдра в стороны: его задавали ягодицы по диагонали
# ----------------------------------------------------------------------------------------------
me.calc_loop_triangles()
tris = [tuple(t.vertices) for t in me.loop_triangles]
shifted = transform(src_co)
bvh = BVHTree.FromPolygons([Vector(p) for p in shifted], tris)


def outer_dist(c, d, reach):
    """Расстояние до внешней ткани по лучу от центра среза: последнее попадание. Первое бывает изнанкой узла
    верёвки или заклёпки внутри ткани."""
    last, start, left = None, c, reach
    while True:
        hit = bvh.ray_cast(start, d, left)
        if hit[0] is None:
            return last
        last = (hit[0] - c).length
        start = hit[0] + d * 1e-5
        left = reach - last


scale = {}
for g, pick in GROUPS.items():
    need = np.full((len(levels), SECTORS), np.nan)
    for i, z in enumerate(levels):
        use = (z >= CROTCH[0]) if g == 'T' else (z <= CROTCH[1])
        if not use or not (Z_RANGE[0] <= z <= Z_RANGE[1]):
            continue
        need[i] = 1.0
        b = cut(body_rest, body_edges, z)
        b = b[pick(b[:, 0])]
        c = Vector((center_b[g][0][i], center_b[g][1][i], z))
        for q in b:
            d = Vector((q[0] - c.x, q[1] - c.y, 0))
            if d.length < 1e-4:
                continue
            r = outer_dist(c, d.normalized(), 0.3 if g == 'T' else LEG_REACH)
            if r is None:
                continue
            k = int((math.atan2(d.x, -d.y) + math.pi) / (2 * math.pi) * SECTORS) % SECTORS
            need[i, k] = max(need[i, k], min((d.length + GAP) / r, MAX_WIDEN))
    rows = np.flatnonzero(~np.isnan(need[:, 0]))
    need = need[[rows[np.argmin(np.abs(rows - i))] for i in range(len(levels))]]  # без данных - ближайший срез
    # по кругу: не меньше соседних секторов, затем сглаживание; по высоте - так же
    need = np.array([circular_smooth(np.maximum(np.maximum(r, np.roll(r, 1)), np.roll(r, -1))) for r in need])
    need = np.array([need[max(0, i - 2):i + 3].max(0) for i in range(len(need))])
    scale[g] = np.stack([table(list(need[:, k])) for k in range(SECTORS)], 1)
    print(f'[Pants] {g} widen by height (min..max over sides): ' +
          ', '.join(f'{levels[i]:.1f}:{scale[g][i].min():.2f}..{scale[g][i].max():.2f}' for i in marks))
co = transform(src_co, scale)

# ----------------------------------------------------------------------------------------------
# Верх пояса - под нагрудником, низ штанин - в голенище
# ----------------------------------------------------------------------------------------------
cb_ang = np.arctan2(chest_bottom[:, 0], -chest_bottom[:, 1])


def chest_edge_z(a):
    """Нижний край нагрудника в стороне a (угол от -Y к +X): самая низкая из 12 точек края, ближайших по углу."""
    d = np.abs((cb_ang - a + np.pi) % (2 * np.pi) - np.pi)
    return chest_bottom[np.argsort(d)[:12], 2].min()


body_bm = bmesh.new()
body_bm.from_object(body, bpy.context.evaluated_depsgraph_get())
body_bm.transform(body.matrix_world)
body_bvh = BVHTree.FromBMesh(body_bm)
body_bm.free()
tucked_waist = tucked_ankle = 0
for i in range(len(co)):
    p = co[i]
    wr, wl, wt = blend_weights(p[None])
    g = 'T' if wt[0] > 0.5 else ('L' if p[0] > 0 else 'R')
    c = Vector((at(center_b[g][0], p[2]), at(center_b[g][1], p[2]), p[2]))
    d = Vector((p[0] - c.x, p[1] - c.y, 0))
    r = d.length
    if r < 1e-5:
        continue
    d.normalize()
    if g == 'T' and p[2] > 0.5:
        edge = chest_edge_z(math.atan2(d.x, -d.y))
        b = float(smoothstep(edge - RAMP, edge, p[2]))
        hit = chest_bvh.ray_cast(c, d, 0.3)
        if b > 0 and hit[0] is not None and r > hit[3] - CHEST_CLEAR:
            r_new = r + (hit[3] - CHEST_CLEAR - r) * b
            co[i, :2] = np.array(c.xy) + np.array(d.xy) * r_new
            tucked_waist += 1
    if g != 'T' and p[2] < ANKLE_TOP:
        b = 1.0 - float(smoothstep(CAP_Z - 0.005, ANKLE_TOP, p[2]))
        hit = body_bvh.ray_cast(c, d, 0.2)
        if b > 0 and hit[0] is not None:
            r_new = r + (hit[3] + ANKLE_GAP - r) * b
            co[i, :2] = np.array(c.xy) + np.array(d.xy) * r_new
            tucked_ankle += 1
print(f'[Pants] waist tucked under the chest: {tucked_waist} verts, legs tucked into boots: {tucked_ankle} verts')
for i, v in enumerate(me.vertices):
    v.co = Vector(co[i])
me.update()
# нормали из FBX модели после сдвига срезов давали гранёный вид: гладкие нормали заново по новой форме
print(f'[Pants] custom normals from the model: {me.has_custom_normals}')
bpy.ops.object.select_all(action='DESELECT')
pants.select_set(True)
bpy.context.view_layer.objects.active = pants
if me.has_custom_normals:
    bpy.ops.mesh.customdata_custom_splitnormals_clear()
me.shade_smooth()

# ----------------------------------------------------------------------------------------------
# Веса с кожи
# ----------------------------------------------------------------------------------------------
for vg in list(pants.vertex_groups):
    pants.vertex_groups.remove(vg)
for g in body.vertex_groups:
    pants.vertex_groups.new(name=g.name)
transfer = pants.modifiers.new('WeightTransfer', 'DATA_TRANSFER')
transfer.object = body
transfer.use_vert_data = True
transfer.data_types_verts = {'VGROUP_WEIGHTS'}
transfer.vert_mapping = 'POLYINTERP_NEAREST'
transfer.layers_vgroup_select_src = 'ALL'
transfer.layers_vgroup_select_dst = 'NAME'
bpy.ops.object.select_all(action='DESELECT')
pants.select_set(True)
bpy.context.view_layer.objects.active = pants
bpy.ops.object.modifier_apply(modifier=transfer.name)
# группы без весов убираются целиком: иначе у вершины остаётся лишняя кость (Docs/Gear.md, «Веса после переноса»)
limit_bones(pants)
bad = [v.index for v in me.vertices if not v.groups]
if bad or max(len(v.groups) for v in me.vertices) > SKIN_BONES:
    raise RuntimeError(f'{name}: bad weights ({len(bad)} vertices without bones)')
used = {pants.vertex_groups[g.group].name for v in me.vertices for g in v.groups if g.weight > 0}
print(f'[Pants] bones: {sorted(used)}')
pants.parent = arm_obj
pants.modifiers.new('Armature', 'ARMATURE').object = arm_obj

# ----------------------------------------------------------------------------------------------
# Зона тела под штанами: кожа таза и ног ниже края пояса
# ----------------------------------------------------------------------------------------------
pbm = bmesh.new()
pbm.from_mesh(me)
top = np.array([tuple(v.co) for e in pbm.edges if len(e.link_faces) == 1 for v in e.verts
                if v.co.z > 0.4 and v.index < len(main_co)])
pbm.free()
top_ang = np.arctan2(top[:, 0], -(top[:, 1] - top[:, 1].mean()))
order = np.argsort(top_ang)
top_ang, top_z = top_ang[order], top[order, 2]
top_ang = np.r_[top_ang - 2 * np.pi, top_ang, top_ang + 2 * np.pi]
top_z = np.r_[top_z, top_z, top_z]


def top_at(p):
    return float(np.interp(math.atan2(p[0], -(p[1] - top[:, 1].mean())), top_ang, top_z))


under = {i for i, p in enumerate(body_rest) if main_bone[i] in ZONE and p[2] < top_at(p)}
chest_hidden = set()
for p in chest_pts:
    i = int(np.argmin(((body_rest - p) ** 2).sum(1)))
    chest_hidden.add(i)
# треугольники на краю пояса: часть вершин под штанами, остальные под нагрудником - прячет штаны
body.data.calc_loop_triangles()
edge_extra = set()
for lt in body.data.loop_triangles:
    vs = set(lt.vertices)
    if vs & under and vs <= (under | chest_hidden):
        edge_extra |= vs - under
hidden = sorted(under | edge_extra)
hid = set(hidden)
# голая кожа: треугольники ниже края нагрудника, которые не прячет ни одна часть (выше края кожа под нагрудником,
# там она была и до штанов)
boots_hidden = set()
with open(os.path.splitext(os.path.abspath(BOOTS_PATH))[0] + '_BodyMask.json', encoding='utf-8') as f:
    for p in np.array(json.load(f)['hidden']).reshape(-1, 3):
        boots_hidden.add(int(np.argmin(((body_rest - p) ** 2).sum(1))))
bare = 0
for lt in body.data.loop_triangles:
    vs = set(lt.vertices)
    low = all(body_rest[i][2] < chest_edge_z(math.atan2(body_rest[i][0], -body_rest[i][1])) for i in vs)
    if low and not (vs <= hid or vs <= chest_hidden or vs <= boots_hidden) and \
            all(main_bone[i] in ZONE | {'LeftFoot', 'RightFoot', 'LeftToes', 'RightToes'} for i in vs):
        bare += 1
print(f'[Pants] hidden body vertices {len(hidden)} (of them on the waist edge {len(edge_extra)}); '
      f'body triangles below the chest edge not hidden by any part: {bare}')

# ----------------------------------------------------------------------------------------------
# Снимки
# ----------------------------------------------------------------------------------------------
if args.png:
    scene = bpy.context.scene
    scene.render.engine = 'BLENDER_WORKBENCH'
    scene.display.shading.light = 'STUDIO'
    scene.display.shading.color_type = 'TEXTURE'
    scene.render.resolution_x, scene.render.resolution_y = 600, 900
    world = bpy.data.worlds.new('PreviewWorld')
    world.color = (0.20, 0.23, 0.26)
    scene.world = world
    cam = bpy.data.objects.new('Cam', bpy.data.cameras.new('Cam'))
    scene.collection.objects.link(cam)
    scene.camera = cam
    cam.data.type = 'ORTHO'
    cam.data.clip_start = 0.001
    os.makedirs(args.png, exist_ok=True)
    VIEWS = {'front': (0, -1, 0), 'back': (0, 1, 0), 'left': (1, 0, 0), 'persp_back': (-0.5, 0.75, 0.45),
             'below_back': (0.2, 0.8, -0.35)}

    def render(prefix, center, size, views=('front', 'back', 'left')):
        cam.data.ortho_scale = size
        for view in views:
            d = Vector(VIEWS[view]).normalized()
            cam.location = center + d * 2
            cam.rotation_euler = (-d).to_track_quat('-Z', 'Y').to_euler()
            scene.render.filepath = os.path.join(args.png, f'{prefix}_{view}.png')
            bpy.ops.render.render(write_still=True)

    def show(*objs):
        for o in bpy.data.objects:
            if o.type == 'MESH':
                o.hide_render = o not in objs

    show(gen_view)
    render('gen', Vector((0, 0, 0.37)), 0.75)
    bm2 = bmesh.new()
    bm2.from_mesh(body.data)
    bm2.verts.ensure_lookup_table()
    bmesh.ops.delete(bm2, geom=[f for f in bm2.faces if all(v.index in hid for v in f.verts) or
                                all(v.index in chest_hidden for v in f.verts) or
                                all(v.index in boots_hidden for v in f.verts)], context='FACES_ONLY')
    bm2.to_mesh(body.data)
    bm2.free()
    skin_mat = bpy.data.materials.new('Skin')
    skin_mat.diffuse_color = (0.85, 0.62, 0.52, 1)
    body.data.materials.clear()
    body.data.materials.append(skin_mat)
    show(body, pants, chest, boots)
    render('tpose', Vector((0, 0, 0.42)), 0.9, ('front', 'back', 'left', 'persp_back', 'below_back'))
    render('waist', Vector((0, 0, 0.6)), 0.3, ('front', 'back', 'left', 'below_back'))
    render('ankle', Vector((0.065, 0.03, 0.14)), 0.16, ('front', 'back', 'left'))
    for pose in [p for p in RECORDED if p.split(':')[1].split('@')[0] in ('walk', 'run', 'jump', 'aim_strafe')][::6]:
        set_pose(arm_obj, pose)
        hips = arm_obj.pose.bones['Hips'].head
        render(pose.replace(':', '_').replace('@', '_'), Vector((hips.x, hips.y, 0.4)), 0.95, ('left', 'persp_back'))
    set_pose(arm_obj, 'tpose')

# ----------------------------------------------------------------------------------------------
# Маска, текстуры, экспорт
# ----------------------------------------------------------------------------------------------
os.makedirs(os.path.dirname(out), exist_ok=True)
with open(stem + '_BodyMask.json', 'w', encoding='utf-8') as f:
    json.dump({'part': 'pants', 'bodyVertices': len(body_rest),
               'hidden': [round(float(c), 6) for i in hidden for c in body_rest[i]]}, f)
print(f'[Pants] mask {stem}_BodyMask.json')

images = {n.image.name: n.image for n in me.materials[0].node_tree.nodes if n.type == 'TEX_IMAGE' and n.image}
base = next(i for n, i in images.items() if not any(k in n for k in ('normal', 'roughness', 'metallic')))
normal = next(i for n, i in images.items() if 'normal' in n)
roughness = next(i for n, i in images.items() if 'roughness' in n)
tex_dir = stem + '_Textures'
os.makedirs(tex_dir, exist_ok=True)


def pixels(img):
    img = img.copy()
    img.scale(TEXTURE_SIZE, TEXTURE_SIZE)
    a = np.empty(TEXTURE_SIZE * TEXTURE_SIZE * 4, np.float32)
    img.pixels.foreach_get(a)
    bpy.data.images.remove(img)
    return a.reshape(-1, 4)


def save(a, file_name, colorspace):
    img = bpy.data.images.new(file_name, TEXTURE_SIZE, TEXTURE_SIZE, alpha=True)
    img.colorspace_settings.name = colorspace
    img.pixels.foreach_set(a.ravel())
    img.filepath_raw = os.path.join(tex_dir, file_name)
    img.file_format = 'PNG'
    img.save()
    print(f'[Pants] texture {img.filepath_raw}')


save(pixels(base), f'{name}.png', 'sRGB')
save(pixels(normal), f'{name}_normal.png', 'Non-Color')
ms = np.zeros((TEXTURE_SIZE * TEXTURE_SIZE, 4), np.float32)
ms[:, 3] = 1.0 - pixels(roughness)[:, 0]
save(ms, f'{name}_metallic_smoothness.png', 'Non-Color')

for o in (gen_view, body, chest, boots):
    bpy.data.objects.remove(o)
pants.matrix_world = Matrix.Identity(4)
me.materials[0].name = 'Material'
for m in bpy.data.materials:
    m.use_nodes = False
bpy.ops.object.select_all(action='DESELECT')
arm_obj.select_set(True)
pants.select_set(True)
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
print(f'[Pants] exported {out}')
