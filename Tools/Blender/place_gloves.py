"""
Ставит перчатки, подогнанные пользователем под тело игрока Assets/Models/20261121003938_8b8745d5.fbx, на его скелет.
Пользователь вырезал перчатки из модели генератора персонажа в них (3ds Max) и подогнал под базовое тело только
одну (NomadGloves - правую, SunGloves - левую). Вторая - отражение по плоскости симметрии тела: тело в кистях
и предплечьях симметрично (проверяется). У SunGloves ещё: подгонка только в вершинах (object_matrix), сварка
отрезанных треугольников (weld), пометка под цвет качества материалом (quality_material, маска по граням),
чистка текстуры gear_texture.clean (texture; --texture-only --png - только снимки до и после), гладкость по классу.

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
import gear_texture  # noqa: E402
from gear_fit import (add_covered, average, covered_triangles, nearest_skin, neighbours,  # noqa: E402
                      push_faces_off_skin, seen_from_outside, sphere_dirs, world_bm)
from gear_poses import POSES, RECORDED, SKIN_BONES, limit_bones, set_pose  # noqa: E402

BODY_PATH = 'Assets/Models/20261121003938_8b8745d5.fbx'

# side - сторона подогнанной перчатки; frame - из сцены Max (координаты объекта с его матрицей) в координаты тела.
# object_matrix False - подгонка пользователя только в вершинах, матрица объекта (масштаб 6,45 и поворот осей Max)
# отбрасывается, как у SunChest и SunHead; frame тогда - только Y вверх -> Z вверх
PARTS = {
    'nomad': dict(side='Right', frame=Matrix.Diagonal((-1 / 2.54, -1 / 2.54, 1 / 2.54, 1))),
    # Культ Солнца: тёмно-синяя ткань с тиснёным узором, серая обмотка на манжете (пометка '01 - Default'), латунный
    # кант, кожаные накладки на ладони, латунная бляха на ладони, латунная панель на тыльной стороне. В FBX
    # подогнана левая; 18 треугольников у кнопки на запястье отделены от перчатки (вершины разрезаны) - weld
    'sun': dict(side='Left', frame=Matrix.Rotation(math.radians(90), 4, 'X'), object_matrix=False, weld=True,
                quality_material='01 - Default', stray_faces=10, texture_size=1024,
                smoothness=dict(fabric=0.2, leather=0.3, brass=0.45),
                texture=dict(
                    # медианы по картинкам генератора SunGloves_front.webp и SunGloves_back.webp (sRGB): ткань светлее
                    # и синее, чем у штанов и нагрудника; латунь - как у комплекта
                    palette=dict(fabric=(0.245, 0.25, 0.305), grey=(0.66, 0.64, 0.62), brass=(0.62, 0.49, 0.33),
                                 leather=(0.27, 0.215, 0.195), copper=(0.72, 0.36, 0.16), sun=(0.33, 0.35, 0.42)),
                    # тиснёный узор ткани (ромбы, круги) - рисунок модели: яркость ткани не сглаживается
                    shade=dict(fabric=(0.0, 0.7, 1.3), grey=(0.8, 0.5, 1.35), brass=(0.8, 0.5, 1.4),
                               leather=(0.6, 0.6, 1.3), copper=(0.5, 0.5, 1.4)),
                    soft_classes=('fabric', 'leather', 'brass', 'grey'), leather_thick=4, leather_min_px=1500,
                    hard=(),  # заполняется ниже: все куски, кроме перчатки (панель на тыльной стороне)
                    panel=(), fabric_pieces=('p00',),
                    brass_share=0.25, window_area=0.003, behind_reach=0.03, sun_clear=0.02,
                    sun_radius=0.01, sun_spacing=0.03, sun_alpha=0.0)),
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
parser.add_argument('--texture-only', action='store_true')  # только снимки текстуры до и после чистки
args = parser.parse_args(argv)
cfg = PARTS[args.part]
src, out = os.path.abspath(args.src), os.path.abspath(args.out)
stem = os.path.splitext(out)[0]
name = os.path.basename(stem)
sgn = 1.0 if cfg['side'] == 'Left' else -1.0  # знак X подогнанной стороны: левая сторона персонажа на +X
TEXTURE_SIZE = cfg.get('texture_size', TEXTURE_SIZE)
if args.png:
    os.makedirs(args.png, exist_ok=True)

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
    o.data.transform(cfg['frame'] @ o.matrix_world if cfg.get('object_matrix', True) else cfg['frame'])
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
if cfg.get('weld'):
    # SunGloves: 18 треугольников у кнопки на запястье лежат на месте в перчатке, но их вершины разрезаны (в
    # неподогнанном FBX они - часть сетки): без сварки отодвигание от кожи и веса двигали бы их отдельно, и шов
    # освещался бы гранями. Сваривается только совпадающее до 0,01 мм - форма та же
    # Только вершины отдельных треугольников - к совпадающим вершинам остальной сетки (сварка всего подряд
    # склеила бы и совпадающие вершины соседних пальцев)
    bm.verts.ensure_lookup_table()
    seen_w, loose = set(), []
    for v in bm.verts:
        if v.index in seen_w:
            continue
        stack, comp = [v], []
        seen_w.add(v.index)
        while stack:
            a = stack.pop()
            comp.append(a)
            for e in a.link_edges:
                b = e.other_vert(a)
                if b.index not in seen_w:
                    seen_w.add(b.index)
                    stack.append(b)
        if len(comp) == 3:
            loose += comp
    loose_set = set(loose)
    kd_w = KDTree(len(bm.verts))
    for v in bm.verts:
        if v not in loose_set:
            kd_w.insert(v.co, v.index)
    kd_w.balance()
    targets = {}
    for v in loose:
        _, j, dist = kd_w.find(v.co)
        if dist < 0.00001:
            targets[v] = bm.verts[j]
    bmesh.ops.weld_verts(bm, targetmap=targets)
    # вокруг них в сетке разрез: совпадающие вершины на открытых рёбрах (не края манжеты и не пальцы - те замкнуты)
    n0 = len(bm.verts)
    rim = list({v for e in bm.edges if len(e.link_faces) == 1 for v in e.verts})
    bmesh.ops.remove_doubles(bm, verts=rim, dist=0.00001)
    print(f'[Gloves] loose triangles {len(loose) // 3}: welded {len(targets)} of {len(loose)} verts; '
          f'crack around them closed: {n0 - len(bm.verts)} verts')
open_edges = sum(1 for e in bm.edges if len(e.link_faces) == 1)
bm.to_mesh(me)
bm.free()
me.update()
print(f'[Gloves] {cfg["side"]} glove: {len(me.vertices)} verts, {len(me.polygons)} faces, open edges {open_edges}')

# Куски по связности: p00 - перчатка, дальше по числу вершин (как в place_head_fitted.py)
bm = bmesh.new()
bm.from_mesh(me)
bm.verts.ensure_lookup_table()
lab = np.full(len(bm.verts), -1)
comps = []
for v in bm.verts:
    if lab[v.index] >= 0:
        continue
    stack, comp = [v], []
    lab[v.index] = len(comps)
    while stack:
        a = stack.pop()
        comp.append(a.index)
        for e in a.link_edges:
            b = e.other_vert(a)
            if lab[b.index] < 0:
                lab[b.index] = len(comps)
                stack.append(b)
    comps.append(comp)
bm.free()
rank = {k: r for r, k in enumerate(sorted(range(len(comps)), key=lambda k: (-len(comps[k]), min(comps[k]))))}
piece_names = [f'p{k:02d}' for k in range(len(comps))]
poly_piece = np.array(piece_names)[[rank[lab[p.vertices[0]]] for p in me.polygons]]
for k in range(len(comps)):
    print(f'[Gloves] piece p{k:02d}: {int((poly_piece == piece_names[k]).sum())} faces')

# Пометка пользователя под цвет качества (материал quality_material): отдельные мелкие куски пометки (меньше
# stray_faces граней, не касаются основной) - промах выделения, снимаются. Слот 1 - пометка, до отражения и экспорта
quality_poly = np.zeros(len(me.polygons), bool)
if 'quality_material' in cfg:
    mat_names = [m.name if m else '' for m in me.materials]
    quality_poly = np.array([mat_names[p.material_index].split('.')[0] == cfg['quality_material'] for p in me.polygons])
    faces_of_edge = {}
    for p in me.polygons:
        for ek in p.edge_keys:
            faces_of_edge.setdefault(ek, []).append(p.index)
    seen_f = set()
    for f0 in np.flatnonzero(quality_poly):
        if f0 in seen_f:
            continue
        stack, grp = [int(f0)], []
        seen_f.add(int(f0))
        while stack:
            a = stack.pop()
            grp.append(a)
            for ek in me.polygons[a].edge_keys:
                for b in faces_of_edge[ek]:
                    if quality_poly[b] and b not in seen_f:
                        seen_f.add(b)
                        stack.append(b)
        if len(grp) < cfg['stray_faces']:
            c = me.polygons[grp[0]].center
            print(f'[Gloves] marked faces apart from the rest: {len(grp)} at ({c.x * 1000:.0f} {c.y * 1000:.0f} '
                  f'{c.z * 1000:.0f}) mm - unmarked')
            quality_poly[grp] = False
    print(f'[Gloves] materials {mat_names}; quality faces {int(quality_poly.sum())} of {len(me.polygons)}')
    tex_slot = next(i for i, m in enumerate(me.materials)
                    if m and m.node_tree and any(n.type == 'TEX_IMAGE' for n in m.node_tree.nodes))
    # пометка идёт в маску цвета качества, материал один (иначе в Unity вторая часть меша без текстуры). Маска
    # считается по развёртке одной перчатки: отражение - копия с той же развёрткой
    for k in reversed(range(len(me.materials))):
        if k != tex_slot:
            me.materials.pop(index=k)
    me.polygons.foreach_set('material_index', np.zeros(len(me.polygons), np.int32))
    me.update()


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
kinds = {'base': next(k for k in tex_nodes if not any(s in k for s in ('normal', 'roughness', 'metallic')))}
if 'texture' not in cfg:
    kinds.update(normal=next(k for k in tex_nodes if 'normal' in k), roughness=next(k for k in tex_nodes if 'roughness' in k))
TEX_VIEWS = {'top': (0, 0.001, 1), 'palm': (0, 0.001, -1), 'front': (0, -1, 0), 'back': (0, 1, 0),
             'persp': (0.35 * sgn, -0.6, 0.7), 'cuff': (-0.5 * sgn, -0.6, 0.6)}


def texture_shots(prefix):
    """Перчатка без тела с текстурой при ровном свете (цвет текстуры как есть): тыльная сторона, ладонь, сбоку."""
    scene = bpy.context.scene
    scene.render.engine = 'BLENDER_WORKBENCH'
    scene.display.shading.light = 'FLAT'
    scene.display.shading.color_type = 'TEXTURE'
    scene.render.resolution_x, scene.render.resolution_y = 1000, 560
    if 'TexCam' not in bpy.data.objects:
        world = bpy.data.worlds.new('TexWorld')
        world.color = (0.75, 0.75, 0.75)
        scene.world = world
        cam_ = bpy.data.objects.new('TexCam', bpy.data.cameras.new('TexCam'))
        scene.collection.objects.link(cam_)
        cam_.data.type = 'ORTHO'
        cam_.data.ortho_scale = 0.28
        cam_.data.clip_start = 0.001
    cam_ = bpy.data.objects['TexCam']
    scene.camera = cam_
    body.hide_render = True
    pts = mesh_co()
    c = Vector(pts.mean(0))
    for view, dv in TEX_VIEWS.items():
        dv = Vector(dv).normalized()
        cam_.location = c + dv * 2
        cam_.rotation_euler = (-dv).to_track_quat('-Z', 'Y').to_euler()
        scene.render.filepath = os.path.join(os.path.abspath(args.png), f'{prefix}_{view}.png')
        bpy.ops.render.render(write_still=True)
    body.hide_render = False


def mesh_co():
    a = np.empty(len(me.vertices) * 3)
    me.vertices.foreach_get('co', a)
    return a.reshape(-1, 3)


if args.png and 'texture' in cfg:
    texture_shots('tex_model')  # исходная развёртка, текстура 4096
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


def show_texture(a, tag):
    img = bpy.data.images.new(f'{name}_{tag}', TEXTURE_SIZE, TEXTURE_SIZE, alpha=True)
    img.pixels.foreach_set(a.ravel())
    tex_nodes[kinds['base']].image = img


smooth_map = None
quality_tex = None
if 'texture' in cfg:
    # Чистка gear_texture.clean, как у SunPants, SunChest, SunHead: кожа и пятна заливаются соседями, ткань, обмотка,
    # кожа ладони и латунь - в цвета картинок генератора, яркость (складки, тиснёный узор) остаётся
    S_ = TEXTURE_SIZE
    if args.png:
        show_texture(baked['base'], 'own_uv')
        texture_shots('tex_before')
    tcfg = dict(cfg['texture'], hard=tuple(n for n in piece_names if n not in cfg['texture']['fabric_pieces']))
    flat_normal = np.zeros((S_, S_, 4), np.float32)
    flat_normal[...] = (0.5, 0.5, 1.0, 1.0)
    rough = np.full((S_, S_, 4), 0.8, np.float32)
    rough[..., 3] = 1.0
    # серое без пометки - только на обмотке (пометка): белёсые пятна на пальцах - мусор генератора
    cleaned, _, _ = gear_texture.clean(me, mesh_co(), poly_piece, quality_poly, baked['base'], flat_normal, rough, S_,
                                       tcfg, tag='[Gloves]', grey_poly=np.zeros(len(me.polygons), bool))
    cls = gear_texture.DEBUG['cls']
    inside_ = gear_texture.DEBUG['inside']
    hard_ = gear_texture.DEBUG['hard']
    sm = cfg['smoothness']
    smooth_map = np.full((S_, S_), sm['fabric'], np.float32)
    smooth_map[inside_ & (cls == gear_texture.LEATHER)] = sm['leather']
    smooth_map[inside_ & (hard_ & gear_texture.brass_mask(cleaned[..., :3], tcfg, lum_min=True) |
                          (cls == gear_texture.BRASS))] = sm['brass']
    smooth_map = gear_texture.bleed(smooth_map[..., None].repeat(3, -1), inside_, BLEED)[..., 0]
    quality_tex = gear_texture.face_mask(me, quality_poly, S_)
    baked = {'base': cleaned, 'normal': flat_normal}
    show_texture(cleaned, 'clean')
    if args.png:
        texture_shots('tex_after')
    if args.texture_only:
        print('[Gloves] texture only: done')
        sys.exit(0)
else:
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


def skin_outside(co):
    """Кожа рук снаружи перчатки: луч от кожи внутрь находит перчатку (не дальше 20 мм) раньше, чем наружу."""
    me.calc_loop_triangles()
    tree = BVHTree.FromPolygons([Vector(p) for p in co], [tuple(t.vertices) for t in me.loop_triangles])
    found = []
    for i, p in enumerate(body_rest):
        if abs(p[0] - plane_x) < ARM_FROM:
            continue
        n = Vector(body_normal[i])
        hin = tree.ray_cast(Vector(p), -n, 0.02)
        hout = tree.ray_cast(Vector(p), n, 0.1)
        if hin[0] is not None and (hout[0] is None or hin[3] < hout[3]):
            found.append((hin[3], i))
    found.sort(reverse=True)
    print(f'[Gloves] skin outside the gloves: {len(found)} verts' +
          ''.join(f'\n[Gloves]     {main_bone[i]} ({body_rest[i][0] * 1000:.0f} {body_rest[i][1] * 1000:.0f} '
                  f'{body_rest[i][2] * 1000:.0f}) {d * 1000:.1f} mm' for d, i in found[:12]))


skin_outside(co)

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
# Палец перчатки должен идти за своим пальцем тела: ребро перчатки между вершинами, у которых главная кость - разные
# пальцы дальше основной фаланги, значит палец перчатки лёг между пальцами тела и порвётся, когда пальцы сожмутся
FINGERS = ('Thumb', 'Index', 'Middle', 'Ring', 'Little')
vmain = [glove.vertex_groups[max(v.groups, key=lambda g: g.weight).group].name for v in me.vertices]
vfinger = [next((f for f in FINGERS if f in n and 'Proximal' not in n), None) for n in vmain]
cross = [e for e in me.edges if vfinger[e.vertices[0]] and vfinger[e.vertices[1]] and
         vfinger[e.vertices[0]] != vfinger[e.vertices[1]]]
print(f'[Gloves] edges between different fingers (beyond the proximal phalanx): {len(cross)}')
pairs = {}
for e in cross:
    a, b = e.vertices
    k = tuple(sorted((vmain[a], vmain[b])))
    p = (me.vertices[a].co + me.vertices[b].co) / 2
    pairs.setdefault(k, []).append(tuple(p))
for k, ps in sorted(pairs.items(), key=lambda t: -len(t[1]))[:12]:
    P = np.array(ps)
    print(f'[Gloves]   {k[0]} - {k[1]}: {len(ps)} edges at x {P[:, 0].min() * 1000:.0f}..{P[:, 0].max() * 1000:.0f} '
          f'y {P[:, 1].min() * 1000:.0f}..{P[:, 1].max() * 1000:.0f} z {P[:, 2].min() * 1000:.0f}..{P[:, 2].max() * 1000:.0f}')
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
# гладкость: у NomadGloves - из карты шероховатости модели, у SunGloves её нет - по классу пикселя
ms[:, 3] = smooth_map.ravel() if smooth_map is not None else 1.0 - baked['roughness'].reshape(-1, 4)[:, 0]
save(ms, f'{name}_metallic_smoothness.png', 'Non-Color')
if quality_tex is not None:
    save((np.repeat(quality_tex.reshape(-1, 1), 4, 1) * [1, 1, 1, 0] + [0, 0, 0, 1]).astype(np.float32),
         f'{name}_quality.png', 'Non-Color')

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
