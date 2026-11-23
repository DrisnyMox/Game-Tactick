"""
Ставит перчатки, подогнанные пользователем под тело игрока Assets/Models/20261121003938_8b8745d5.fbx, на его скелет.
Пользователь вырезал перчатки из модели генератора персонажа в них (3ds Max) и подогнал под базовое тело только
правую. Левая - отражение правой по плоскости симметрии тела: тело в кистях и предплечьях симметрично (проверяется).

Запуск (из корня проекта):
    "E:\\Software\\Blender\\blender.exe" --background --factory-startup --python Tools/Blender/place_gloves.py -- ^
        --part nomad --src "<FBX перчаток>" --out Assets/Models/Gear/NomadGloves.fbx [--png "<папка>"]

Что делает:
  * координаты: тело в сцене Max повёрнуто на 180° вокруг вертикали и увеличено в 2,54 раза. Перчатки берутся
    с матрицей объекта (подгонка пользователя - и в вершинах, и в ней), затем поворот и масштаб обратно (FRAME);
  * в FBX обе перчатки, подогнана одна: берётся кусок на стороне side, второй выбрасывается;
  * вершины ближе GAP к коже отодвигаются по нормали кожи, не меньше соседей и сглаженно (форма пользователя
    не меняется, сдвиг - доли миллиметра); точки внутри граней - не ближе FACE_GAP (грань провисает на костяшках);
  * вторая перчатка - отражение по плоскости симметрии тела, затем то же отодвигание для обеих: если левая кисть
    чуть отличается, левая перчатка подгоняется к ней сама;
  * веса - с ближайшей кожи тела, SKIN_BONES кости на вершину;
  * маска: кожа кисти и предплечья, над которой по нормали есть перчатка и которую не видно снаружи ни с одной
    стороны (камера смотрит на руки с любой стороны). Прячутся только треугольники тела, целиком закрытые
    перчаткой: у отверстий для пальцев треугольники тела выходят из перчатки;
  * своя развёртка перчатки: текстура модели - от целого персонажа, обмотки и манжета на ней - сотни кусочков
    в несколько пикселей. Рисунок переносится с исходной текстуры по тем же граням в TEXTURE_SIZE, пятна кожи
    перекрашиваются в цвет своей части (PALETTE - замша, обмотка, манжета по картинкам генератора);
  * металла нет (металл 0, гладкость из шероховатости), экспорт FBX со скелетом тела.
С --png рисует перчатки на теле с текстурой в T-позе и в позах игры (стоя, в прицеле), и тело одним цветом без
спрятанной зоны (дыры в сетке и кожа сквозь перчатку).
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
from gear_fit import (add_covered, average, covered_triangles, nearest_skin, neighbours,  # noqa: E402
                      push_faces_off_skin, seen_from_outside, sphere_dirs, world_bm)
from gear_poses import POSES, RECORDED, SKIN_BONES, limit_bones, set_pose  # noqa: E402

BODY_PATH = 'Assets/Models/20261121003938_8b8745d5.fbx'

# side - сторона подогнанной перчатки; frame - из сцены Max (координаты объекта с его матрицей) в координаты тела
PARTS = {
    'nomad': dict(side='Right', frame=Matrix.Diagonal((-1 / 2.54, -1 / 2.54, 1 / 2.54, 1))),
}
GAP = 0.001         # вершина перчатки не ближе к коже, м (рост тела 1,088; в Unity умножается на 1,662)
FACE_GAP = 0.0005   # точка внутри грани не ближе к коже
COVER_REACH = 0.03  # кожа под перчаткой: луч по нормали находит её не дальше
ARM_FROM = 0.25     # кожа рук от этого |x| (локоть 0,27) - только там ищется кожа под перчаткой
TEXTURE_SIZE = 1024  # на исходной 4096 перчатка - около 825 x 825 пикселей, больше не нужно
BLEED = 8           # цвет кусков развёртки продлевается наружу на столько пикселей
# Цвета частей перчатки по картинкам генератора (персонаж в перчатках спереди и сзади, sRGB): замша, бирюзовая
# обмотка, светлая манжета. Манжета - грани не дальше CUFF_LEN от края у локтя (светлые - ещё на 12 мм)
PALETTE = ((0.62, 0.51, 0.41), (0.40, 0.45, 0.44), (0.65, 0.61, 0.59))
CUFF_LEN = 0.030

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
parser = argparse.ArgumentParser()
parser.add_argument('--part', required=True, choices=sorted(PARTS))
parser.add_argument('--src', required=True)
parser.add_argument('--out', required=True)
parser.add_argument('--png', default='')
args = parser.parse_args(argv)
cfg = PARTS[args.part]
src, out = os.path.abspath(args.src), os.path.abspath(args.out)
stem = os.path.splitext(out)[0]
name = os.path.basename(stem)
sgn = 1.0 if cfg['side'] == 'Left' else -1.0  # знак X подогнанной стороны: левая сторона персонажа на +X

bpy.ops.wm.read_factory_settings(use_empty=True)

# ----------------------------------------------------------------------------------------------
# Тело: плоскость симметрии и симметрия рук
# ----------------------------------------------------------------------------------------------
bpy.ops.import_scene.fbx(filepath=os.path.abspath(BODY_PATH))
arm_obj = bpy.data.objects['Armature']
body = bpy.data.objects['Body']
limit_bones(body)
set_pose(arm_obj, 'tpose')
body_rest = np.array([tuple(body.matrix_world @ v.co) for v in body.data.vertices])
body_kd = KDTree(len(body_rest))
for i, p in enumerate(body_rest):
    body_kd.insert(Vector(p), i)
body_kd.balance()
mids = []
for p in body_rest:
    if p[0] < -0.05:
        q, j, d = body_kd.find(Vector((-p[0], p[1], p[2])))
        if d < 0.002:
            mids.append((p[0] + body_rest[j][0]) / 2)
plane_x = float(np.mean(mids))  # тело симметрично не точно по x = 0
print(f'[Gloves] body mirror plane x {plane_x * 1000:.3f} mm ({len(mids)} pairs, spread {np.std(mids) * 1000:.3f} mm)')
for lo, hi, what in ((0.20, 0.27, 'elbow'), (0.27, 0.415, 'forearm'), (0.415, 0.46, 'palm'), (0.46, 0.53, 'fingers')):
    side_idx = [i for i, p in enumerate(body_rest) if lo <= (p[0] - plane_x) * sgn < hi]
    d = [body_kd.find(Vector((2 * plane_x - body_rest[i][0], *body_rest[i][1:])))[2] for i in side_idx]
    print(f'[Gloves] body symmetry {what}: {len(side_idx)} verts, mirrored to the nearest vertex of the other side '
          f'max {max(d) * 1000:.2f} mm, mean {np.mean(d) * 1000:.2f} mm')
body_bm = world_bm(body)
body_normal = [tuple(v.normal) for v in body_bm.verts]
skin_bvh = BVHTree.FromBMesh(body_bm)
group_names = {g.index: g.name for g in body.vertex_groups}
main_bone = [group_names[max(v.groups, key=lambda g: g.weight).group] if v.groups else '' for v in body.data.vertices]

# ----------------------------------------------------------------------------------------------
# Перчатка: подогнанный кусок в координатах тела
# ----------------------------------------------------------------------------------------------
before = set(bpy.data.objects)
bpy.ops.import_scene.fbx(filepath=src)
new = [o for o in bpy.data.objects if o not in before]
meshes = [o for o in new if o.type == 'MESH']
for o in new:
    if o.type != 'MESH':
        bpy.data.objects.remove(o)
for o in meshes:
    o.parent = None
    o.data.transform(cfg['frame'] @ o.matrix_world)
    o.matrix_world = Matrix.Identity(4)
bpy.ops.object.select_all(action='DESELECT')
for o in meshes:
    o.select_set(True)
bpy.context.view_layer.objects.active = meshes[0]
if len(meshes) > 1:
    bpy.ops.object.join()
glove = meshes[0]
glove.name = name
me = glove.data
# Свои нормали FBX после отражения остаются у углов граней прежними: левая перчатка выходила гранёной пятнами.
# Нормали - заново, гладкие
if me.has_custom_normals:
    bpy.ops.mesh.customdata_custom_splitnormals_clear()
me.shade_smooth()
bm = bmesh.new()
bm.from_mesh(me)
pieces, seen = [], set()
for v in bm.verts:
    if v.index in seen:
        continue
    stack, piece = [v], []
    seen.add(v.index)
    while stack:
        a = stack.pop()
        piece.append(a)
        for e in a.link_edges:
            b = e.other_vert(a)
            if b.index not in seen:
                seen.add(b.index)
                stack.append(b)
    pieces.append(piece)
for piece in pieces:
    xs = [v.co.x for v in piece]
    print(f'[Gloves] piece {len(piece)} verts, x {min(xs):.3f}..{max(xs):.3f}')
keep = {v.index for piece in pieces if np.mean([v.co.x for v in piece]) * sgn > 0 for v in piece}
bmesh.ops.delete(bm, geom=[v for v in bm.verts if v.index not in keep], context='VERTS')
open_edges = sum(1 for e in bm.edges if len(e.link_faces) == 1)
bm.to_mesh(me)
bm.free()
me.update()
print(f'[Gloves] {cfg["side"]} glove: {len(me.vertices)} verts, {len(me.polygons)} faces, open edges {open_edges}')


# ----------------------------------------------------------------------------------------------
# Своя развёртка перчатки. Перчатку вырезали из модели целого персонажа, и текстура - его: кожа, лицо, одежда.
# У перчатки одним куском только ладонь, обмотки и манжета - сотни кусочков в несколько пикселей по всей текстуре.
# Уменьшенная текстура и мип-уровни смешивали их с кожей вокруг - перчатка в пятнах. Новая развёртка - крупные
# куски только перчатки; рисунок переносится с исходной текстуры 4096 по тем же граням (точка грани в новой
# развёртке берёт цвет той же точки в старой), поэтому совпадает с геометрией как был
# ----------------------------------------------------------------------------------------------
def image_array(img):
    a = np.empty(img.size[0] * img.size[1] * 4, np.float32)
    img.pixels.foreach_get(a)
    return a.reshape(img.size[1], img.size[0], 4)


def rebake(me, sources, size):
    """Новая развёртка (Smart UV Project) и перенос картинок sources со старой развёртки. Возвращает картинки size x size
    строками снизу; вокруг кусков цвет продлён на BLEED пикселей (мип-уровни смешивают соседей)."""
    old = me.uv_layers.active.name
    new = me.uv_layers.new(name='GloveUV')
    me.uv_layers.active = new
    bpy.ops.object.select_all(action='DESELECT')
    glove.select_set(True)
    bpy.context.view_layer.objects.active = glove
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.uv.smart_project(angle_limit=math.radians(60), island_margin=0.006, area_weight=0.0)
    bpy.ops.object.mode_set(mode='OBJECT')
    me.calc_loop_triangles()
    uv_old = np.array([tuple(d.uv) for d in me.uv_layers[old].data])
    uv_new = np.array([tuple(d.uv) for d in me.uv_layers['GloveUV'].data])
    outs = [np.zeros((size, size, 4), np.float32) for _ in sources]
    face_of = np.full((size, size), -1, np.int32)  # грань перчатки в каждом пикселе развёртки
    filled = np.zeros((size, size), bool)
    for lt in me.loop_triangles:
        t = uv_new[list(lt.loops)] * size
        s = uv_old[list(lt.loops)]
        x0, y0 = np.maximum(np.floor(t.min(0)).astype(int) - 1, 0)
        x1, y1 = np.minimum(np.ceil(t.max(0)).astype(int) + 2, size)
        if x1 <= x0 or y1 <= y0:
            continue
        py, px = np.mgrid[y0:y1, x0:x1] + 0.5
        (ax, ay), (bx, by), (cx, cy) = t
        den = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy)
        if abs(den) < 1e-12:
            continue
        l1 = ((by - cy) * (px - cx) + (cx - bx) * (py - cy)) / den
        l2 = ((cy - ay) * (px - cx) + (ax - cx) * (py - cy)) / den
        l3 = 1 - l1 - l2
        # пиксель у края куска берёт цвет ближайшей точки грани: ребро соседней грани закроет его своим
        tol = 1.5 / max(abs(den) ** 0.5, 1.0)
        hit = (l1 >= -tol) & (l2 >= -tol) & (l3 >= -tol) & ~filled[y0:y1, x0:x1]
        if not hit.any():
            continue
        w = np.clip(np.stack([l1[hit], l2[hit], l3[hit]], 1), 0, None)
        w /= w.sum(1, keepdims=True)
        uv = w @ s
        for src, dst in zip(sources, outs):
            h, wd = src.shape[:2]
            fx = np.clip(uv[:, 0] * wd - 0.5, 0, wd - 1.001)
            fy = np.clip(uv[:, 1] * h - 0.5, 0, h - 1.001)
            ix, iy = fx.astype(int), fy.astype(int)
            dx, dy = (fx - ix)[:, None], (fy - iy)[:, None]
            c = (src[iy, ix] * (1 - dx) * (1 - dy) + src[iy, ix + 1] * dx * (1 - dy) +
                 src[iy + 1, ix] * (1 - dx) * dy + src[iy + 1, ix + 1] * dx * dy)
            sub = dst[y0:y1, x0:x1]
            sub[hit] = c
        face_of[y0:y1, x0:x1][hit] = lt.polygon_index
        filled[y0:y1, x0:x1] |= hit
    me.uv_layers.remove(me.uv_layers[old])
    print(f'[Gloves] own UV: {filled.mean() * 100:.0f} % of the texture under the glove')
    return outs, face_of


def bleed(img, good, steps):
    """Цвет кусков продлевается наружу на steps пикселей."""
    for _ in range(steps):
        acc = np.zeros_like(img)
        cnt = np.zeros(good.shape, np.float32)
        for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            cnt += np.roll(np.roll(good, dy, 0), dx, 1)
            acc += np.roll(np.roll(img * good[..., None], dy, 0), dx, 1)
        fill = ~good & (cnt > 0)
        img[fill] = acc[fill] / cnt[fill][:, None]
        good |= fill


tex_nodes = {n.image.name: n for n in me.materials[0].node_tree.nodes if n.type == 'TEX_IMAGE' and n.image}
kinds = {'base': next(k for k in tex_nodes if not any(s in k for s in ('normal', 'roughness', 'metallic'))),
         'normal': next(k for k in tex_nodes if 'normal' in k),
         'roughness': next(k for k in tex_nodes if 'roughness' in k)}
baked, face_of = rebake(me, [image_array(tex_nodes[kinds[k]].image) for k in kinds], TEXTURE_SIZE)
baked = dict(zip(kinds, baked))


def clean_colors(img, face_of):
    """Цвета модели генератора близки к картинкам, по которым её делали, но на перчатке пятна кожи: край манжеты,
    между обмотками, края отверстий пальцев (у модели там была кожа персонажа). Пиксель своей части (замша, обмотка,
    манжета) остаётся и только подтягивается к цвету картинки; пятно другого цвета перекрашивается в цвет части,
    яркость (тени, складки) остаётся. Часть: манжета - грани у края к локтю, обмотка - сине-серый пиксель, остальное -
    замша (между обмотками на картинках замша, а не кожа)."""
    ids = face_of.ravel()
    cols = img.reshape(-1, 4)[:, :3]
    has = ids >= 0
    fx = np.array([p.center.x for p in me.polygons])
    dist = np.abs(fx - (fx.max() if sgn < 0 else fx.min()))  # от края манжеты у локтя
    order = np.argsort(ids[has])
    sid, scol = ids[has][order], cols[has][order]
    nf = len(me.polygons)
    a, b = np.searchsorted(sid, np.arange(nf)), np.searchsorted(sid, np.arange(nf), side='right')
    med = np.array([np.median(scol[i:j], 0) if j > i else (0.5, 0.5, 0.5) for i, j in zip(a, b)])
    mx, mn = med.max(1), med.min(1)
    light = (mx > 0.6) & ((mx - mn) / np.maximum(mx, 1e-6) < 0.15)
    blue_face = med[:, 2] >= med[:, 0] - 0.02
    cuff_face = ~blue_face & ((dist < CUFF_LEN) | ((dist < CUFF_LEN + 0.012) & light))
    r, g, bl = cols[:, 0], cols[:, 1], cols[:, 2]
    mx, mn = cols.max(1), cols.min(1)
    sat = (mx - mn) / np.maximum(mx, 1e-6)
    hue_red = (r >= g) & (g >= bl) & ((g - bl) < 0.35 * (r - bl + 1e-6))  # оттенок ближе к красному, чем у замши
    blue_px = bl >= r - 0.02
    cls = np.full(len(cols), -1)
    cls[has & cuff_face[np.maximum(ids, 0)]] = 2
    cls[has & (cls < 0) & blue_px] = 1
    cls[has & (cls < 0)] = 0
    good = {0: ~blue_px & ~(hue_red & (sat > 0.22)),           # замша без кожи
            1: blue_px,                                        # обмотка
            2: ~blue_px & (sat < 0.2)}                         # манжета без кожи
    lum = cols @ np.array([0.299, 0.587, 0.114])
    out = cols.copy()
    for c, ref in enumerate(PALETTE):
        m = cls == c
        ok = m & good[c]
        mean = np.median(cols[ok], 0)
        lc = np.median(lum[ok])
        out[ok] = cols[ok] * (np.array(ref) / mean)
        bad = m & ~good[c]
        out[bad] = np.array(ref) * np.clip(lum[bad] / lc, 0.7, 1.08)[:, None]
        print(f'[Gloves] texture {("suede", "wrap", "cuff")[c]}: {int(m.sum())} px, model colour {mean.round(3)} -> '
              f'{ref}, repainted spots {int(bad.sum())} px ({bad.sum() / max(m.sum(), 1) * 100:.0f} %)')
    img.reshape(-1, 4)[:, :3] = np.clip(out, 0, 1)


clean_colors(baked['base'], face_of)
for k in kinds:
    bleed(baked[k], face_of >= 0, BLEED)
for k, name_ in kinds.items():
    # снимки --png рисуют новую развёртку с новой картинкой
    img = bpy.data.images.new(f'{name}_{k}', TEXTURE_SIZE, TEXTURE_SIZE, alpha=True)
    img.pixels.foreach_set(baked[k].ravel())
    tex_nodes[name_].image = img


def skin_gaps(co):
    return np.array([d for _, _, d in nearest_skin(skin_bvh, co, 0.1)])


def report(co, what):
    for s, side in ((sgn, cfg['side']), (-sgn, 'other')):
        m = (co[:, 0] - plane_x) * s > 0
        if not m.any():
            continue
        d = skin_gaps(co[m])
        print(f'[Gloves] {what} {side}: over the skin min {d.min() * 1000:.1f} mm, 5 % {np.percentile(d, 5) * 1000:.1f}, '
              f'median {np.median(d) * 1000:.1f}, 95 % {np.percentile(d, 95) * 1000:.1f}; closer than {GAP * 1000:.1f} mm '
              f'{int((d < GAP).sum())}, inside deeper 1 mm {int((d < -0.001).sum())} of {int(m.sum())}')


def push_off(co, edges, passes=4):
    """Вершины ближе GAP к коже - по нормали кожи, не меньше соседей и сглаженно, как последний шаг shell_fit."""
    nb = neighbours(edges, list(range(len(co))))
    for it in range(passes):
        near = nearest_skin(skin_bvh, co, 0.1)
        need = np.array([max(0.0, GAP - d) for _, _, d in near])
        worst = need.max()
        if worst < 1e-5:
            break
        normals = np.array([n for _, n, _ in near])
        need = np.array([max([need[k]] + [need[j] for j in n_]) for k, n_ in enumerate(nb)])
        for _ in range(3):
            need = 0.5 * need + 0.5 * average(need, nb)
        co += normals * need[:, None]
        print(f'[Gloves] pass {it + 1}: closer to the skin than {GAP * 1000:.1f} mm up to {worst * 1000:.1f} mm')


def mesh_co():
    a = np.empty(len(me.vertices) * 3)
    me.vertices.foreach_get('co', a)
    return a.reshape(-1, 3)


def set_co(co):
    me.vertices.foreach_set('co', co.ravel())
    me.update()


co = mesh_co()
report(co, 'as given')

# ----------------------------------------------------------------------------------------------
# Вторая перчатка - отражение по плоскости симметрии тела
# ----------------------------------------------------------------------------------------------
bm = bmesh.new()
bm.from_mesh(me)
dup = bmesh.ops.duplicate(bm, geom=list(bm.verts) + list(bm.edges) + list(bm.faces))
for g in dup['geom']:
    if isinstance(g, bmesh.types.BMVert):
        g.co.x = 2 * plane_x - g.co.x
bmesh.ops.reverse_faces(bm, faces=[g for g in dup['geom'] if isinstance(g, bmesh.types.BMFace)])
bm.to_mesh(me)
bm.free()
me.update()
co = mesh_co()
report(co, 'mirrored')
given = co.copy()
edges = np.array([tuple(e.vertices) for e in me.edges])
push_off(co, edges)
me.calc_loop_triangles()
tris = [tuple(lt.vertices) for lt in me.loop_triangles]
push_faces_off_skin(co, tris, skin_bvh, FACE_GAP, tag='[Gloves]', what='glove faces')
moved = np.linalg.norm(co - given, axis=1)
print(f'[Gloves] moved off the skin: {int((moved > 1e-5).sum())} verts, up to {moved.max() * 1000:.2f} mm')
set_co(co)
report(co, 'placed')

# ----------------------------------------------------------------------------------------------
# Веса с тела
# ----------------------------------------------------------------------------------------------
for vg in list(glove.vertex_groups):
    glove.vertex_groups.remove(vg)
for g in body.vertex_groups:
    glove.vertex_groups.new(name=g.name)
transfer = glove.modifiers.new('WeightTransfer', 'DATA_TRANSFER')
transfer.object = body
transfer.use_vert_data = True
transfer.data_types_verts = {'VGROUP_WEIGHTS'}
transfer.vert_mapping = 'POLYINTERP_NEAREST'
transfer.layers_vgroup_select_src = 'ALL'
transfer.layers_vgroup_select_dst = 'NAME'
bpy.ops.object.select_all(action='DESELECT')
glove.select_set(True)
bpy.context.view_layer.objects.active = glove
bpy.ops.object.modifier_apply(modifier=transfer.name)
# после переноса группы очищаются целиком и пишутся заново (Docs/Gear.md, «Веса после переноса»)
limit_bones(glove)
bad = [v.index for v in me.vertices if not v.groups]
used = {glove.vertex_groups[g.group].name for v in me.vertices for g in v.groups if g.weight > 0}
if bad or max(len(v.groups) for v in me.vertices) > SKIN_BONES:
    raise RuntimeError(f'{name}: bad weights ({len(bad)} vertices without bones)')
print(f'[Gloves] bones: {sorted(used)}')
glove.parent = arm_obj
mod = glove.modifiers.new('Armature', 'ARMATURE')
mod.object = arm_obj

# ----------------------------------------------------------------------------------------------
# Маска: кожа под перчаткой, которую не видно снаружи
# ----------------------------------------------------------------------------------------------
gbm = bmesh.new()
gbm.from_mesh(me)
glove_bvh = BVHTree.FromBMesh(gbm)
gbm.free()
arm_skin = [i for i, p in enumerate(body_rest) if abs(p[0] - plane_x) > ARM_FROM]


def covered_at(p, n):
    hit = glove_bvh.ray_cast(Vector(p) + Vector(n) * 0.0005, Vector(n), COVER_REACH)
    return hit[0] is not None


dirs = sphere_dirs()
# руки видно с любой стороны: лучи вверх и вниз тоже берутся
unseen = {i for i in arm_skin if covered_at(body_rest[i], body_normal[i]) and
          not seen_from_outside(body_rest[i], body_normal[i], dirs, (glove_bvh, skin_bvh), max_slope=1.01)}
print(f'[Gloves] arm skin {len(arm_skin)} verts, under the glove and unseen {len(unseen)}')
tris_ok = covered_triangles(body, body_rest, body_normal, lambda i: i in unseen, covered_at)
hidden, added, left = add_covered(body, set(), tris_ok)
hidden = sorted(hidden)
print(f'[Gloves] hidden body vertices {len(hidden)} ({len(tris_ok)} triangles fully under the glove, '
      f'{left} verts left visible so that no other triangle hides)')
hid = set(hidden)
for s, side in ((sgn, cfg['side']), (-sgn, 'other')):
    print(f'[Gloves] hidden on {side}: {sum(1 for i in hidden if (body_rest[i][0] - plane_x) * s > 0)}')


# ----------------------------------------------------------------------------------------------
# Снимки
# ----------------------------------------------------------------------------------------------
def setup_render():
    scene = bpy.context.scene
    scene.render.engine = 'BLENDER_WORKBENCH'
    scene.display.shading.light = 'STUDIO'
    scene.render.resolution_x = scene.render.resolution_y = 700
    world = bpy.data.worlds.new('PreviewWorld')
    world.color = (0.20, 0.23, 0.26)
    scene.world = world
    cam = bpy.data.objects.new('Cam', bpy.data.cameras.new('Cam'))
    scene.collection.objects.link(cam)
    scene.camera = cam
    cam.data.type = 'ORTHO'
    return cam


def evaluated(o):
    dg = bpy.context.evaluated_depsgraph_get()
    ev = o.evaluated_get(dg)
    m = ev.to_mesh()
    a = np.empty(len(m.vertices) * 3)
    m.vertices.foreach_get('co', a)
    ev.to_mesh_clear()
    return a.reshape(-1, 3)


def render(cam, prefix, center, size, views):
    dirs_ = {'front': (0, -1, 0), 'back': (0, 1, 0), 'top': (0, -0.02, 1), 'bottom': (0, -0.02, -1),
             'out': (sgn, 0, 0), 'persp': (0.5 * sgn, -0.7, 0.5), 'persp_back': (0.4 * sgn, 0.7, -0.4)}
    cam.data.ortho_scale = size
    for view in views:
        d = Vector(dirs_[view]).normalized()
        cam.location = center + d * 3
        cam.rotation_euler = (-d).to_track_quat('-Z', 'Y').to_euler()
        bpy.context.scene.render.filepath = os.path.join(os.path.abspath(args.png), f'{prefix}_{view}.png')
        bpy.ops.render.render(write_still=True)


if args.png:
    cam = setup_render()
    scene = bpy.context.scene
    bm = bmesh.new()
    bm.from_mesh(body.data)
    bmesh.ops.delete(bm, geom=[f for f in bm.faces if all(v.index in hid for v in f.verts)], context='FACES_ONLY')
    bm.to_mesh(body.data)
    bm.free()
    skin = bpy.data.materials.new('Skin')
    skin.diffuse_color = (0.85, 0.62, 0.52, 1)
    body.data.materials.clear()
    body.data.materials.append(skin)
    tex_mat = me.materials[0]
    flat = bpy.data.materials.new('Flat')
    flat.diffuse_color = (0.35, 0.45, 0.3, 1)
    for pose in ['tpose'] + [p for p in ('play:idle@1.0', 'play:aim@1.0', 'play:reload@1.2', 'play:run@0.8')
                             if p in RECORDED]:
        set_pose(arm_obj, pose)
        pts = evaluated(glove)
        tag = pose.replace(':', '_').replace('@', '_')
        for s, side in ((sgn, 'R'), (-sgn, 'L')):
            c = Vector(pts[(pts[:, 0] - plane_x) * s > 0].mean(0))
            me.materials[0] = tex_mat
            scene.display.shading.color_type = 'TEXTURE'
            render(cam, f'{tag}_{side}', c, 0.3, ('front', 'top', 'bottom', 'persp') if pose == 'tpose' else ('persp', 'front'))
            me.materials[0] = flat
            scene.display.shading.color_type = 'MATERIAL'
            render(cam, f'{tag}_{side}_flat', c, 0.3, ('persp', 'bottom'))
            me.materials[0] = tex_mat
    set_pose(arm_obj, 'tpose')

# ----------------------------------------------------------------------------------------------
# Маска, текстуры, экспорт
# ----------------------------------------------------------------------------------------------
os.makedirs(os.path.dirname(out), exist_ok=True)
with open(stem + '_BodyMask.json', 'w', encoding='utf-8') as f:
    json.dump({'part': args.part, 'bodyVertices': len(body_rest),
               'hidden': [round(float(c), 6) for i in hidden for c in body_rest[i]]}, f)
print(f'[Gloves] mask {stem}_BodyMask.json')

tex_dir = stem + '_Textures'
os.makedirs(tex_dir, exist_ok=True)


def save(a, file_name, colorspace):
    img = bpy.data.images.new(file_name, TEXTURE_SIZE, TEXTURE_SIZE, alpha=True)
    img.colorspace_settings.name = colorspace
    img.pixels.foreach_set(a.ravel())
    img.filepath_raw = os.path.join(tex_dir, file_name)
    img.file_format = 'PNG'
    img.save()
    print(f'[Gloves] texture {img.filepath_raw}')


save(baked['base'], f'{name}.png', 'sRGB')
save(baked['normal'], f'{name}_normal.png', 'Non-Color')
ms = np.zeros((TEXTURE_SIZE * TEXTURE_SIZE, 4), np.float32)  # металла нет
ms[:, 3] = 1.0 - baked['roughness'].reshape(-1, 4)[:, 0]
save(ms, f'{name}_metallic_smoothness.png', 'Non-Color')

bpy.data.objects.remove(body)
glove.matrix_world = Matrix.Identity(4)
me.materials[0].name = 'Material'
for m in bpy.data.materials:
    m.use_nodes = False
bpy.ops.object.select_all(action='DESELECT')
arm_obj.select_set(True)
glove.select_set(True)
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
print(f'[Gloves] exported {out}')
