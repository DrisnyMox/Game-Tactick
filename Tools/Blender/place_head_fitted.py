"""
Ставит головной убор, который пользователь вырезал из модели генератора и сам подогнал под тело игрока
Assets/Models/20261121003938_8b8745d5.fbx, на его скелет. Первый - убор Культа Солнца SunHead.

Запуск (из корня проекта):
    "E:\\Software\\Blender\\blender.exe" --background --factory-startup --python Tools/Blender/place_head_fitted.py -- ^
        --part sun_head --src "<FBX убора>" --out Assets/Models/Gear/SunHead.fbx [--png "<папка>"] [--texture-only]

Форма - пользователя, скриптом заново не сажается (как place_chest_fitted.py). Что делается:
  * координаты: вершины в FBX уже в единицах тела (матрица объекта из Max - масштаб 6,45 и поворот - отбрасывается),
    только Y вверх -> Z вверх;
  * куски сетки - по связности (p00 - самый большой), материалы сводятся в один; грани, помеченные пользователем
    материалом quality_material, - маска цвета качества, но только на кусках quality_pieces (на остальных пометка -
    промах выделения: тёмная латунь и ткань под ней стали бы полосой цвета качества);
  * своя развёртка и перенос рисунка, как у NomadGloves (place_gloves.py): текстура - целого персонажа, убор занимает
    4 % её, лучи и обруч - узкие куски среди кожи лица, края колпака у лица и уха захватывают кожу;
  * текстура чистится gear_texture.clean (кожа и пятна на ткани заливаются соседями, ткань и серое - в цвета
    комплекта). С --png снимки до и после; --texture-only - только они, без посадки и экспорта;
  * подгонка пользователя проверяется и правится только там, где нужно игре: вершины ближе GAP к коже отодвигаются
    по нормали кожи, не меньше соседей и сглаженно; точки внутри граней - не ближе FACE_GAP;
  * веса - с ближайшей кожи тела, SKIN_BONES кости на вершину; жёсткие куски (обруч, лучи) - целиком на Head;
  * маска: кожа головы под убором, которую не видно снаружи, - только треугольники тела, целиком закрытые убором
    (у края убора у ушей и шеи треугольник со всеми вершинами под убором может выходить из-под него).
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
import gear_texture  # noqa: E402
from gear_fit import (add_covered, average, covered_triangles, nearest_skin, neighbours,  # noqa: E402
                      push_faces_off_skin, seen_from_outside, sphere_dirs, world_bm)
from gear_poses import RECORDED, SKIN_BONES, limit_bones, set_pose  # noqa: E402

BODY_PATH = 'Assets/Models/20261121003938_8b8745d5.fbx'
GEAR = 'Assets/Models/Gear/'
PARTS = {
    # Культ Солнца: колпак из индиго-чёрной ткани со стёжкой медной нитью, серая полоса через макушку от лба до
    # затылка, клапан на затылке, латунный обруч с диском надо лбом и семь латунных лучей над обручем. Полосу
    # пользователь пометил материалом '01 - Default'
    'sun_head': dict(quality_material='01 - Default', quality_pieces=('p01',), chest='SunChest',
                     texture=dict(
                         # цвета - как у SunPants и SunChest (один комплект)
                         palette=dict(fabric=(0.165, 0.178, 0.215), grey=(0.67, 0.645, 0.62), brass=(0.62, 0.49, 0.31),
                                      leather=(0.31, 0.224, 0.14), copper=(0.72, 0.36, 0.16), sun=(0.33, 0.35, 0.42)),
                         shade=dict(fabric=(2.5, 0.75, 1.2), grey=(0.8, 0.5, 1.35), brass=(0.8, 0.5, 1.4),
                                    leather=(1.0, 0.6, 1.3), copper=(0.5, 0.5, 1.4)),
                         # на колпаке латуни и меди нет: латунный или рыжий пиксель там - кожа лица и уха модели
                         # у края колпака; серое - кремовое (вставки под обручем сзади)
                         soft_classes=('fabric', 'grey'), grey_sat=0.22,
                         hard=(),  # заполняется ниже: все куски, кроме колпака
                         panel=(), fabric_pieces=('p01',),
                         brass_share=0.25, window_area=0.003, behind_reach=0.03, sun_clear=0.02,
                         # узора солнц у убора нет: на колпаке генератор нарисовал стёжку, она остаётся
                         sun_radius=0.01, sun_spacing=0.03, sun_alpha=0.0)),
}
GAP = 0.002          # вершина убора не ближе к коже, м (рост тела 1,088; в Unity умножается на 1,662)
FACE_GAP = 0.001     # точка внутри грани не ближе к коже
HEAD = {'Head', 'Neck'}
TEXTURE_SIZE = 1024  # своя развёртка: на исходной 4096 убор - около 720 тыс. пикселей, здесь около 60 % из 1 млн
BLEED = 8
SMOOTHNESS = dict(fabric=0.15, brass=0.45)  # карт шероховатости и нормалей у модели нет

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
parser = argparse.ArgumentParser()
parser.add_argument('--part', required=True, choices=sorted(PARTS))
parser.add_argument('--src', required=True)
parser.add_argument('--out', required=True)
parser.add_argument('--png', default='')
parser.add_argument('--texture-only', action='store_true')
args = parser.parse_args(argv)
cfg = PARTS[args.part]
src, out = os.path.abspath(args.src), os.path.abspath(args.out)
stem = os.path.splitext(out)[0]
name = os.path.basename(stem)
if args.png:
    os.makedirs(args.png, exist_ok=True)

bpy.ops.wm.read_factory_settings(use_empty=True)


def import_new(path):
    before = set(bpy.data.objects)
    bpy.ops.import_scene.fbx(filepath=os.path.abspath(path))
    return [o for o in bpy.data.objects if o not in before]


# ----------------------------------------------------------------------------------------------
# Тело
# ----------------------------------------------------------------------------------------------
import_new(BODY_PATH)
arm_obj = bpy.data.objects['Armature']
body = bpy.data.objects['Body']
limit_bones(body)
set_pose(arm_obj, 'tpose')
body_rest = np.array([tuple(body.matrix_world @ v.co) for v in body.data.vertices])
group_names = {g.index: g.name for g in body.vertex_groups}
main_bone = [group_names[max(v.groups, key=lambda g: g.weight).group] if v.groups else '' for v in body.data.vertices]
bbm = world_bm(body)
body_normal = np.array([tuple(v.normal) for v in bbm.verts])
skin_bvh = BVHTree.FromBMesh(bbm)
bbm.free()

# ----------------------------------------------------------------------------------------------
# Убор: координаты, куски, пометка, один материал
# ----------------------------------------------------------------------------------------------
meshes = [o for o in import_new(src) if o.type == 'MESH']
for o in [o for o in bpy.data.objects if o.type == 'EMPTY']:
    bpy.data.objects.remove(o)
for o in meshes:
    o.parent = None
    # подгонка пользователя - в вершинах; матрица объекта - масштаб 6,45 (2,54 дважды) и поворот осей Max
    o.data.transform(Matrix.Rotation(math.radians(90), 4, 'X'))
    o.matrix_world = Matrix.Identity(4)
bpy.ops.object.select_all(action='DESELECT')
for o in meshes:
    o.select_set(True)
bpy.context.view_layer.objects.active = meshes[0]
if len(meshes) > 1:
    bpy.ops.object.join()
hat = meshes[0]
hat.name = name
me = hat.data

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
order = sorted(range(len(comps)), key=lambda k: (-len(comps[k]), min(comps[k])))
rank = {k: r for r, k in enumerate(order)}
piece = np.array([rank[k] for k in lab])
piece_names = [f'p{k:02d}' for k in range(len(comps))]
co0 = np.array([tuple(v.co) for v in me.vertices])
for k in range(len(comps)):
    P = co0[piece == k]
    print(f'[Head] piece p{k:02d}: {len(P)} verts, x {P[:, 0].min():.3f}..{P[:, 0].max():.3f} '
          f'y {P[:, 1].min():.3f}..{P[:, 1].max():.3f} z {P[:, 2].min():.3f}..{P[:, 2].max():.3f}')
poly_piece = np.array(piece_names)[piece[[p.vertices[0] for p in me.polygons]]]
mat_names = [m.name if m else '' for m in me.materials]
marked = np.array([mat_names[p.material_index].split('.')[0] == cfg['quality_material'] for p in me.polygons])
quality_poly = marked & np.isin(poly_piece, cfg['quality_pieces'])
for k in sorted(set(poly_piece[marked & ~quality_poly])):
    print(f'[Head] marked faces on {k} ({int((marked & (poly_piece == k)).sum())}): unmarked - not the cloth strip')
print(f'[Head] materials {mat_names}; marked faces {int(marked.sum())}, kept {int(quality_poly.sum())} of {len(me.polygons)}')
tex_slot = next(i for i, m in enumerate(me.materials)
                if m and m.node_tree and any(n.type == 'TEX_IMAGE' for n in m.node_tree.nodes))
for k in reversed(range(len(me.materials))):
    if k != tex_slot:
        me.materials.pop(index=k)
for p in me.polygons:
    p.material_index = 0
tex_node = next(n for n in me.materials[0].node_tree.nodes if n.type == 'TEX_IMAGE' and n.image)
hard_names = tuple(n for n in piece_names if n not in cfg['texture']['fabric_pieces'])


# ----------------------------------------------------------------------------------------------
# Снимки
# ----------------------------------------------------------------------------------------------
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
VIEWS = {'front': (0, -1, 0), 'back': (0, 1, 0), 'left': (1, 0, 0), 'right': (-1, 0, 0), 'top': (0, -0.05, 1),
         'persp': (-0.6, -0.7, 0.4), 'persp_l': (0.6, -0.7, 0.4), 'persp_back': (0.5, 0.75, 0.45),
         'below': (0.1, -0.6, -0.6), 'below_back': (0.2, 0.8, -0.35), 'game': (0.15, 0.9, 0.42)}
HEAD_C = (0, 0.012, 1.03)


def render(prefix, center, size, views):
    cam.data.ortho_scale = size
    for view in views:
        dv = Vector(VIEWS[view]).normalized()
        cam.location = Vector(center) + dv * 2
        cam.rotation_euler = (-dv).to_track_quat('-Z', 'Y').to_euler()
        scene.render.filepath = os.path.join(os.path.abspath(args.png), f'{prefix}_{view}.png')
        bpy.ops.render.render(write_still=True)


def show(*objs):
    for o in bpy.data.objects:
        if o.type == 'MESH':
            o.hide_render = o not in objs


def texture_shots(prefix):
    show(hat)
    render(prefix, HEAD_C, 0.19, ('front', 'back', 'left', 'right', 'persp', 'persp_back', 'game'))
    render(prefix + '_close', (0.035, 0.02, 1.0), 0.1, ('left', 'back'))


# ----------------------------------------------------------------------------------------------
# Своя развёртка и перенос рисунка (как place_gloves.rebake)
# ----------------------------------------------------------------------------------------------
def image_array(img):
    a = np.empty(img.size[0] * img.size[1] * 4, np.float32)
    img.pixels.foreach_get(a)
    return a.reshape(img.size[1], img.size[0], 4)


def rebake(source, size):
    """Новая развёртка (Smart UV Project) и перенос картинки source со старой развёртки, size x size строками снизу."""
    old = me.uv_layers.active.name
    new = me.uv_layers.new(name='HeadUV')
    me.uv_layers.active = new
    bpy.ops.object.select_all(action='DESELECT')
    hat.select_set(True)
    bpy.context.view_layer.objects.active = hat
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.uv.smart_project(angle_limit=math.radians(60), island_margin=0.008, area_weight=0.0)
    bpy.ops.object.mode_set(mode='OBJECT')
    me.calc_loop_triangles()
    uv_old = np.array([tuple(d.uv) for d in me.uv_layers[old].data])
    uv_new = np.array([tuple(d.uv) for d in me.uv_layers['HeadUV'].data])
    dst = np.zeros((size, size, 4), np.float32)
    filled = np.zeros((size, size), bool)
    h, wd = source.shape[:2]
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
        tol = 1.5 / max(abs(den) ** 0.5, 1.0)
        hit = (l1 >= -tol) & (l2 >= -tol) & (l3 >= -tol) & ~filled[y0:y1, x0:x1]
        if not hit.any():
            continue
        w = np.clip(np.stack([l1[hit], l2[hit], l3[hit]], 1), 0, None)
        w /= w.sum(1, keepdims=True)
        uv = w @ s
        fx = np.clip(uv[:, 0] * wd - 0.5, 0, wd - 1.001)
        fy = np.clip(uv[:, 1] * h - 0.5, 0, h - 1.001)
        ix, iy = fx.astype(int), fy.astype(int)
        dx, dy = (fx - ix)[:, None], (fy - iy)[:, None]
        c = (source[iy, ix] * (1 - dx) * (1 - dy) + source[iy, ix + 1] * dx * (1 - dy) +
             source[iy + 1, ix] * (1 - dx) * dy + source[iy + 1, ix + 1] * dx * dy)
        dst[y0:y1, x0:x1][hit] = c
        filled[y0:y1, x0:x1] |= hit
    me.uv_layers.remove(me.uv_layers[old])
    print(f'[Head] own UV: {filled.mean() * 100:.0f} % of the texture under the hat')
    return dst


def set_view(a, tag):
    img = bpy.data.images.new(f'{name}_{tag}', TEXTURE_SIZE, TEXTURE_SIZE, alpha=True)
    img.pixels.foreach_set(a.ravel())
    tex_node.image = img


src_img = tex_node.image
if args.png:
    texture_shots('tex_model')  # исходная развёртка и текстура 4096
base = rebake(image_array(src_img), TEXTURE_SIZE)
S_ = TEXTURE_SIZE
# Пометка мимо полосы: у SunHead низ полосы под обручем сзади помечен и на тёмном клапане. Помеченная грань, где
# на текстуре тёмная ткань (средняя яркость ниже DARK_MARKED), снимается: серой она была бы пятном на клапане
DARK_MARKED = 0.3
lt_of, _, _, lt_poly = gear_texture.texel_map(me, S_)
px_face = lt_poly[np.maximum(lt_of, 0)][lt_of >= 0]
px_lum = (base[..., :3] @ np.array([0.299, 0.587, 0.114], np.float32))[lt_of >= 0]
order_ = np.argsort(px_face)
fa, fb = np.searchsorted(px_face[order_], np.arange(len(me.polygons))), \
    np.searchsorted(px_face[order_], np.arange(len(me.polygons)), side='right')
face_lum = np.array([np.median(px_lum[order_][a:b]) if b > a else 1.0 for a, b in zip(fa, fb)])
dark_marked = quality_poly & (face_lum < DARK_MARKED)
if dark_marked.any():
    C = np.array([tuple(me.polygons[i].center) for i in np.flatnonzero(dark_marked)])
    print(f'[Head] marked faces on dark cloth: {int(dark_marked.sum())} unmarked, at x {C[:, 0].min() * 1000:.0f}..'
          f'{C[:, 0].max() * 1000:.0f} y {C[:, 1].min() * 1000:.0f}..{C[:, 1].max() * 1000:.0f} '
          f'z {C[:, 2].min() * 1000:.0f}..{C[:, 2].max() * 1000:.0f} mm')
    quality_poly &= ~dark_marked
flat_normal = np.zeros((S_, S_, 4), np.float32)
flat_normal[...] = (0.5, 0.5, 1.0, 1.0)
rough = np.full((S_, S_, 4), 0.8, np.float32)
rough[..., 3] = 1.0
if args.png:
    set_view(base, 'own_uv')
    texture_shots('tex_before')

# ----------------------------------------------------------------------------------------------
# Текстура: чистка gear_texture.clean
# ----------------------------------------------------------------------------------------------
tcfg = dict(cfg['texture'], hard=hard_names)
# Колпак - не детали, даже где пиксели похожи на латунь: это кожа лица и уха модели у края колпака (у SunPants
# латунными гранями нашлись наколенники, здесь - край у уха, и кожа оставалась своим рисунком)
cap_poly = np.isin(poly_piece, cfg['texture']['fabric_pieces'])
cleaned_base, _, _ = gear_texture.clean(me, co0, poly_piece, quality_poly, base, flat_normal, rough, S_, tcfg,
                                        tag='[Head]', plain_poly=cap_poly)
# Ткань колпака вне полосы - рисунок модели: стёжка медной нитью (тонкие тёплые линии) и кремовые вставки под обручем
# сзади. Чистка ткани их стирает (сглаживание яркости, серое - только почти без цвета), поэтому там остаются пиксели
# модели, цвет наполовину к ткани комплекта, как у деталей. Чищеными остаются только пятна кожи: тёплые и толще
# стёжки или у края куска развёртки (тонкие полосы кожи лица и уха)
lt_of, _, _, lt_poly = gear_texture.texel_map(me, S_)
inside = lt_of >= 0
lt_pid = lt_poly[np.maximum(lt_of, 0)]
on_cap = inside & cap_poly[lt_pid] & ~quality_poly[lt_pid]
r_, g_, b_, lum_, sat_ = gear_texture.features(base[..., :3])
# кожа и стёжка - тёплые с насыщенностью от 0,3 (кожа около 0,4); у кремовых вставок 0,09-0,22, правило кожи
# gear_texture.skin_mask их тоже берёт
warm = on_cap & (r_ - b_ > 0.1) & (sat_ > 0.3) & (lum_ > 0.1)
inner = inside & cap_poly[lt_pid]
for _ in range(4):
    inner = inner & np.roll(inner, 1, 0) & np.roll(inner, -1, 0) & np.roll(inner, 1, 1) & np.roll(inner, -1, 1)
skin_spot = warm & (gear_texture.opening(warm, 2) | ~inner)
model_px = on_cap & ~skin_spot
navy = model_px & (b_ > r_) & (lum_ < 0.3)
gain = np.sqrt(np.array(tcfg['palette']['fabric'], np.float32) / np.median(base[navy, :3], 0))
cleaned_base[model_px, :3] = np.clip(base[model_px, :3] * gain, 0, 1)
cleaned_base[..., :3] = gear_texture.bleed(cleaned_base[..., :3].copy(), inside, 12)
print(f'[Head] cap cloth: model pixels {int(model_px.sum())}, skin spots repainted {int(skin_spot.sum())}, '
      f'colour gain {gain.round(2)}')
cls = gear_texture.DEBUG['cls']
inside = gear_texture.DEBUG['inside']
hard_px = gear_texture.DEBUG['hard']
# гладкость: латунь (детали в латуни и латунные пиксели ткани) блестит, ткань матовая
brass_px = inside & (hard_px & gear_texture.brass_mask(cleaned_base[..., :3], tcfg, lum_min=True) |
                     (cls == gear_texture.BRASS))
smooth = np.full((S_, S_), SMOOTHNESS['fabric'], np.float32)
smooth[brass_px] = SMOOTHNESS['brass']
tex_px = {'base': cleaned_base, 'normal': flat_normal}
set_view(cleaned_base, 'clean')
if args.png:
    texture_shots('tex_after')
if args.texture_only:
    print('[Head] texture only: done')
    sys.exit(0)

# ----------------------------------------------------------------------------------------------
# Подгонка: только отодвигание от кожи там, где убор ближе GAP
# ----------------------------------------------------------------------------------------------
edges = np.array([tuple(e.vertices) for e in me.edges])
me.calc_loop_triangles()
tris = [tuple(t.vertices) for t in me.loop_triangles]


def gap_report(co, what):
    d = np.array([dd for _, _, dd in nearest_skin(skin_bvh, co, 0.3)])
    print(f'[Head] {what}: over the skin min {d.min() * 1000:.1f} mm, 1 % {np.percentile(d, 1) * 1000:.1f}, '
          f'median {np.median(d) * 1000:.1f}; closer than {GAP * 1000:.0f} mm {int((d < GAP).sum())}, '
          f'inside deeper 1 mm {int((d < -0.001).sum())} of {len(d)}')
    return d


def skin_outside(co, what):
    """Кожа головы снаружи убора: луч от кожи внутрь находит убор (не дальше 15 мм) раньше, чем наружу."""
    tree = BVHTree.FromPolygons([Vector(p) for p in co], tris)
    found = {}
    for i, p in enumerate(body_rest):
        if main_bone[i] not in HEAD:
            continue
        n = Vector(body_normal[i])
        hin = tree.ray_cast(Vector(p), -n, 0.015)
        hout = tree.ray_cast(Vector(p), n, 0.1)
        if hin[0] is not None and (hout[0] is None or hin[3] < hout[3]):
            found[i] = hin[3]
    print(f'[Head] {what}: skin outside the hat {len(found)}' +
          ''.join(f'\n[Head]     {main_bone[i]} ({body_rest[i][0] * 1000:.0f} {body_rest[i][1] * 1000:.0f} '
                  f'{body_rest[i][2] * 1000:.0f}) depth {d * 1000:.1f}' for i, d in sorted(found.items(), key=lambda t: -t[1])[:10]))
    return found


co = co0.copy()
d0 = gap_report(co, 'as fitted')
for i in np.argsort(d0)[:12]:
    print(f'[Head]     v{i} {piece_names[piece[i]]} ({co[i][0] * 1000:.0f} {co[i][1] * 1000:.0f} {co[i][2] * 1000:.0f}) '
          f'{d0[i] * 1000:.1f} mm')
skin_outside(co, 'as fitted')
nb = neighbours(edges, list(range(len(co))))
for it in range(5):
    near = nearest_skin(skin_bvh, co, 0.3)
    need = np.array([max(0.0, GAP - dd) for _, _, dd in near])
    worst = need.max()
    if worst < 1e-5:
        break
    normals = np.array([n for _, n, _ in near])
    need = np.array([max([need[k]] + [need[j] for j in n_]) for k, n_ in enumerate(nb)])  # не меньше соседей
    for _ in range(3):
        need = 0.5 * need + 0.5 * average(need, nb)
    co += normals * need[:, None]
    print(f'[Head] pass {it + 1}: closer to the skin than {GAP * 1000:.0f} mm up to {worst * 1000:.1f} mm')
push_faces_off_skin(co, tris, skin_bvh, FACE_GAP, tag='[Head]', what='hat faces')
moved = np.linalg.norm(co - co0, axis=1)
print(f'[Head] moved off the skin: {int((moved > 1e-5).sum())} verts, over 1 mm {int((moved > 0.001).sum())}, '
      f'up to {moved.max() * 1000:.1f} mm')
for k in range(len(comps)):
    m = piece == k
    if moved[m].max() > 0.0005:
        print(f'[Head]   {piece_names[k]}: moved {int((moved[m] > 0.0005).sum())} verts up to {moved[m].max() * 1000:.1f} mm')
gap_report(co, 'placed')
skin_outside(co, 'placed')
me.vertices.foreach_set('co', co.ravel())
me.update()
hat_bvh = BVHTree.FromPolygons([Vector(p) for p in co], tris)

# ----------------------------------------------------------------------------------------------
# Веса с кожи; обруч и лучи - целиком на голове
# ----------------------------------------------------------------------------------------------
for vg in list(hat.vertex_groups):
    hat.vertex_groups.remove(vg)
for g in body.vertex_groups:
    hat.vertex_groups.new(name=g.name)
transfer = hat.modifiers.new('WeightTransfer', 'DATA_TRANSFER')
transfer.object = body
transfer.use_vert_data = True
transfer.data_types_verts = {'VGROUP_WEIGHTS'}
transfer.vert_mapping = 'POLYINTERP_NEAREST'
transfer.layers_vgroup_select_src = 'ALL'
transfer.layers_vgroup_select_dst = 'NAME'
bpy.ops.object.select_all(action='DESELECT')
hat.select_set(True)
bpy.context.view_layer.objects.active = hat
bpy.ops.object.modifier_apply(modifier=transfer.name)
# латунь не гнётся: с весами кожи у затылка (Neck) обруч гнулся бы с шеей
head_g = hat.vertex_groups['Head']
rigid = [v.index for v in me.vertices if piece_names[piece[v.index]] in hard_names]
for vg in hat.vertex_groups:
    vg.remove(rigid)
head_g.add(rigid, 1.0, 'REPLACE')
limit_bones(hat)
bad = [v.index for v in me.vertices if not v.groups]
if bad or max(len(v.groups) for v in me.vertices) > SKIN_BONES:
    raise RuntimeError(f'{name}: bad weights ({len(bad)} vertices without bones)')
used = {}
for v in me.vertices:
    for g in v.groups:
        if g.weight > 0:
            nm = hat.vertex_groups[g.group].name
            used[nm] = used.get(nm, 0) + 1
print(f'[Head] bones (verts): {used}')
hat.parent = arm_obj
hat.modifiers.new('Armature', 'ARMATURE').object = arm_obj

# ----------------------------------------------------------------------------------------------
# Маска: кожа головы под убором, которую не видно снаружи; только треугольники, целиком закрытые убором
# ----------------------------------------------------------------------------------------------
dirs = sphere_dirs()
REACH = 0.06


def covered_at(p, n):
    """Над точкой кожи по нормали есть убор, и снаружи её не видно (вниз круче 30° камера не смотрит)."""
    p, n = Vector(p), Vector(n)
    if hat_bvh.ray_cast(p + n * 0.0005, n, REACH)[0] is None:
        return False
    return not seen_from_outside(p, n, dirs, (hat_bvh, skin_bvh), max_slope=0.5, up_slope=1.01)


cand = {i for i, p in enumerate(body_rest) if main_bone[i] in HEAD and p[2] > 0.92 and
        hat_bvh.ray_cast(Vector(p) + Vector(body_normal[i]) * 0.0005, Vector(body_normal[i]), REACH)[0] is not None}
under = {i for i in cand if covered_at(body_rest[i], body_normal[i])}
tris_ok = covered_triangles(body, body_rest, body_normal, lambda i: i in under, covered_at)
hid, n_add, n_loose = add_covered(body, set(), tris_ok)
hidden = sorted(hid)
print(f'[Head] head skin under the hat {len(cand)}, not seen from outside {len(under)}; triangles fully covered '
      f'{len(tris_ok)}, hidden verts {len(hidden)} (left visible {n_loose})')
body.data.calc_loop_triangles()


def mask_of(part):
    path = os.path.abspath(GEAR + part + '_BodyMask.json')
    if not os.path.exists(path):
        return set()
    with open(path, encoding='utf-8') as f:
        pts = np.array(json.load(f)['hidden']).reshape(-1, 3)
    return {int(np.argmin(((body_rest - p) ** 2).sum(1))) for p in pts}


# WornGear прячет треугольник, если все его вершины под надетыми частями вместе: с нагрудником у шеи не должно
# спрятаться лишнего
for chest in ('SunChest', 'NomadChest', 'ClothChest'):
    other = mask_of(chest)
    both = [lt for lt in body.data.loop_triangles if set(lt.vertices) <= (hid | other) and
            not set(lt.vertices) <= hid and not set(lt.vertices) <= other]
    print(f'[Head] with {chest}: triangles hidden only together {len(both)}')

# ----------------------------------------------------------------------------------------------
# Снимки на теле
# ----------------------------------------------------------------------------------------------


def worn(part):
    """Надетая часть в T-позе на скелете тела (свой скелет из FBX удаляется). Текстура цвета - для снимков."""
    new = import_new(GEAR + part + '.fbx')
    mesh = next(o for o in new if o.type == 'MESH')
    mw = mesh.matrix_world.copy()
    mesh.parent = arm_obj
    mesh.matrix_world = mw
    mesh.modifiers[0].object = arm_obj
    for o in new:
        if o.type == 'ARMATURE':
            bpy.data.objects.remove(o)
    mat = bpy.data.materials.new(part)
    mat.use_nodes = True
    node = mat.node_tree.nodes.new('ShaderNodeTexImage')
    node.image = bpy.data.images.load(os.path.abspath(os.path.join(GEAR + part + '_Textures', part + '.png')))
    mat.node_tree.links.new(node.outputs['Color'], mat.node_tree.nodes['Principled BSDF'].inputs['Base Color'])
    mesh.data.materials.clear()
    mesh.data.materials.append(mat)
    return mesh


if args.png:
    chest = worn(cfg['chest'])
    chest_hidden = mask_of(cfg['chest'])
    skin_mat = bpy.data.materials.new('Skin')
    skin_mat.use_nodes = True
    node = skin_mat.node_tree.nodes.new('ShaderNodeTexImage')
    node.image = bpy.data.images.load(os.path.abspath(
        'Assets/Models/20261121003938_8b8745d5_Textures/texture_pbr_20250901.png'))
    skin_mat.node_tree.links.new(node.outputs['Color'], skin_mat.node_tree.nodes['Principled BSDF'].inputs['Base Color'])
    bm2 = bmesh.new()
    bm2.from_mesh(body.data)
    bmesh.ops.delete(bm2, geom=[f for f in bm2.faces if all(v.index in (hid | chest_hidden) for v in f.verts)],
                     context='FACES_ONLY')
    bm2.to_mesh(body.data)
    bm2.free()
    body.data.materials.clear()
    body.data.materials.append(skin_mat)
    ALL = ('front', 'back', 'left', 'right', 'top', 'persp', 'persp_back', 'below', 'below_back', 'game')
    show(body, hat, chest)
    render('on', HEAD_C, 0.2, ALL)
    render('ear', (0.05, 0.01, 0.99), 0.08, ('left', 'below', 'front'))
    render('rear', (-0.05, 0.01, 0.99), 0.08, ('right',))
    render('nape', (0, 0.05, 0.97), 0.1, ('back', 'below_back', 'left'))
    show(hat)
    render('hat_only', HEAD_C, 0.2, ('front', 'back', 'left', 'below'))
    show(body, chest)
    render('mask_only', HEAD_C, 0.2, ('front', 'back', 'left', 'top'))
    show(body, hat, chest)
    for pose in [p for p in RECORDED if p.split(':')[1].split('@')[0] in
                 ('idle', 'walk', 'run', 'jump', 'aim', 'aim_strafe', 'aim_up', 'aim_down', 'reload')][::6]:
        set_pose(arm_obj, pose)
        hd = arm_obj.pose.bones['Head'].head
        tag = pose.replace(':', '_').replace('@', '_')
        render(tag, Vector((hd.x, hd.y, hd.z + 0.05)), 0.24, ('front', 'left', 'game', 'below_back'))
    set_pose(arm_obj, 'tpose')

# ----------------------------------------------------------------------------------------------
# Маска, текстуры, экспорт
# ----------------------------------------------------------------------------------------------
os.makedirs(os.path.dirname(out), exist_ok=True)
with open(stem + '_BodyMask.json', 'w', encoding='utf-8') as f:
    json.dump({'part': 'head', 'bodyVertices': len(body_rest),
               'hidden': [round(float(c), 6) for i in hidden for c in body_rest[i]]}, f)
print(f'[Head] mask {stem}_BodyMask.json')
tex_dir = stem + '_Textures'
os.makedirs(tex_dir, exist_ok=True)


def save(a, file_name, colorspace):
    img = bpy.data.images.new(file_name, TEXTURE_SIZE, TEXTURE_SIZE, alpha=True)
    img.colorspace_settings.name = colorspace
    img.pixels.foreach_set(a.ravel())
    img.filepath_raw = os.path.join(tex_dir, file_name)
    img.file_format = 'PNG'
    img.save()
    print(f'[Head] texture {img.filepath_raw}')


save(tex_px['base'], f'{name}.png', 'sRGB')
save(tex_px['normal'], f'{name}_normal.png', 'Non-Color')
ms = np.zeros((S_, S_, 4), np.float32)  # металла у модели нет, гладкость - по классу
ms[..., 3] = gear_texture.bleed(smooth[..., None].repeat(3, -1), inside, BLEED)[..., 0]
save(ms, f'{name}_metallic_smoothness.png', 'Non-Color')
q = gear_texture.face_mask(me, quality_poly, S_)
save((np.repeat(q.reshape(-1, 1), 4, 1) * [1, 1, 1, 0] + [0, 0, 0, 1]).astype(np.float32), f'{name}_quality.png',
     'Non-Color')

for o in [o for o in bpy.data.objects if o.type == 'MESH' and o is not hat]:  # тело, нагрудник для снимков
    bpy.data.objects.remove(o)
hat.matrix_world = Matrix.Identity(4)
me.materials[0].name = 'Material'
for m in bpy.data.materials:
    m.use_nodes = False
bpy.ops.object.select_all(action='DESELECT')
arm_obj.select_set(True)
hat.select_set(True)
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
print(f'[Head] exported {out}, verts {len(me.vertices)}')
