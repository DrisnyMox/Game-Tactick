"""
Ставит верх, который пользователь вырезал из модели генератора и сам подогнал под тело игрока
Assets/Models/20261121003938_8b8745d5.fbx, на его скелет. Первый - нагрудник Культа Солнца SunChest.

Запуск (из корня проекта):
    "E:\\Software\\Blender\\blender.exe" --background --factory-startup --python Tools/Blender/place_chest_fitted.py -- ^
        --part sun --src "<FBX верха>" --out Assets/Models/Gear/SunChest.fbx [--png "<папка>"] [--texture-only]

Форма - пользователя, скриптом заново не сажается (в отличие от place_chest.py). Что делается:
  * координаты: вершины в FBX уже в единицах тела (матрица объекта из Max - масштаб 6,45 и поворот - отбрасывается),
    только Y вверх -> Z вверх;
  * куски сетки - по связности (p00 - самый большой, ткань), материалы сводятся в один; грани, помеченные
    пользователем материалом quality_material, - маска цвета качества;
  * текстура чистится gear_texture.clean (как у SunPants: класс пикселя по цвету, перекраска в цвета картинки,
    узор солнц заново). С --png снимки до и после; --texture-only - только они, без посадки и экспорта;
  * подгонка пользователя проверяется и правится только там, где нужно игре: вершины ближе GAP к коже отодвигаются
    по нормали кожи, не меньше соседей и сглаженно (как place_gloves.py); точки внутри граней - не ближе FACE_GAP
    (gear_fit.push_faces_off_skin);
  * веса - с ближайшей кожи тела, SKIN_BONES кости на вершину; у наплечника и ткани половина веса плеча руки отдана
    ключице (ARM_SHARE, как у NomadChest), ремень на руке (кусок почти весь на UpperArm) остаётся на руке;
  * маска: кожа торса, над которой по нормали есть верх и которую не видно снаружи (лучи во все стороны, кроме круче
    30° вниз; закрывают только верх и тело - верх надевается и без штанов).
С --png рисует верх на теле в T-позе и в позах игры со штанами pants и без, вблизи - шею, плечи, пояс.
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
from gear_fit import (average, nearest_skin, neighbours, push_faces_off_skin, seen_from_outside,  # noqa: E402
                      sphere_dirs, world_bm)
from gear_poses import RECORDED, SKIN_BONES, limit_bones, set_pose  # noqa: E402

BODY_PATH = 'Assets/Models/20261121003938_8b8745d5.fbx'
GEAR = 'Assets/Models/Gear/'
PARTS = {
    # Культ Солнца: жилет из индиго-чёрной ткани, серые ремни крест-накрест (спереди и сзади на дисках), серый
    # кушак с узлом и двумя ленточками сзади, латунный ворот из пластин, латунные диски на груди и спине, наплечник
    # с решёткой солнечной панели на левом плече с ремнём вокруг руки, медные провода от диска к наплечнику, подвески
    # на кожаных шнурах. Ремни, кушак и ленточки пользователь пометил материалом '01 - Default'
    'sun': dict(quality_material='01 - Default', pants='SunPants', boots='NomadBoots', gloves='NomadGloves',
                # серое без пометки - только окантовка по краю (подол, проймы); дальше от края светлое - мусор
                # генератора (пятно посреди спины над кушаком)
                grey_edge=0.022,
                texture=dict(
                    # цвета - как у SunPants (один комплект): ткань, серое, латунь, ремень, медь, солнца
                    palette=dict(fabric=(0.165, 0.178, 0.215), grey=(0.67, 0.645, 0.62), brass=(0.62, 0.49, 0.31),
                                 leather=(0.31, 0.224, 0.14), copper=(0.72, 0.36, 0.16), sun=(0.33, 0.35, 0.42)),
                    shade=dict(fabric=(2.5, 0.75, 1.2), grey=(0.8, 0.5, 1.35), brass=(0.8, 0.5, 1.4),
                               leather=(1.0, 0.6, 1.3), copper=(0.5, 0.5, 1.4)),
                    # латунь у модели бледная (0,58 0,47 0,36): правило кожи SunPants принимало её за кожу, а кожи
                    # персонажа на этой текстуре нет
                    skin=False, brass_rule=(0.05, 0.12, 0.30, 0.30),
                    # на ткани латуни и меди нет: латунный и рыжий пиксель там - царапина
                    soft_classes=('fabric', 'grey'),
                    hard=(),  # заполняется ниже: все куски, кроме ткани
                    panel=(), fabric_pieces=('p00',),
                    # окна в латуни (середина дисков на груди и спине, окошки ворота) остаются рисунком: середина диска
                    # на груди - 50 см², с 0,003 (как у наколенников SunPants) она перекрашивалась в ткань
                    brass_share=0.25, window_area=0.008, behind_reach=0.03, sun_clear=0.022,
                    sun_radius=0.012, sun_spacing=0.042, sun_alpha=0.7)),
}
GAP = 0.002          # вершина верха не ближе к коже, м (рост тела 1,088; в Unity умножается на 1,662)
FACE_GAP = 0.001     # точка внутри грани не ближе к коже
PANTS_GAP = 0.0015   # верх над штанами: пояс штанов под подолом
ARM_SHARE = 0.5      # доля веса руки у наплечника и ткани, остальное - ключице (как у NomadChest)
TORSO = {'Hips', 'Spine', 'Chest', 'Neck', 'LeftShoulder', 'RightShoulder'}
HIDE_BONES = TORSO
TEXTURE_SIZE = 2048

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
body_edges = np.array([tuple(e.vertices) for e in body.data.edges])
bbm = world_bm(body)
body_normal = np.array([tuple(v.normal) for v in bbm.verts])
skin_bvh = BVHTree.FromBMesh(bbm)
bbm.free()

# ----------------------------------------------------------------------------------------------
# Верх: координаты, куски, пометка, один материал
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
top = meshes[0]
top.name = name
me = top.data

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
    print(f'[Chest] piece p{k:02d}: {len(P)} verts, x {P[:, 0].min():.3f}..{P[:, 0].max():.3f} '
          f'y {P[:, 1].min():.3f}..{P[:, 1].max():.3f} z {P[:, 2].min():.3f}..{P[:, 2].max():.3f}')
poly_piece = np.array(piece_names)[piece[[p.vertices[0] for p in me.polygons]]]
mat_names = [m.name if m else '' for m in me.materials]
# у пометки может быть несколько слотов одного материала (у SunChest - три) и суффикс .001 от импорта
quality_poly = np.array([mat_names[p.material_index].split('.')[0] == cfg['quality_material'] for p in me.polygons])
print(f'[Chest] materials {mat_names}; marked faces {int(quality_poly.sum())} of {len(me.polygons)}')
tex_slot = next(i for i, m in enumerate(me.materials)
                if m and m.node_tree and any(n.type == 'TEX_IMAGE' for n in m.node_tree.nodes))
for k in reversed(range(len(me.materials))):
    if k != tex_slot:
        me.materials.pop(index=k)
for p in me.polygons:
    p.material_index = 0
tex_nodes = {n.image.name: n for n in me.materials[0].node_tree.nodes if n.type == 'TEX_IMAGE' and n.image}
base = next(n.image for k, n in tex_nodes.items() if not any(s in k for s in ('normal', 'roughness', 'metallic')))
normal = next(n.image for k, n in tex_nodes.items() if 'normal' in k)
roughness = next(n.image for k, n in tex_nodes.items() if 'roughness' in k)


def pixels(img):
    img = img.copy()
    img.scale(TEXTURE_SIZE, TEXTURE_SIZE)
    a = np.empty(TEXTURE_SIZE * TEXTURE_SIZE * 4, np.float32)
    img.pixels.foreach_get(a)
    bpy.data.images.remove(img)
    return a.reshape(-1, 4)


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
         'below': (0.1, -0.5, -0.8), 'below_back': (0.2, 0.8, -0.35), 'game': (0.15, 0.9, 0.42)}


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
    show(top)
    render(prefix, (0.05, 0.01, 0.79), 0.42, ('front', 'back', 'left', 'right', 'persp_l'))
    render(prefix + '_close_front', (0.0, -0.05, 0.78), 0.22, ('front',))
    render(prefix + '_close_back', (0.0, 0.05, 0.75), 0.22, ('back',))
    render(prefix + '_shoulder', (0.13, 0.03, 0.88), 0.2, ('top', 'persp_l', 'back'))


# ----------------------------------------------------------------------------------------------
# Текстура: чистка gear_texture.clean
# ----------------------------------------------------------------------------------------------
tex_px = {'base': pixels(base), 'normal': pixels(normal), 'roughness': pixels(roughness)}
# Пометка мимо ремня: мелкий кусок помеченных граней (меньше STRAY_FACES) на тёмной ткани. У SunChest - одна грань
# спереди у ворота и пять сзади у ворота справа: серыми после чистки они были бы пятнами на ткани
STRAY_FACES = 10
uvd = me.uv_layers.active.data
base_img = tex_px['base'].reshape(TEXTURE_SIZE, TEXTURE_SIZE, 4)


def face_luma(p):
    u, v = np.mean([tuple(uvd[li].uv) for li in p.loop_indices], 0)
    c = base_img[min(int(v * TEXTURE_SIZE), TEXTURE_SIZE - 1), min(int(u * TEXTURE_SIZE), TEXTURE_SIZE - 1), :3]
    return float(c @ [0.299, 0.587, 0.114])


faces_of = {}
for p in me.polygons:
    for ek in p.edge_keys:
        faces_of.setdefault(ek, []).append(p.index)
seen_q = np.zeros(len(me.polygons), bool)
for s in np.flatnonzero(quality_poly):
    if seen_q[s]:
        continue
    stack, patch = [s], []
    seen_q[s] = True
    while stack:
        a = stack.pop()
        patch.append(a)
        for ek in me.polygons[a].edge_keys:
            for b in faces_of[ek]:
                if quality_poly[b] and not seen_q[b]:
                    seen_q[b] = True
                    stack.append(b)
    if len(patch) < STRAY_FACES:
        lum_ = np.median([face_luma(me.polygons[i]) for i in patch])
        c = np.mean([tuple(me.polygons[i].center) for i in patch], 0)
        stray = lum_ < 0.35
        print(f'[Chest] small marked patch {len(patch)} faces at ({c[0] * 1000:.0f} {c[1] * 1000:.0f} {c[2] * 1000:.0f}), '
              f'texture luma {lum_:.2f}: {"unmarked (dark cloth)" if stray else "kept"}')
        if stray:
            quality_poly[patch] = False
if args.png:
    view0 = bpy.data.images.new(name + '_model', TEXTURE_SIZE, TEXTURE_SIZE, alpha=True)
    view0.pixels.foreach_set(tex_px['base'].ravel())
    tex_nodes[base.name].image = view0
    texture_shots('tex_before')
tcfg = dict(cfg['texture'], hard=tuple(n for n in piece_names if n != 'p00'))
# серое без пометки - у края ткани (окантовка подола и пройм)
bm = bmesh.new()
bm.from_mesh(me)
edge_pts = np.array([tuple(v.co) for e in bm.edges if len(e.link_faces) == 1 and piece[e.verts[0].index] == 0
                     for v in e.verts])
bm.free()
from mathutils.kdtree import KDTree  # noqa: E402
kd_edge = KDTree(len(edge_pts))
for i, p in enumerate(edge_pts):
    kd_edge.insert(Vector(p), i)
kd_edge.balance()
grey_poly = np.array([kd_edge.find(p.center)[2] < cfg['grey_edge'] for p in me.polygons])
S_ = TEXTURE_SIZE
cleaned = gear_texture.clean(me, co0, poly_piece, quality_poly, *(tex_px[k].reshape(S_, S_, 4) for k in
                             ('base', 'normal', 'roughness')), S_, tcfg, tag='[Chest]', grey_poly=grey_poly)
tex_px = {k: a.reshape(-1, 4) for k, a in zip(('base', 'normal', 'roughness'), cleaned)}
view1 = bpy.data.images.new(name + '_clean', S_, S_, alpha=True)
view1.pixels.foreach_set(tex_px['base'].ravel())
tex_nodes[base.name].image = view1
if args.png:
    texture_shots('tex_after')
if args.texture_only:
    print('[Chest] texture only: done')
    sys.exit(0)

# ----------------------------------------------------------------------------------------------
# Подгонка: только отодвигание от кожи там, где верх ближе GAP
# ----------------------------------------------------------------------------------------------
edges = np.array([tuple(e.vertices) for e in me.edges])
me.calc_loop_triangles()
tris = [tuple(t.vertices) for t in me.loop_triangles]


def gap_report(co, what):
    d = np.array([dd for _, _, dd in nearest_skin(skin_bvh, co, 0.3)])
    print(f'[Chest] {what}: over the skin min {d.min() * 1000:.1f} mm, 1 % {np.percentile(d, 1) * 1000:.1f}, '
          f'median {np.median(d) * 1000:.1f}; closer than {GAP * 1000:.0f} mm {int((d < GAP).sum())}, '
          f'inside deeper 1 mm {int((d < -0.001).sum())} of {len(d)}')
    return d


def skin_outside(co, what):
    """Кожа торса снаружи верха: луч от кожи внутрь находит верх раньше, чем наружу."""
    tree = BVHTree.FromPolygons([Vector(p) for p in co], tris)
    found = {}
    for i, p in enumerate(body_rest):
        if p[2] < 0.6 or main_bone[i] == 'Head':
            continue
        n = Vector(body_normal[i])
        hin = tree.ray_cast(Vector(p), -n, 0.03)
        hout = tree.ray_cast(Vector(p), n, 0.08)
        if hin[0] is not None and (hout[0] is None or hin[3] < hout[3]):
            found[i] = hin[3]
    print(f'[Chest] {what}: skin outside the top {len(found)}' +
          ''.join(f'\n[Chest]     {main_bone[i]} ({body_rest[i][0] * 1000:.0f} {body_rest[i][1] * 1000:.0f} '
                  f'{body_rest[i][2] * 1000:.0f}) depth {d * 1000:.1f}' for i, d in sorted(found.items(), key=lambda t: -t[1])[:10]))
    return found


co = co0.copy()
d0 = gap_report(co, 'as fitted')
for i in np.argsort(d0)[:12]:
    print(f'[Chest]     v{i} {piece_names[piece[i]]} ({co[i][0] * 1000:.0f} {co[i][1] * 1000:.0f} {co[i][2] * 1000:.0f}) '
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
    print(f'[Chest] pass {it + 1}: closer to the skin than {GAP * 1000:.0f} mm up to {worst * 1000:.1f} mm')
push_faces_off_skin(co, tris, skin_bvh, FACE_GAP, tag='[Chest]', what='top faces')
moved = np.linalg.norm(co - co0, axis=1)
print(f'[Chest] moved off the skin: {int((moved > 1e-5).sum())} verts, over 1 mm {int((moved > 0.001).sum())}, '
      f'up to {moved.max() * 1000:.1f} mm')
for k in range(len(comps)):
    m = piece == k
    if moved[m].max() > 0.0005:
        print(f'[Chest]   {piece_names[k]}: moved {int((moved[m] > 0.0005).sum())} verts up to {moved[m].max() * 1000:.1f} mm')
gap_report(co, 'placed')
skin_outside(co, 'placed')
me.vertices.foreach_set('co', co.ravel())
me.update()

# ----------------------------------------------------------------------------------------------
# Штаны комплекта: пояс под подолом (сам пояс прижимает place_pants.py, здесь - проверка)
# ----------------------------------------------------------------------------------------------


def worn(part):
    """Надетая часть в T-позе на скелете тела (свой скелет из FBX удаляется). Текстура цвета - для снимков."""
    path = GEAR + part + '.fbx'
    new = import_new(path)
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


def mask_of(part):
    with open(os.path.abspath(GEAR + part + '_BodyMask.json'), encoding='utf-8') as f:
        pts = np.array(json.load(f)['hidden']).reshape(-1, 3)
    return {int(np.argmin(((body_rest - p) ** 2).sum(1))) for p in pts}


pants = worn(cfg['pants'])
others = [worn(cfg['boots']), worn(cfg['gloves'])]
pbm = world_bm(pants)
pants_co = np.array([tuple(v.co) for v in pbm.verts])
pants_normal = np.array([tuple(v.normal) for v in pbm.verts])
pbm.free()
top_bvh = BVHTree.FromPolygons([Vector(p) for p in co], tris)
bm = bmesh.new()
bm.from_mesh(me)
hem = np.array([tuple(v.co) for e in bm.edges if len(e.link_faces) == 1 and piece[e.verts[0].index] == 0
                for v in e.verts if v.co.z < 0.70])
bm.free()


def hem_z(p):
    a = math.atan2(p[0], -p[1])
    ang = np.arctan2(hem[:, 0], -hem[:, 1])
    d = np.abs((ang - a + np.pi) % (2 * np.pi) - np.pi)
    return hem[np.argsort(d)[:12], 2].min()


above = [i for i, p in enumerate(pants_co) if p[2] > 0.55 and p[2] > hem_z(p)]
inside_top = [i for i in above if top_bvh.ray_cast(Vector(pants_co[i]) + Vector(pants_normal[i]) * 0.0005,
                                                    Vector(pants_normal[i]), 0.08)[0] is not None]
print(f'[Chest] {cfg["pants"]}: top of the pants z up to {pants_co[:, 2].max():.3f}, hem of the top z '
      f'{hem[:, 2].min():.3f}..{hem[:, 2].max():.3f}; pants verts above the hem {len(above)}, under the top {len(inside_top)}')
pants_top_by_side = {}
for p in pants_co:
    k = int(round(math.degrees(math.atan2(p[0], -p[1])) / 45)) % 8
    pants_top_by_side[k] = max(pants_top_by_side.get(k, 0), p[2])
print('[Chest]   by side (front, front-left, left, ...): pants top / hem ' +
      ', '.join(f'{pants_top_by_side[k]:.3f}/{hem_z((math.sin(math.radians(k * 45)), -math.cos(math.radians(k * 45)), 0)):.3f}'
                for k in range(8)))

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
# Ремень вокруг руки (почти весь на плече руки) идёт за рукой. Остальное (наплечник, ткань у пройм) лежит на плечах:
# часть ARM_SHARE веса руки отдаётся ключице - у NomadChest с полным весом руки накидка у плеча рвалась в прицеле
arm_main = np.array([max(v.groups, key=lambda g: g.weight).group if v.groups else -1 for v in me.vertices])
arm_ids = {top.vertex_groups[s + 'UpperArm'].index for s in ('Left', 'Right')}
on_arm = [k for k in range(len(comps)) if np.isin(arm_main[piece == k], list(arm_ids)).mean() > 0.9]
print(f'[Chest] pieces on the arm (weights kept): {[piece_names[k] for k in on_arm]}')
for side in ('Left', 'Right'):
    arm_g, sh_g = top.vertex_groups[side + 'UpperArm'], top.vertex_groups[side + 'Shoulder']
    for v in me.vertices:
        if piece[v.index] in on_arm:
            continue
        w = next((g.weight for g in v.groups if g.group == arm_g.index), 0.0)
        if w > 0:
            arm_g.add([v.index], w * ARM_SHARE, 'REPLACE')
            sh_g.add([v.index], w * (1 - ARM_SHARE), 'ADD')
limit_bones(top)
bad = [v.index for v in me.vertices if not v.groups]
if bad or max(len(v.groups) for v in me.vertices) > SKIN_BONES:
    raise RuntimeError(f'{name}: bad weights ({len(bad)} vertices without bones)')
used = {top.vertex_groups[g.group].name for v in me.vertices for g in v.groups if g.weight > 0}
print(f'[Chest] bones: {sorted(used)}')
top.parent = arm_obj
top.modifiers.new('Armature', 'ARMATURE').object = arm_obj

# ----------------------------------------------------------------------------------------------
# Маска: кожа торса под верхом, которую не видно снаружи
# ----------------------------------------------------------------------------------------------
dirs = sphere_dirs()
covered = [i for i, p in enumerate(body_rest) if main_bone[i] in HIDE_BONES and
           top_bvh.ray_cast(Vector(p) + Vector(body_normal[i]) * 0.0005, Vector(body_normal[i]), 0.08)[0] is not None]
# лучи вверх берутся (горловина и проймы смотрят вверх), вниз круче 30° - нет: камера не смотрит снизу под подол
under = {i for i in covered if not seen_from_outside(body_rest[i], body_normal[i], dirs, (top_bvh, skin_bvh),
                                                     max_slope=0.5, up_slope=1.01)}
kept = {}
for i in set(covered) - under:
    key = (main_bone[i], round(body_rest[i][2] * 50) / 50)
    kept[key] = kept.get(key, 0) + 1
print('[Chest] kept (seen through openings): ' + ', '.join(f'{b} {z:.2f}:{n}' for (b, z), n in sorted(kept.items())))
nbrs = {}
for a, b in body_edges:
    nbrs.setdefault(a, set()).add(b)
    nbrs.setdefault(b, set()).add(a)
filled = {i for i in range(len(body_rest)) if i not in under and main_bone[i] in {'Hips', 'Spine', 'Chest'} and
          nbrs[i] <= under}
under |= filled
hidden = sorted(under)
hid = set(hidden)
print(f'[Chest] torso skin covered by the top: {len(covered)}, kept {len(covered) - len(under) + len(filled)}, '
      f'filled {len(filled)}, hidden {len(hidden)}')
body.data.calc_loop_triangles()
pants_hidden = mask_of(cfg['pants'])
bare = [lt for lt in body.data.loop_triangles
        if all(main_bone[i] in {'Hips', 'Spine'} for i in lt.vertices) and
        not (set(lt.vertices) <= hid or set(lt.vertices) <= pants_hidden)]
print(f'[Chest] hips and spine triangles not hidden by the top or {cfg["pants"]}: {len(bare)}')

# ----------------------------------------------------------------------------------------------
# Снимки на теле
# ----------------------------------------------------------------------------------------------
if args.png:
    skin_mat = bpy.data.materials.new('Skin')
    skin_mat.diffuse_color = (0.85, 0.62, 0.52, 1)
    other_hidden = [mask_of(cfg['boots']), mask_of(cfg['gloves'])]
    body_np = body.copy()
    body_np.data = body.data.copy()
    scene.collection.objects.link(body_np)
    for o, sets in ((body, [hid, pants_hidden] + other_hidden), (body_np, [hid] + other_hidden)):
        bm2 = bmesh.new()
        bm2.from_mesh(o.data)
        bmesh.ops.delete(bm2, geom=[f for f in bm2.faces if any(all(v.index in s for v in f.verts) for s in sets)],
                         context='FACES_ONLY')
        bm2.to_mesh(o.data)
        bm2.free()
        o.data.materials.clear()
        o.data.materials.append(skin_mat)
    show(body, top, pants, *others)
    ALL = ('front', 'back', 'left', 'right', 'top', 'persp', 'persp_back', 'below', 'below_back', 'game')
    render('body', (0.03, 0, 0.74), 0.6, ALL)
    render('neck', (0, 0.02, 0.9), 0.2, ('front', 'back', 'left', 'top', 'game'))
    render('shoulder', (0.14, 0.03, 0.87), 0.22, ('front', 'back', 'left', 'top', 'below', 'persp_l'))
    render('rshoulder', (-0.12, 0.02, 0.86), 0.2, ('front', 'back', 'right', 'below'))
    render('waist', (0, 0.01, 0.64), 0.3, ('front', 'back', 'left', 'right', 'below', 'below_back', 'game'))
    show(body_np, top, *others)
    render('nopants', (0, 0, 0.68), 0.45, ('front', 'back', 'left', 'below', 'game'))
    show(body, top, pants, *others)
    for pose in [p for p in RECORDED if p.split(':')[1].split('@')[0] in
                 ('idle', 'walk', 'run', 'jump', 'aim', 'aim_strafe', 'aim_up', 'aim_down', 'reload')][::5]:
        set_pose(arm_obj, pose)
        hips = arm_obj.pose.bones['Hips'].head
        tag = pose.replace(':', '_').replace('@', '_')
        render(tag, Vector((hips.x, hips.y, 0.75)), 0.6, ('front', 'left', 'game', 'persp_back'))
        ch = arm_obj.pose.bones['Chest'].head
        render('close_' + tag, Vector((ch.x + 0.05, ch.y, ch.z + 0.1)), 0.32, ('front', 'persp_l', 'game', 'top'))
    set_pose(arm_obj, 'tpose')

# ----------------------------------------------------------------------------------------------
# Маска, текстуры, экспорт
# ----------------------------------------------------------------------------------------------
os.makedirs(os.path.dirname(out), exist_ok=True)
with open(stem + '_BodyMask.json', 'w', encoding='utf-8') as f:
    json.dump({'part': 'chest', 'bodyVertices': len(body_rest),
               'hidden': [round(float(c), 6) for i in hidden for c in body_rest[i]]}, f)
print(f'[Chest] mask {stem}_BodyMask.json')
tex_dir = stem + '_Textures'
os.makedirs(tex_dir, exist_ok=True)


def save(a, file_name, colorspace):
    img = bpy.data.images.new(file_name, TEXTURE_SIZE, TEXTURE_SIZE, alpha=True)
    img.colorspace_settings.name = colorspace
    img.pixels.foreach_set(a.ravel())
    img.filepath_raw = os.path.join(tex_dir, file_name)
    img.file_format = 'PNG'
    img.save()
    print(f'[Chest] texture {img.filepath_raw}')


save(tex_px['base'], f'{name}.png', 'sRGB')
save(tex_px['normal'], f'{name}_normal.png', 'Non-Color')
ms = np.zeros((TEXTURE_SIZE * TEXTURE_SIZE, 4), np.float32)  # металла у модели нет
ms[:, 3] = 1.0 - tex_px['roughness'][:, 0]
save(ms, f'{name}_metallic_smoothness.png', 'Non-Color')
q = gear_texture.face_mask(me, quality_poly, TEXTURE_SIZE)
save((np.repeat(q.reshape(-1, 1), 4, 1) * [1, 1, 1, 0] + [0, 0, 0, 1]).astype(np.float32), f'{name}_quality.png',
     'Non-Color')

for o in [body, pants] + others + [o for o in bpy.data.objects if o.name.startswith('Body.')]:
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
