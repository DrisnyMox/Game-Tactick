"""
Надевает меш верха (туники с накидкой) на тело Assets/Models/20261121003938_8b8745d5.fbx. Первый - верх комплекта
Островных Кочевников NomadChest: туника из выгоревшего льна, бирюзовая накидка на плечах со светло-серой полосой,
серый шарф, серый пояс-обмотка с кожаными сумками, подол ниже пояса.

Запуск (из корня проекта):
    "E:\\Software\\Blender\\blender.exe" --background --factory-startup --python Tools/Blender/place_chest.py -- ^
        --src "<FBX верха>" --out Assets/Models/Gear/NomadChest.fbx [--gen "<FBX модели генератора>"] [--png "<папка>"]

Источник - верх, вырезанный пользователем в 3ds Max из модели генератора персонажа в этом верхе
(20261123011456_940bca6a.fbx): одна сетка (туника, накидка, шарф, пояс, подол) и мелкие куски (сумки, застёжки,
заклёпки, бирка). Вершины в FBX - в координатах модели генератора (масштаб 2,54 и сдвиг объекта от экспорта Max
отбрасываются, как в place_pants.py). У модели генератора ноги, шея и толщина рук совпадают с телом, а у тела
грудь и таз больше (грудь выходит за ткань до 3 см, ягодицы и штаны - до 2,7 см), руки в T-позе выше на 2,2 см.
Вершины по одной не двигаются (на нагруднике ClothChest это смяло ремни и шнуровку), форма меняется только
плавными полями, одинаковыми для ткани и деталей на ней:
  * накидка над плечами поднимается по ширине плеч (|x|): насколько плечо и рука тела выходят над ней. Подъём плавный
    по ширине плеч и по высоте (ниже LIFT_Z не поднимается ничего);
  * срез по высоте отодвигается от центра тела по секторам угла на столько, на сколько кожа, смотрящая вбок, или
    штаны ClothPantsRope ближе GAP к ткани. Отодвигается, а не растягивается: накидка и сумки снаружи туники уходят
    на то же расстояние, что и туника под ними, и не разлетаются (растяжение раздуло края накидки). Слой выше
    отодвигается не меньше нижнего (UP_REACH);
  * кожа, смотрящая вверх (верх груди у горловины, плечи), - под накидкой сверху: там накидка поднимается по месту
    (x, y). Отодвигание и подъём - по PASSES проходов, каждый добирает то, что срезало сглаживание;
  * нормали после этого считаются заново (гладкие), веса - с ближайшей кожи тела, SKIN_BONES кости на вершину,
    у накидки половина веса руки отдаётся ключице (ARM_SHARE);
  * под туникой прячется кожа торса, которую туника закрывает и которую не видно снаружи ни в одну щель
    (горловина, проймы): <out>_BodyMask.json;
  * проверка по позам из игры: штаны, закрытые туникой в покое, не выходят наружу;
  * текстуры модели уменьшаются до TEXTURE_SIZE, гладкость - из шероховатости, металла у модели нет.
С --png рисует модель генератора, верх как есть и верх на теле (спереди, сзади, сбоку, сверху, плечи и края
вблизи) в T-позе и в позах игры. Координаты как в rig_model_8b8745d5.py: лицом в -Y, левая сторона персонажа
на +X, рост тела 1,088.
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
PANTS_PATH = 'Assets/Models/Gear/ClothPantsRope.fbx'
OTHER_PARTS = ('Assets/Models/Gear/NomadBoots.fbx', 'Assets/Models/Gear/ClothGloves.fbx')  # только для снимков
TORSO = {'Hips', 'Spine', 'Chest', 'Neck', 'LeftShoulder', 'RightShoulder', 'LeftUpperLeg', 'RightUpperLeg'}
LIFT_BONES = {'LeftShoulder', 'RightShoulder', 'LeftUpperArm', 'RightUpperArm'}
HIDE_BONES = {'Hips', 'Spine', 'Chest', 'Neck', 'LeftShoulder', 'RightShoulder'}
GAP = 0.004            # ткань не ближе к коже, м (рост тела 1,088; в Unity умножается на 1,662)
PANTS_GAP = 0.003      # и к штанам: пояс штанов под туникой
STEP = 0.01            # шаг срезов по высоте
LEVELS = (0.53, 0.95)  # подол модели на 0,545, верх шарфа на 0,937
SHOULDER_Z = 0.84      # выше по срезам ставится только шарф на шею; плечи под накидкой - подъёмом
LIFT_Z = (0.70, 0.78)  # подъём накидки: ниже первой высоты нет, выше второй - полный
LIFT_BIN = 0.01        # шаг по ширине плеч
SECTORS = 24
MAX_PUSH = 0.05        # отодвигается не больше
UP_REACH = 4           # отодвигание тянется вверх на столько уровней
PASSES = 4             # проходов отодвигания и подъёма
SMOOTH = 2.0           # сглаживание по высоте, шагов
ARM_SHARE = 0.5        # доля веса руки у накидки, остальное - ключице
TEXTURE_SIZE = 2048

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
parser = argparse.ArgumentParser()
parser.add_argument('--src', required=True)
parser.add_argument('--out', required=True)
parser.add_argument('--gen', default='')
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


def world_bm(o):
    bm = bmesh.new()
    bm.from_object(o, bpy.context.evaluated_depsgraph_get())
    bm.transform(o.matrix_world)
    bm.normal_update()
    return bm


def evaluated(o):
    dg = bpy.context.evaluated_depsgraph_get()
    ev = o.evaluated_get(dg)
    m = ev.to_mesh()
    a = np.empty(len(m.vertices) * 3)
    m.vertices.foreach_get('co', a)
    ev.to_mesh_clear()
    a = a.reshape(-1, 3)
    mw = np.array(o.matrix_world)
    return a @ mw[:3, :3].T + mw[:3, 3]


# ----------------------------------------------------------------------------------------------
# Тело, штаны и верх
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
bbm = world_bm(body)
body_normal = np.array([tuple(v.normal) for v in bbm.verts])
body_bvh = BVHTree.FromBMesh(bbm)
bbm.free()


def worn(path):
    """Надетая часть в T-позе на скелете тела (свой скелет из FBX удаляется). Текстура цвета - для снимков."""
    new = import_new(path)
    mesh = next(o for o in new if o.type == 'MESH')
    mw = mesh.matrix_world.copy()
    mesh.parent = arm_obj
    mesh.matrix_world = mw
    mesh.modifiers[0].object = arm_obj
    for o in new:
        if o.type == 'ARMATURE':
            bpy.data.objects.remove(o)
    stem_ = os.path.splitext(os.path.abspath(path))[0]
    tex = os.path.join(stem_ + '_Textures', os.path.basename(stem_) + '.png')
    mat = bpy.data.materials.new(os.path.basename(stem_))
    mat.use_nodes = True
    node = mat.node_tree.nodes.new('ShaderNodeTexImage')
    node.image = bpy.data.images.load(tex)
    mat.node_tree.links.new(node.outputs['Color'], mat.node_tree.nodes['Principled BSDF'].inputs['Base Color'])
    mesh.data.materials.clear()
    mesh.data.materials.append(mat)
    return mesh


pants = worn(PANTS_PATH)
others = [worn(p) for p in OTHER_PARTS]
pants_co = np.array([tuple(pants.matrix_world @ v.co) for v in pants.data.vertices])
pants_edges = np.array([tuple(e.vertices) for e in pants.data.edges])

# Вершины верха - в координатах модели (Y вверх) -> Z вверх Blender, как у тела
pieces = [o for o in import_new(src) if o.type == 'MESH']
for o in [o for o in bpy.data.objects if o.type == 'EMPTY']:
    bpy.data.objects.remove(o)
for o in pieces:
    o.parent = None
    o.data.transform(Matrix.Rotation(math.radians(90), 4, 'X'))
    o.matrix_world = Matrix.Identity(4)
main_piece = next(o for o in pieces if o.name == 'part_000')
bpy.ops.object.select_all(action='DESELECT')
for o in pieces:
    o.select_set(True)
bpy.context.view_layer.objects.active = main_piece
bpy.ops.object.join()
top = main_piece
top.name = name
me = top.data
gen_view = top.copy()  # верх как есть - для снимков рядом
gen_view.data = me.copy()
bpy.context.scene.collection.objects.link(gen_view)
src_co = np.array([tuple(v.co) for v in me.vertices])
me.calc_loop_triangles()
tris = [tuple(t.vertices) for t in me.loop_triangles]
print(f'[Chest] generator: {len(pieces)} pieces, verts {len(src_co)}, z {src_co[:, 2].min():.3f}..{src_co[:, 2].max():.3f}')


def cloth_bvh(P):
    return BVHTree.FromPolygons([Vector(p) for p in P], tris)


gen_body = []
if args.gen:
    gen_body = [o for o in import_new(args.gen) if o.type == 'MESH']
    for o in gen_body:
        o.parent = None
        o.data.transform(Matrix.Rotation(math.radians(90), 4, 'X'))
        o.matrix_world = Matrix.Identity(4)


def gauss(vals, sigma, edge=True):
    k = np.arange(-6, 7)
    w = np.exp(-0.5 * (k / sigma) ** 2)
    pad = np.pad(vals, 6, mode='edge' if edge else 'constant')
    return np.convolve(pad, w / w.sum(), mode='valid')


def grow(vals, n):
    """Не меньше соседей на n шагов: после сглаживания пик не опускается ниже нужного."""
    return np.array([vals[max(0, i - n):i + n + 1].max() for i in range(len(vals))])


# ----------------------------------------------------------------------------------------------
# Подъём накидки над плечами. У тела руки в T-позе на 2,2 см выше, чем у модели генератора, и плечо выходит
# над накидкой. Нужный подъём - по коже, смотрящей вверх: насколько ткань выше кожи меньше GAP.
# ----------------------------------------------------------------------------------------------
bins = np.arange(0, 0.30 + 1e-6, LIFT_BIN)
bvh0 = cloth_bvh(src_co)
up, down = Vector((0, 0, 1)), Vector((0, 0, -1))
lift_need = np.zeros(len(bins))


def vertical_need(tree, p):
    """На сколько поднять ткань над точкой кожи p: кожа над тканью - на глубину и GAP, под тканью ближе GAP - до GAP.
    Ткань ниже кожи считается, только если она внутри тела (ближе, чем луч вниз выходит из тела): под плечом
    луч находил ткань по другую сторону тела, и подъём рос с каждым проходом."""
    v = Vector(p)
    hd = tree.ray_cast(v, down, 0.04)
    hu = tree.ray_cast(v, up, 0.04)
    if hd[0] is not None and (hu[0] is None or hd[3] < hu[3]):
        exit_ = body_bvh.ray_cast(v + down * 1e-4, down, 0.04)
        if exit_[0] is None or hd[3] < exit_[3]:
            return hd[3] + GAP
    return max(0.0, GAP - hu[3]) if hu[0] is not None else 0.0


for i, p in enumerate(body_rest):
    # только плечи и руки: верх груди тоже смотрит вверх, по нему шарф поднимало до подбородка (грудь - срезами,
    # остаток у горловины - вторым подъёмом после срезов)
    if p[2] < 0.80 or body_normal[i][2] < 0.4 or main_bone[i] not in LIFT_BONES:
        continue
    k = int(round(abs(p[0]) / LIFT_BIN))
    if k < len(bins):
        lift_need[k] = max(lift_need[k], vertical_need(bvh0, p))
lift = gauss(grow(lift_need, 1), 1.5)
print('[Chest] lift over shoulders (mm) by |x|: ' + ', '.join(f'{bins[k]:.2f}:{lift[k] * 1000:.0f}' for k in range(0, len(bins), 2)))
co = src_co.copy()
co[:, 2] += np.interp(np.abs(co[:, 0]), bins, lift) * smoothstep(LIFT_Z[0], LIFT_Z[1], src_co[:, 2])

# ----------------------------------------------------------------------------------------------
# Срезы: ткань отодвигается от центра тела по секторам угла
# ----------------------------------------------------------------------------------------------


def cut(V, E, z):
    """Точки пересечения рёбер с плоскостью на высоте z."""
    a, b = V[E[:, 0]], V[E[:, 1]]
    m = (a[:, 2] - z) * (b[:, 2] - z) < 0
    t = ((z - a[m, 2]) / (b[m, 2] - a[m, 2]))[:, None]
    return a[m] + (b[m] - a[m]) * t


levels = np.arange(LEVELS[0], LEVELS[1] + 1e-6, STEP)
marks = [i for i, z in enumerate(levels) if round(z * 100) % 4 == 0]


def table(values, smooth=SMOOTH):
    """Значения по уровням levels (None - нет данных) -> заполнены крайними и сглажены по высоте."""
    vals = np.array([np.nan if v is None else v for v in values], float)
    good = ~np.isnan(vals)
    vals = np.interp(np.arange(len(vals)), np.flatnonzero(good), vals[good])
    return gauss(vals, smooth)


def at(arr, z):
    return np.interp(z, levels, arr)


# Препятствия на каждом срезе: кожа торса и бёдер (выше SHOULDER_Z - только шея) и штаны, с зазором
torso_edge = np.array([main_bone[a] in TORSO and main_bone[b] in TORSO for a, b in body_edges])
# выше SHOULDER_Z - шея и верх груди: грудь у горловины выходила сквозь накидку
side = np.abs(body_normal[:, 2]) < 0.5  # кожа, смотрящая вверх, - под накидкой сверху: её ставит подъём
neck_edge = np.array([main_bone[a] in ('Neck', 'Chest') and main_bone[b] in ('Neck', 'Chest') and side[a] and side[b]
                      for a, b in body_edges])
only_neck = np.array([main_bone[a] == 'Neck' and main_bone[b] == 'Neck' for a, b in body_edges])
obstacles, centers = [], []
for z in levels:
    b = cut(body_rest, body_edges[neck_edge if z > SHOULDER_Z else torso_edge], z)
    p = cut(pants_co, pants_edges, z)
    # центр вверху - по шее: по шее с грудью он уходил вперёд к коже горла, и лучи от него шли куда попало
    cb = cut(body_rest, body_edges[only_neck], z) if z > SHOULDER_Z else b
    centers.append(None if len(cb) < 4 else (cb[:, 1].min() + cb[:, 1].max()) / 2)
    obstacles.append([(q, GAP) for q in b] + [(q, PANTS_GAP) for q in p])
center_y = table(centers)


def sector(dx, dy):
    """Сектор по углу от -Y к +X."""
    return int((math.atan2(dx, -dy) + math.pi) / (2 * math.pi) * SECTORS) % SECTORS


def circular_smooth(row):
    k = np.arange(-2, 3)
    w = np.exp(-0.5 * k ** 2)
    w /= w.sum()
    return sum(np.roll(row, -j) * wj for j, wj in zip(k, w))


def lookup(tab, z, ang):
    """Значение из таблицы (уровень по высоте, сектор по углу от -Y к +X): линейно по высоте и по кругу."""
    fi = np.clip((z - levels[0]) / STEP, 0, len(levels) - 1.001)
    i0 = fi.astype(int)
    ti = fi - i0
    fk = (ang + np.pi) / (2 * np.pi) * SECTORS - 0.5
    k0 = np.floor(fk).astype(int)
    tk = fk - k0
    k0, k1 = k0 % SECTORS, (k0 + 1) % SECTORS
    row = lambda i: tab[i, k0] * (1 - tk) + tab[i, k1] * tk
    return row(i0) * (1 - ti) + row(i0 + 1) * ti


def push_table(P):
    """Насколько отодвинуть ткань P по уровням и секторам: (r препятствия + зазор) - r ткани по тому же лучу.
    Ткань - первое попадание от центра: к телу ближе всего она."""
    tree = cloth_bvh(P)
    raw = np.zeros((len(levels), SECTORS))
    for i, z in enumerate(levels):
        c = Vector((0.0, center_y[i], z))
        for q, gap in obstacles[i]:
            d = Vector((q[0] - c.x, q[1] - c.y, 0))
            r = d.length
            if r < 1e-4:
                continue
            d /= r
            hit = tree.ray_cast(c, d, 0.4)
            if hit[0] is None:
                continue
            k = sector(d.x, d.y)
            raw[i, k] = max(raw[i, k], min(r + gap - hit[3], MAX_PUSH))
    # не меньше соседних секторов и уровней (ниже на шаг, выше на UP_REACH), затем сглаживание: по кругу и по высоте.
    # Вверх дальше: слой выше отодвигается не меньше нижнего. Иначе подол под поясом-обмоткой уходил наружу дальше
    # обмотки, его верхний край открывался, и в щель были видны штаны и фон (тело там спрятано)
    t = np.array([circular_smooth(np.maximum(np.maximum(r, np.roll(r, 1)), np.roll(r, -1))) for r in raw])
    t = np.array([t[max(0, i - UP_REACH):i + 2].max(0) for i in range(len(t))])
    w = np.unravel_index(raw.argmax(), raw.shape)
    return np.stack([table(list(t[:, k])) for k in range(SECTORS)], 1), (raw.max(), f'level {levels[w[0]]:.2f} sector {w[1]}')


def apply_push(P, tab):
    z = P[:, 2]
    cy = at(center_y, z)
    d = np.stack([P[:, 0], P[:, 1] - cy], 1)
    r = np.linalg.norm(d, axis=1)
    dr = lookup(tab, z, np.arctan2(d[:, 0], -d[:, 1]))
    P[:, :2] += d / np.maximum(r, 1e-6)[:, None] * dr[:, None]


# ----------------------------------------------------------------------------------------------
# Второй подъём - по месту (x, y). У горловины накидка почти горизонтальна: срез на ровной высоте её почти
# не пересекает, и верх груди между шарфом и плечом выходил сквозь неё на 5-15 мм. Нужный подъём - по коже
# груди, плеч и рук, смотрящей вверх. Шея - только срезами: подъём по коже шеи поднимал шарф с каждым проходом
# ----------------------------------------------------------------------------------------------
GX = np.arange(-0.30, 0.30 + 1e-6, LIFT_BIN)
GY = np.arange(-0.14, 0.16 + 1e-6, LIFT_BIN)


def lift_grid(P):
    tree = cloth_bvh(P)
    grid = np.zeros((len(GX), len(GY)))
    for i, p in enumerate(body_rest):
        if p[2] < 0.80 or body_normal[i][2] < 0.3 or main_bone[i] not in LIFT_BONES | {'Chest'}:
            continue
        kx, ky = int(round((p[0] - GX[0]) / LIFT_BIN)), int(round((p[1] - GY[0]) / LIFT_BIN))
        if 0 <= kx < len(GX) and 0 <= ky < len(GY):
            grid[kx, ky] = max(grid[kx, ky], vertical_need(tree, p))
    w = np.unravel_index(grid.argmax(), grid.shape)
    raw = (grid.max(), f'at ({GX[w[0]]:.2f}, {GY[w[1]]:.2f})')
    grid = np.array([grow(row, 1) for row in grid])
    grid = np.array([grow(col, 1) for col in grid.T]).T
    grid = np.array([gauss(row, 1.5, edge=False) for row in grid])
    grid = np.array([gauss(col, 1.5, edge=False) for col in grid.T]).T
    return grid, raw


def bilinear(g, x, y):
    fx = np.clip((x - GX[0]) / LIFT_BIN, 0, len(GX) - 1.001)
    fy = np.clip((y - GY[0]) / LIFT_BIN, 0, len(GY) - 1.001)
    ix, iy = fx.astype(int), fy.astype(int)
    tx, ty = fx - ix, fy - iy
    return (g[ix, iy] * (1 - tx) * (1 - ty) + g[ix + 1, iy] * tx * (1 - ty) +
            g[ix, iy + 1] * (1 - tx) * ty + g[ix + 1, iy + 1] * tx * ty)


# Сглаживание срезает пики нужного, поэтому несколько проходов: каждый добирает остаток после предыдущего.
# Сначала все проходы срезами, потом все подъёмом: вперемешку они раскачивали друг друга (подъём вводил в срез
# низ накидки, срез отодвигал его)
push_total = np.zeros((len(levels), SECTORS))
for it in range(PASSES):
    tab, raw_push = push_table(co)
    apply_push(co, tab)
    push_total += tab
    print(f'[Chest] pass {it + 1}: push needed up to {raw_push[0] * 1000:.1f} mm {raw_push[1]}')
for it in range(PASSES):
    grid, raw_lift = lift_grid(co)
    co[:, 2] += bilinear(grid, co[:, 0], co[:, 1]) * smoothstep(LIFT_Z[0], LIFT_Z[1], co[:, 2])
    print(f'[Chest] pass {it + 1}: lift by place needed up to {raw_lift[0] * 1000:.1f} mm {raw_lift[1]}')
print('[Chest] push by height (mm, min..max over sides): ' +
      ', '.join(f'{levels[i]:.2f}:{push_total[i].min() * 1000:.0f}..{push_total[i].max() * 1000:.0f}' for i in marks))
front, back = SECTORS // 2, 0
print('[Chest] push front/back (mm): ' +
      ', '.join(f'{levels[i]:.2f}:{push_total[i, front] * 1000:.0f}/{push_total[i, back] * 1000:.0f}' for i in marks))
for i, v in enumerate(me.vertices):
    v.co = Vector(co[i])
me.update()
# нормали модели после сдвига дали бы гранёный вид (как у штанов): гладкие нормали заново по новой форме
bpy.ops.object.select_all(action='DESELECT')
top.select_set(True)
bpy.context.view_layer.objects.active = top
if me.has_custom_normals:
    bpy.ops.mesh.customdata_custom_splitnormals_clear()
me.shade_smooth()

# ----------------------------------------------------------------------------------------------
# Что осталось в покое: кожа и штаны снаружи ткани (луч от точки внутрь по нормали упирается в ткань раньше,
# чем луч наружу)
# ----------------------------------------------------------------------------------------------
fit_bvh = cloth_bvh(co)


def outside(p, n):
    hin = fit_bvh.ray_cast(Vector(p), -Vector(n), 0.03)
    hout = fit_bvh.ray_cast(Vector(p), Vector(n), 0.08)
    return hin[3] if hin[0] is not None and (hout[0] is None or hin[3] < hout[3]) else None


left = {}
for i, p in enumerate(body_rest):
    bone = main_bone[i]
    if p[2] < 0.53 or bone == 'Head' or 'Hand' in bone or 'Arm' not in bone and bone not in TORSO:
        continue
    dep = outside(p, body_normal[i])
    if dep is not None:
        key = (bone, round(p[2] * 50) / 50)
        n, dd = left.get(key, (0, 0.0))
        left[key] = (n + 1, max(dd, dep))
        if dep > 0.005:
            nn = body_normal[i]
            print(f'[Chest]     skin out ({p[0] * 1000:.0f} {p[1] * 1000:.0f} {p[2] * 1000:.0f}) {bone} depth {dep * 1000:.1f} '
                  f'normal ({nn[0]:.2f} {nn[1]:.2f} {nn[2]:.2f})')
for (bone, zz), (n, dd) in sorted(left.items()):
    print(f'[Chest]   skin outside: {bone:14s} z {zz:.2f} verts {n:3d} up to {dd * 1000:.0f} mm')
pbm = world_bm(pants)
pants_normal = np.array([tuple(v.normal) for v in pbm.verts])
pbm.free()
# только наружная сторона штанов (нормаль от оси тела): у изнанки пояса и узла нормаль внутрь, проверка по ней бессмысленна
outer = [(p[0] * n[0] + (p[1] - at(center_y, p[2])) * n[1]) > 0 for p, n in zip(pants_co, pants_normal)]
pants_out = [i for i, p in enumerate(pants_co) if p[2] > 0.55 and outer[i] and outside(p, pants_normal[i]) is not None]
print(f'[Chest]   pants outside: {len(pants_out)} verts' +
      (f' at z {min(pants_co[i][2] for i in pants_out):.3f}..{max(pants_co[i][2] for i in pants_out):.3f}' if pants_out else ''))
for i in pants_out[:12]:
    p = pants_co[i]
    print(f'[Chest]     pants out ({p[0] * 1000:.0f} {p[1] * 1000:.0f} {p[2] * 1000:.0f}) depth {outside(p, pants_normal[i]) * 1000:.1f} '
          f'normal ({pants_normal[i][0]:.2f} {pants_normal[i][1]:.2f} {pants_normal[i][2]:.2f})')

# ----------------------------------------------------------------------------------------------
# Веса с кожи
# ----------------------------------------------------------------------------------------------
for vg in list(top.vertex_groups):
    top.vertex_groups.remove(vg)
for g in body.vertex_groups:
    top.vertex_groups.new(name=g.name)
transfer = top.modifiers.new('WeightTransfer', 'DATA_TRANSFER')
transfer.object = body
transfer.use_vert_data = True
transfer.data_types_verts = {'VGROUP_WEIGHTS'}
transfer.vert_mapping = 'POLYINTERP_NEAREST'
transfer.layers_vgroup_select_src = 'ALL'
transfer.layers_vgroup_select_dst = 'NAME'
bpy.ops.object.select_all(action='DESELECT')
top.select_set(True)
bpy.context.view_layer.objects.active = top
bpy.ops.object.modifier_apply(modifier=transfer.name)
# Накидка лежит на плечах и за рукой целиком не идёт: часть ARM_SHARE веса плеча руки (UpperArm) отдаётся
# ключице (Shoulder). С полным весом руки в прицеле край накидки на руке уходил вперёд, а соседняя часть оставалась
# на груди: накидка у плеча рвалась, сквозь разрыв была видна туника. Сглаживание весов по сетке (10 проходов)
# разрыв не убрало, а стоя дало излом полосы на передней части накидки
for side in ('Left', 'Right'):
    arm_g, sh_g = top.vertex_groups[side + 'UpperArm'], top.vertex_groups[side + 'Shoulder']
    for v in me.vertices:
        w = next((g.weight for g in v.groups if g.group == arm_g.index), 0.0)
        if w > 0 and ARM_SHARE < 1:
            arm_g.add([v.index], w * ARM_SHARE, 'REPLACE')
            sh_g.add([v.index], w * (1 - ARM_SHARE), 'ADD')
# группы без весов убираются целиком: иначе у вершины остаётся лишняя кость (Docs/Gear.md, «Веса после переноса»)
limit_bones(top)
bad = [v.index for v in me.vertices if not v.groups]
if bad or max(len(v.groups) for v in me.vertices) > SKIN_BONES:
    raise RuntimeError(f'{name}: bad weights ({len(bad)} vertices without bones)')
used = {top.vertex_groups[g.group].name for v in me.vertices for g in v.groups if g.weight > 0}
print(f'[Chest] bones: {sorted(used)}')
top.parent = arm_obj
top.modifiers.new('Armature', 'ARMATURE').object = arm_obj

# ----------------------------------------------------------------------------------------------
# Зона тела под туникой: кожа торса, над которой по нормали есть ткань и которую не видно снаружи ни в одну
# щель. Шею в шарфе и кожу у проём видно сверху и сбоку - она остаётся, иначе в Unity (грани с одной стороны)
# сквозь горловину был бы виден пол
# ----------------------------------------------------------------------------------------------
N_DIRS = 160
dirs = []
for k in range(N_DIRS):
    zz = 1 - 2 * (k + 0.5) / N_DIRS
    rr = math.sqrt(1 - zz * zz)
    a = k * math.pi * (3 - math.sqrt(5))
    dirs.append(Vector((rr * math.cos(a), rr * math.sin(a), zz)))
fit_bvh = cloth_bvh(co)


def masks(path):
    with open(os.path.splitext(os.path.abspath(path))[0] + '_BodyMask.json', encoding='utf-8') as f:
        pts = np.array(json.load(f)['hidden']).reshape(-1, 3)
    return {int(np.argmin(((body_rest - p) ** 2).sum(1))) for p in pts}

occluders = [world_bm(o) for o in [pants] + others]
occluders = [BVHTree.FromBMesh(b) for b in occluders]


def seen_from_outside(i):
    """Видна ли кожа снаружи: хоть один луч от неё уходит, не встретив ни туники, ни тела, ни других частей. Штаны
    закрывают обзор снизу: без них из кожи талии и под грудью луч уходил вниз в щель между подолом и штанами.
    Лучи круче 30° вниз не считаются: камера игры не смотрит снизу вверх под подол."""
    p = Vector(body_rest[i]) + Vector(body_normal[i]) * 0.0005
    n = Vector(body_normal[i])
    for dv in dirs:
        if dv.dot(n) <= 0.05 or dv.z < -0.5:
            continue
        if all(t.ray_cast(p, dv, 1.0)[0] is None for t in [fit_bvh, body_bvh] + occluders):
            return True
    return False


covered = [i for i, p in enumerate(body_rest) if main_bone[i] in HIDE_BONES and
           fit_bvh.ray_cast(Vector(p) + Vector(body_normal[i]) * 0.0005, Vector(body_normal[i]), 0.08)[0] is not None]
under = {i for i in covered if not seen_from_outside(i)}
kept = {}
for i in set(covered) - under:
    key = (main_bone[i], round(body_rest[i][2] * 50) / 50)
    kept[key] = kept.get(key, 0) + 1
print("[Chest] kept (seen through openings): " + ", ".join(f"{b} {z:.2f}:{n}" for (b, z), n in sorted(kept.items())))
pants_hidden = masks(PANTS_PATH)
other_hidden = [masks(p) for p in OTHER_PARTS]
# треугольники на краю подола: часть вершин под туникой, остальные под штанами - прячет туника
body.data.calc_loop_triangles()
edge_extra = set()
for lt in body.data.loop_triangles:
    vs = set(lt.vertices)
    if vs & under and vs <= (under | pants_hidden):
        edge_extra |= vs - under
# одиночная вершина среди спрятанных (в ложбинке на пояснице луч по нормали проходит мимо туники) оставляла
# 6 треугольников кожи под туникой
nbrs = {}
for a, b in body_edges:
    nbrs.setdefault(a, set()).add(b)
    nbrs.setdefault(b, set()).add(a)
filled = {i for i in range(len(body_rest)) if i not in under | pants_hidden and main_bone[i] in {'Hips', 'Spine', 'Chest'} and
          nbrs[i] <= (under | edge_extra | pants_hidden)}
under |= filled
hidden = sorted(under | edge_extra)
hid = set(hidden)
print(f'[Chest] torso skin covered by the top: {len(covered)}, seen through openings (kept): {len(covered) - len(under)}, '
      f'hidden {len(hidden)} (of them on the pants edge {len(edge_extra)})')
# голая кожа торса между туникой и штанами: треугольники торса ниже горловины, которые не прячет ни одна часть
bare = [lt for lt in body.data.loop_triangles
        if all(main_bone[i] in {'Hips', 'Spine'} for i in lt.vertices) and
        not (set(lt.vertices) <= hid or set(lt.vertices) <= pants_hidden)]
print(f'[Chest] hips and spine triangles not hidden by the top or pants: {len(bare)}')

# ----------------------------------------------------------------------------------------------
# Позы из игры: штаны, закрытые туникой в покое, не выходят наружу (луч по нормали штанов не находит тунику)
# ----------------------------------------------------------------------------------------------


def posed(o):
    bm = world_bm(o)
    pts = np.array([tuple(v.co) for v in bm.verts])
    nrm = np.array([tuple(v.normal) for v in bm.verts])
    bm.free()
    return pts, nrm


pants_cov = [i for i in range(len(pants_co)) if outer[i] and
             fit_bvh.ray_cast(Vector(pants_co[i]) + Vector(pants_normal[i]) * 0.0005, Vector(pants_normal[i]), 0.08)[0] is not None]
print(f'[Chest] pants verts covered by the top at rest: {len(pants_cov)}')
worst = (0, '')
for pose in list(RECORDED):
    set_pose(arm_obj, pose)
    pp, pn = posed(pants)
    tp, _ = posed(top)
    tree = cloth_bvh(tp)
    n_out = sum(1 for i in pants_cov
                if tree.ray_cast(Vector(pp[i]) + Vector(pn[i]) * 0.0005, Vector(pn[i]), 0.08)[0] is None)
    if n_out > worst[0]:
        worst = (n_out, pose)
    if n_out > 10:
        print(f'[Chest]   {pose:24s} pants out of the top: {n_out}')
print(f'[Chest] poses: pants out of the top worst {worst[0]} of {len(pants_cov)} ({worst[1]})')
set_pose(arm_obj, 'tpose')

# ----------------------------------------------------------------------------------------------
# Снимки
# ----------------------------------------------------------------------------------------------
if args.png:
    scene = bpy.context.scene
    scene.render.engine = 'BLENDER_WORKBENCH'
    scene.display.shading.light = 'STUDIO'
    scene.display.shading.color_type = 'TEXTURE'
    scene.render.resolution_x, scene.render.resolution_y = 800, 800
    world = bpy.data.worlds.new('PreviewWorld')
    world.color = (0.20, 0.23, 0.26)
    scene.world = world
    cam = bpy.data.objects.new('Cam', bpy.data.cameras.new('Cam'))
    scene.collection.objects.link(cam)
    scene.camera = cam
    cam.data.type = 'ORTHO'
    cam.data.clip_start = 0.001
    os.makedirs(args.png, exist_ok=True)
    VIEWS = {'front': (0, -1, 0), 'back': (0, 1, 0), 'left': (1, 0, 0), 'right': (-1, 0, 0), 'top': (0, -0.05, 1),
             'persp': (-0.6, -0.7, 0.4), 'persp_back': (0.5, 0.75, 0.45), 'below': (0.1, -0.5, -0.8),
             'below_back': (0.2, 0.8, -0.35), 'game': (0.15, 0.9, 0.42)}

    def render(prefix, center, size, views):
        cam.data.ortho_scale = size
        for view in views:
            dv = Vector(VIEWS[view]).normalized()
            cam.location = center + dv * 2
            cam.rotation_euler = (-dv).to_track_quat('-Z', 'Y').to_euler()
            scene.render.filepath = os.path.join(args.png, f'{prefix}_{view}.png')
            bpy.ops.render.render(write_still=True)

    def show(*objs):
        for o in bpy.data.objects:
            if o.type == 'MESH':
                o.hide_render = o not in objs

    ALL = ('front', 'back', 'left', 'right', 'top', 'persp', 'persp_back')
    if gen_body:
        show(*gen_body)
        render('gen', Vector((0, 0, 0.74)), 0.55, ALL)
    show(gen_view)
    render('src', Vector((0, 0, 0.74)), 0.55, ALL)
    show(top)
    render('fit', Vector((0, 0, 0.74)), 0.55, ALL)
    # тело без спрятанной кожи всех частей, как в игре
    bm2 = bmesh.new()
    bm2.from_mesh(body.data)
    bm2.verts.ensure_lookup_table()
    sets = [hid, pants_hidden] + other_hidden
    bmesh.ops.delete(bm2, geom=[f for f in bm2.faces if any(all(v.index in s for v in f.verts) for s in sets)],
                     context='FACES_ONLY')
    bm2.to_mesh(body.data)
    bm2.free()
    skin_mat = bpy.data.materials.new('Skin')
    skin_mat.diffuse_color = (0.85, 0.62, 0.52, 1)
    body.data.materials.clear()
    body.data.materials.append(skin_mat)
    show(body, top, pants, *others)
    render('body', Vector((0, 0, 0.74)), 0.55, ALL + ('below', 'below_back', 'game'))
    render('shoulder', Vector((0.11, 0.02, 0.85)), 0.2, ('front', 'back', 'left', 'top', 'persp'))
    render('neck', Vector((0, 0.02, 0.9)), 0.2, ('front', 'left', 'top', 'persp', 'game'))
    render('waist', Vector((0, 0.01, 0.6)), 0.3, ('front', 'back', 'left', 'below', 'below_back', 'game', 'persp_back'))
    for pose in [p for p in RECORDED if p.split(':')[1].split('@')[0] in ('idle', 'walk', 'run', 'jump', 'aim', 'aim_strafe', 'aim_down', 'reload')][::5]:
        set_pose(arm_obj, pose)
        hips = arm_obj.pose.bones['Hips'].head
        render(pose.replace(':', '_').replace('@', '_'), Vector((hips.x, hips.y, 0.72)), 0.6, ('front', 'left', 'game', 'persp_back'))
        if 'aim' in pose or 'idle' in pose or 'reload' in pose:
            ch = arm_obj.pose.bones['Chest'].head
            render('close_' + pose.replace(':', '_').replace('@', '_'), Vector((ch.x, ch.y, ch.z + 0.1)), 0.32,
                   ('front', 'persp', 'game', 'top'))
    set_pose(arm_obj, 'tpose')

# ----------------------------------------------------------------------------------------------
# Маска, текстуры, экспорт
# ----------------------------------------------------------------------------------------------
os.makedirs(os.path.dirname(out), exist_ok=True)
with open(stem + '_BodyMask.json', 'w', encoding='utf-8') as f:
    json.dump({'part': 'chest', 'bodyVertices': len(body_rest),
               'hidden': [round(float(c), 6) for i in hidden for c in body_rest[i]]}, f)
print(f'[Chest] mask {stem}_BodyMask.json')

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
    print(f'[Chest] texture {img.filepath_raw}')


save(pixels(base), f'{name}.png', 'sRGB')
save(pixels(normal), f'{name}_normal.png', 'Non-Color')
ms = np.zeros((TEXTURE_SIZE * TEXTURE_SIZE, 4), np.float32)
ms[:, 3] = 1.0 - pixels(roughness)[:, 0]
save(ms, f'{name}_metallic_smoothness.png', 'Non-Color')

for o in [gen_view, body, pants] + others + gen_body:
    bpy.data.objects.remove(o)
top.matrix_world = Matrix.Identity(4)
me.materials[0].name = 'Material'
for m in bpy.data.materials:
    m.use_nodes = False
bpy.ops.object.select_all(action='DESELECT')
arm_obj.select_set(True)
top.select_set(True)
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
print(f'[Chest] exported {out}, verts {len(me.vertices)}')
