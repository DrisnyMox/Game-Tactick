"""
Вырезает часть снаряжения из тела Assets/Models/20261121003938_8b8745d5.fbx и запекает на неё вид модели генератора.
Первая такая часть - перчатки ClothGloves из модели персонажа в перчатках.

Запуск (из корня проекта):
    "E:\\Software\\Blender\\blender.exe" --background --factory-startup --python Tools/Blender/cut_gear.py -- ^
        --part gloves --src "<FBX персонажа в перчатках>" --out Assets/Models/Gear/ClothGloves.fbx [--png "<папка>"]

Что делает (форму модели генератора не меняет, только переносит с неё рисунок):
  * оболочка - зона тела под частью (кисть, пальцы, предплечье) с весами кожи тела. Поэтому кожа не проступает
    ни в одной позе без подгонки по позам;
  * модель сдвигается так, чтобы ось её голого предплечья легла на кость предплечья тела. Кисти скелета тела
    ставятся в позу модели: предплечье поворачивается вокруг своей оси (у тела ладони вниз, у модели - вперёд),
    кисть и пальцы - на кончики пальцев модели. После запекания скелет возвращается в T-позу;
  * где на модели ткань, а где кожа - по цвету её текстуры в точке над кожей тела по нормали. По точкам границы
    на каждом пальце и на предплечье ставится плоскость края, и оболочка режется по ней точно, по сетке тела:
    граница по цвету шумит, обрез по ней выходил рваным;
  * оболочка раздувается по нормалям на INFLATE, серая манжета - ещё на CUFF_EXTRA. У края - стенка до кожи:
    без неё край как лист бумаги;
  * своя развёртка и своё запекание цвета, нормалей, металла и гладкости: точка модели - по нормали оболочки,
    как при разметке (Cycles с клеткой лучей запекал кожу большого пальца на запястье);
  * зона тела под частью (вершины, у которых вся окрестность под оболочкой) пишется в <out>_BodyMask.json;
  * экспорт FBX со скелетом тела, как fit_gear.py; текстуры - в <out>_Textures.
С --png рисует снимки: оболочку рядом с моделью в её позе, край оболочки сеткой поверх модели, перчатки на теле
в T-позе. --stage align - только совмещение и снимок кисти тела сеткой поверх модели.
Координаты как в rig_model_8b8745d5.py: лицом в -Y, левая сторона персонажа на +X, рост тела 1,088.
"""
import argparse
import colorsys
import json
import math
import os
import sys

import bmesh
import bpy
import numpy as np
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree
from mathutils.geometry import barycentric_transform
from mathutils.kdtree import KDTree

sys.dont_write_bytecode = True  # без __pycache__ в Tools/Blender
sys.path.append(os.path.dirname(os.path.abspath(__file__)))
from gear_poses import SKIN_BONES, limit_bones  # noqa: E402

BODY_PATH = 'Assets/Models/20261121003938_8b8745d5.fbx'
FINGERS = ('Thumb', 'Index', 'Middle', 'Ring', 'Little')

# piece - кусок модели генератора с рукой этой стороны; twist - поворот предплечья вокруг его оси, градусы
PARTS = {
    'gloves': dict(sides={'Right': 'part_002', 'Left': 'part_003'}, twist=-90.0),
}
INFLATE = 0.002        # раздувание оболочки над кожей, м (рост тела 1,088; в Unity умножается на 1,662)
CUFF_EXTRA = 0.0045    # манжета толще остальной перчатки
CUFF_SCALE = 2.0       # манжета во столько раз шире, чем у модели генератора
CLASS_SIZE = 1024      # текстура модели уменьшается до этого размера для разметки ткань/кожа
FOREARM_SLAB = (0.25, 0.31)  # |x| голого предплечья у модели и у тела: по нему совмещается ось руки
MIN_EDGE = 0.002       # рёбра у границы ткани делятся, пока не станут короче
MIN_ISLAND = 0.00006   # пятна ткани или кожи меньше 0,6 см2 - шум текстуры (грязь, блики)
TEX = 1024             # размер запечённых текстур: обе перчатки в одной
MAX_TILT = 35.0        # плоскость края манжеты, наклонённая к кости сильнее, - шум, тогда край поперёк кости
EDGE_PERCENTILE = 30   # край на пальце: у стольких процентов точек границы ткань кончается раньше
EDGE_BIAS = 0.0015     # обрез на 1,5 мм ближе к основанию, чем граница на модели: у края запекается ткань, не кожа
CUFF_RAMP = 0.002      # за манжетой толщина спадает до обычной на этой длине
GEN_TEX = 2048         # текстуры модели (4096) для запекания уменьшаются до этого размера: иначе 1 ГБ памяти
BAKE_MARGIN = 12       # на столько точек текстуры края островов развёртки дополняются соседним цветом

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
parser = argparse.ArgumentParser()
parser.add_argument('--part', required=True, choices=sorted(PARTS))
parser.add_argument('--src', required=True)
parser.add_argument('--out', required=True)
parser.add_argument('--png', default='')
parser.add_argument('--stage', default='all', choices=['align', 'all'])
args = parser.parse_args(argv)
cfg = PARTS[args.part]
src, out = os.path.abspath(args.src), os.path.abspath(args.out)
stem = os.path.splitext(out)[0]
name = os.path.basename(stem)

bpy.ops.wm.read_factory_settings(use_empty=True)

# ----------------------------------------------------------------------------------------------
# Тело и модель генератора
# ----------------------------------------------------------------------------------------------
bpy.ops.import_scene.fbx(filepath=os.path.abspath(BODY_PATH))
arm_obj = bpy.data.objects['Armature']
body = bpy.data.objects['Body']
limit_bones(body)
for pb in arm_obj.pose.bones:
    pb.matrix_basis = Matrix.Identity(4)
bpy.context.view_layer.update()

before = set(bpy.data.objects)
bpy.ops.import_scene.fbx(filepath=src)
gen = {}
for o in [o for o in bpy.data.objects if o not in before]:
    if o.type == 'MESH' and o.name in cfg['sides'].values():
        o.data.transform(o.matrix_world)
        o.matrix_world = Matrix.Identity(4)
        side = next(s for s, n in cfg['sides'].items() if n == o.name)
        gen[side] = o
    else:
        bpy.data.objects.remove(o)
print(f'[Cut] generator pieces: ' + ', '.join(f'{s} {o.name} verts={len(o.data.vertices)}' for s, o in gen.items()))

# Разметка текстуры модели: ткань (1) или кожа (0) и серая манжета. Ткань бежевая (тон ~0,085, яркость ~0,5),
# кожа розовее и светлее (тон ~0,05, яркость 0,7-0,8), манжета почти без цвета
gen_mat = gen['Right'].data.materials[0]
base_img = next(n.image for n in gen_mat.node_tree.nodes
                if n.type == 'TEX_IMAGE' and n.image and not any(k in n.image.name for k in ('normal', 'roughness', 'metallic')))
small = base_img.copy()
small.scale(CLASS_SIZE, CLASS_SIZE)
tex = np.empty(CLASS_SIZE * CLASS_SIZE * 4, np.float32)
small.pixels.foreach_get(tex)
tex = tex.reshape(CLASS_SIZE, CLASS_SIZE, 4)[:, :, :3]
bpy.data.images.remove(small)


def classify(rgb):
    """(ткань 0/1, манжета 0/1) по цвету."""
    h, s, v = colorsys.rgb_to_hsv(*rgb)
    skin = h < 0.068 and s > 0.18 and v > 0.55
    cuff = s < 0.16 and v > 0.4
    return (0.0 if skin else 1.0), (1.0 if cuff else 0.0)


def sample_uv(u, v):
    return tex[min(CLASS_SIZE - 1, max(0, int(v * CLASS_SIZE))), min(CLASS_SIZE - 1, max(0, int(u * CLASS_SIZE)))]


def gen_face_classes(o):
    me = o.data
    uv = me.uv_layers[0].data
    res = []
    for p in me.polygons:
        u = sum(uv[i].uv.x for i in p.loop_indices) / p.loop_total
        v = sum(uv[i].uv.y for i in p.loop_indices) / p.loop_total
        res.append(classify(sample_uv(u, v)))
    return res


gen_classes = {s: gen_face_classes(o) for s, o in gen.items()}


# ----------------------------------------------------------------------------------------------
# Поза кисти тела под модель
# ----------------------------------------------------------------------------------------------
def rotate_bone(pb, axis, deg):
    head = pb.matrix.translation.copy()
    pb.matrix = Matrix.Translation(head) @ Matrix.Rotation(math.radians(deg), 4, axis) @ Matrix.Translation(-head) @ pb.matrix
    bpy.context.view_layer.update()


def bone_tip(side, finger):
    return arm_obj.pose.bones[f'{side}{finger}Distal'].tail.copy()


for side in gen:
    lower = arm_obj.pose.bones[f'{side}LowerArm']
    # ось кости смотрит от локтя к кисти, у правой руки это -X: знак берётся по стороне, чтобы обе ладони
    # повернулись вперёд, а большие пальцы вверх
    axis = (lower.tail - lower.head).normalized() * math.copysign(1.0, lower.tail.x)
    rotate_bone(lower, axis, cfg['twist'])
    thumb, index = bone_tip(side, 'Thumb'), bone_tip(side, 'Index')
    print(f'[Cut] {side}: forearm twisted {cfg["twist"]}, thumb tip z {thumb.z:.3f}, index tip z {index.z:.3f}')


def posed_bm(o):
    bm = bmesh.new()
    bm.from_object(o, bpy.context.evaluated_depsgraph_get())
    bm.transform(o.matrix_world)
    bm.normal_update()
    return bm


groups = {g.index: g.name for g in body.vertex_groups}
main_bone = [groups[max(v.groups, key=lambda g: g.weight).group] if v.groups else '' for v in body.data.vertices]


def align(side):
    """Сдвигает модель так, чтобы ось её предплечья легла на кость предплечья тела. Длина руки у модели та же
    (кончики пальцев на |x| 0,52), поэтому вдоль руки не сдвигается и не масштабируется; руки у обоих
    горизонтальны, поворот не нужен."""
    o = gen[side]
    lower = arm_obj.pose.bones[f'{side}LowerArm']
    lo, hi = FOREARM_SLAB
    slab = np.array([tuple(v.co) for v in o.data.vertices if lo <= abs(v.co.x) <= hi])
    center = Vector(((slab[:, 0].min() + slab[:, 0].max()) / 2, (slab[:, 1].min() + slab[:, 1].max()) / 2,
                     (slab[:, 2].min() + slab[:, 2].max()) / 2))
    d = lower.tail - lower.head
    on_bone = lower.head + d * ((center.x - lower.head.x) / d.x)
    shift = on_bone - center
    o.data.transform(Matrix.Translation(shift))
    o.data.update()
    print(f'[Cut] {side}: generator moved by {tuple(round(c, 4) for c in shift)}')


def turn_hand(side, tips):
    """Поворачивает кисть тела так, чтобы направление от запястья к середине четырёх пальцев было как у модели."""
    hand = arm_obj.pose.bones[f'{side}Hand']
    wrist = hand.head.copy()
    body_mid = sum((bone_tip(side, f) for f in FINGERS[1:]), Vector()) / 4
    gen_mid = sum((tips[f] for f in FINGERS[1:]), Vector()) / 4
    cur, want = (body_mid - wrist).normalized(), (gen_mid - wrist).normalized()
    angle = math.degrees(cur.angle(want))
    if angle > 0.1:
        rotate_bone(hand, cur.cross(want).normalized(), angle)
    print(f'[Cut] {side}Hand: turned {angle:.1f} deg')


def finger_tips(side):
    """Кончики открытых пальцев модели: связные куски кожи у кисти, у каждого - самая дальняя от запястья точка."""
    o = gen[side]
    me = o.data
    wrist = arm_obj.pose.bones[f'{side}Hand'].head
    skin = {p.index for p in me.polygons if gen_classes[side][p.index][0] < 0.5
            and (p.center - wrist).length > 0.035 and abs(p.center.x) > abs(wrist.x)}
    bm = bmesh.new()
    bm.from_mesh(me)
    bm.faces.ensure_lookup_table()
    seen, comps = set(), []
    for f0 in skin:
        if f0 in seen:
            continue
        stack, comp = [f0], []
        seen.add(f0)
        while stack:
            f = stack.pop()
            comp.append(f)
            for e in bm.faces[f].edges:
                for g in e.link_faces:
                    if g.index in skin and g.index not in seen:
                        seen.add(g.index)
                        stack.append(g.index)
        comps.append(comp)
    comps = [c for c in comps if len(c) > 20]
    tips = []
    for c in comps:
        verts = {v.index for f in c for v in bm.faces[f].verts}
        tip = max((me.vertices[i].co for i in verts), key=lambda p: (p - wrist).length)
        tips.append((tip.copy(), len(c)))
    print(f'[Cut] {side}: skin pieces at fingers {len(comps)}: ' +
          ', '.join(f'{tuple(round(c, 3) for c in t)} ({n})' for t, n in tips))
    if len(tips) != 5:
        raise RuntimeError(f'{side}: expected 5 finger tips, got {len(tips)}')
    # большой палец - ближе всех к запястью вдоль руки; остальные по высоте (ладонь вперёд, большой палец вверх)
    along = (arm_obj.pose.bones[f'{side}Hand'].tail - wrist).normalized()
    tips.sort(key=lambda t: (t[0] - wrist).dot(along))
    thumb = tips[0][0]
    rest = sorted((t[0] for t in tips[1:]), key=lambda p: -p.z)
    return dict(zip(FINGERS, [thumb] + rest))


def spread_fingers(side, tips):
    for finger in FINGERS:
        pb = arm_obj.pose.bones[f'{side}{finger}Proximal']
        head = pb.head.copy()
        cur = (bone_tip(side, finger) - head).normalized()
        want = (tips[finger] - head).normalized()
        angle = math.degrees(cur.angle(want))
        if angle > 0.1:
            rotate_bone(pb, cur.cross(want).normalized(), angle)
        miss = (bone_tip(side, finger) - tips[finger]).length
        print(f'[Cut] {side}{finger}: turned {angle:.1f} deg, tip off by {miss * 1000:.1f} mm along the finger')


for side in gen:
    align(side)
    tips = finger_tips(side)
    turn_hand(side, tips)
    spread_fingers(side, tips)


# ----------------------------------------------------------------------------------------------
# Снимки для проверки
# ----------------------------------------------------------------------------------------------
def setup_render():
    scene = bpy.context.scene
    scene.render.engine = 'BLENDER_WORKBENCH'
    scene.display.shading.light = 'STUDIO'
    scene.display.shading.color_type = 'TEXTURE'
    scene.render.resolution_x = scene.render.resolution_y = 700
    world = bpy.data.worlds.new('PreviewWorld')
    world.color = (0.20, 0.23, 0.26)
    scene.world = world
    cam = bpy.data.objects.new('Cam', bpy.data.cameras.new('Cam'))
    scene.collection.objects.link(cam)
    scene.camera = cam
    cam.data.type = 'ORTHO'
    cam.data.clip_start = 0.001
    return cam


def render(cam, prefix, center, size, views=('front', 'back', 'top', 'bottom', 'end')):
    offs = {'front': ((0, -1, 0), 'Y'), 'back': ((0, 1, 0), 'Y'), 'top': ((0, 0, 1), 'X'),
            'bottom': ((0, 0, -1), 'X'), 'end': ((math.copysign(1, center.x), 0, 0), 'Y')}
    cam.data.ortho_scale = size
    os.makedirs(args.png, exist_ok=True)
    for view in views:
        off, up = offs[view]
        cam.location = center + Vector(off) * 2
        cam.rotation_euler = (center - cam.location).to_track_quat('-Z', up).to_euler()
        bpy.context.scene.render.filepath = os.path.join(args.png, f'{prefix}_{view}.png')
        bpy.ops.render.render(write_still=True)


if args.stage == 'align':
    # тело - сеткой поверх модели: видно, где кисть тела легла в перчатку
    wire = body.copy()
    wire.data = body.data.copy()
    bpy.context.scene.collection.objects.link(wire)
    mod = wire.modifiers.new('Wire', 'WIREFRAME')
    mod.thickness = 0.0006
    red = bpy.data.materials.new('Red')
    red.diffuse_color = (1, 0.1, 0.1, 1)
    wire.data.materials.clear()
    wire.data.materials.append(red)
    # порядок: сначала арматура, потом сетка
    wire.modifiers.move(len(wire.modifiers) - 1, len(wire.modifiers) - 1)
    body.hide_render = True
    cam = setup_render()
    bpy.context.scene.display.shading.color_type = 'MATERIAL'
    for o in gen.values():
        o.active_material.diffuse_color = (0.8, 0.7, 0.5, 1)
    for side in gen:
        c = arm_obj.pose.bones[f'{side}Hand'].head.copy()
        c.x += math.copysign(0.03, c.x)
        render(cam, f'align_{side}', c, 0.17)
    print('[Cut] align renders done')
    sys.exit(0)

# ----------------------------------------------------------------------------------------------
# Оболочка: зона тела под частью с весами кожи. Координаты - мира (как у fit_gear.py), объект без поворота
# ----------------------------------------------------------------------------------------------
zone_bones = {f'{s}{b}' for s in gen for b in ['Hand', 'LowerArm'] +
              [f'{f}{p}' for f in FINGERS for p in ('Proximal', 'Intermediate', 'Distal')]}
shell = body.copy()
shell.data = body.data.copy()
shell.data.transform(body.matrix_world)
bpy.context.scene.collection.objects.link(shell)
shell.parent = arm_obj
shell.matrix_world = Matrix.Identity(4)
shell.name = shell.data.name = name
bm = bmesh.new()
bm.from_mesh(shell.data)
bm.verts.ensure_lookup_table()
bmesh.ops.delete(bm, geom=[f for f in bm.faces if not all(main_bone[v.index] in zone_bones for v in f.verts)],
                 context='FACES')
bmesh.ops.delete(bm, geom=[v for v in bm.verts if not v.link_faces], context='VERTS')
bmesh.ops.triangulate(bm, faces=bm.faces[:])
bm.to_mesh(shell.data)
shell.data.update()
print(f'[Cut] zone: verts={len(bm.verts)} faces={len(bm.faces)}')

# Разметка на вершинах модели: среднее по углам граней вокруг вершины и центрам этих граней. Пятна меньше
# MIN_ISLAND (грязь на пальцах, блики на ткани) переходят к окружению. Между вершинами разметка
# интерполируется, поэтому граница ткани гладкая, а не по шуму текстуры
def gen_vertex_classes(o):
    me = o.data
    uv = me.uv_layers[0].data
    nv = len(me.vertices)
    acc, cnt = np.zeros((nv, 2)), np.zeros(nv)
    area = np.zeros(nv)
    for p in me.polygons:
        cu = sum(uv[i].uv.x for i in p.loop_indices) / p.loop_total
        cv = sum(uv[i].uv.y for i in p.loop_indices) / p.loop_total
        center = classify(sample_uv(cu, cv))
        for li in p.loop_indices:
            vi = me.loops[li].vertex_index
            acc[vi] += classify(sample_uv(*uv[li].uv))
            acc[vi] += center
            cnt[vi] += 2
            area[vi] += p.area / p.loop_total
    val = acc / cnt[:, None]
    nb = [[] for _ in range(nv)]
    for e in me.edges:
        a, b = e.vertices
        nb[a].append(b)
        nb[b].append(a)
    val = np.array([(val[i] + val[n].sum(0)) / (1 + len(n)) for i, n in enumerate(nb)])
    for sign in (1, -1):
        seen = set()
        for v0 in range(nv):
            if v0 in seen or (val[v0, 0] - 0.5) * sign <= 0:
                continue
            stack, comp = [v0], []
            seen.add(v0)
            while stack:
                i = stack.pop()
                comp.append(i)
                for n in nb[i]:
                    if n not in seen and (val[n, 0] - 0.5) * sign > 0:
                        seen.add(n)
                        stack.append(n)
            if area[comp].sum() < MIN_ISLAND:
                val[comp, 0] = 1.0 - val[comp, 0]
    return val


gen_tris = {}
for side, o in gen.items():
    me = o.data
    me.calc_loop_triangles()
    verts = [v.co.copy() for v in me.vertices]
    tris = [tuple(t.vertices) for t in me.loop_triangles]
    gen_tris[side] = (BVHTree.FromPolygons(verts, tris), verts, tris, gen_vertex_classes(o))
BARY = (Vector((1, 0, 0)), Vector((0, 1, 0)), Vector((0, 0, 1)))


def sample(p, n):
    """(ткань, манжета) модели от 0 до 1 в точке над p по нормали кожи n, как при запекании; сторона - по знаку x.
    Ближайшая точка не годится: у обреза перчатки ближайшей к пальцу оказывается торцевая кромка ткани,
    и перчатка на оболочке выходила на 1,5 см длиннее."""
    bvh, verts, tris, val = gen_tris['Left' if p.x > 0 else 'Right']
    hits = [h for h in (bvh.ray_cast(p, n, 0.03), bvh.ray_cast(p, -n, 0.015)) if h[0] is not None]
    if hits:
        loc, _, ti, _ = min(hits, key=lambda h: h[3])
    else:
        loc, _, ti, _ = bvh.find_nearest(p, 0.08)
        if loc is None:
            return 0.0, 0.0
    t = tris[ti]
    w = barycentric_transform(loc, verts[t[0]], verts[t[1]], verts[t[2]], *BARY)
    r = val[t[0]] * w.x + val[t[1]] * w.y + val[t[2]] * w.z
    return float(r[0]), float(r[1])


def posed_coords():
    pbm = posed_bm(shell)
    pbm.verts.ensure_lookup_table()
    res = [(v.co.copy(), v.normal.copy()) for v in pbm.verts]
    pbm.free()
    return res



def segment(bm_vert, deform):
    """(сторона, кусок): палец по имени, предплечье или кисть - по кости с наибольшим весом."""
    gi = max(bm_vert[deform].items(), key=lambda x: x[1])[0]
    bone = body.vertex_groups[gi].name
    side = 'Left' if bone.startswith('Left') else 'Right'
    part = bone[len(side):]
    for f in FINGERS:
        if part.startswith(f):
            return side, f
    return side, ('Forearm' if part == 'LowerArm' else 'Hand')


# Граница ткани по цвету текстуры шумит у кромки, и обрез по ней выходил рваным. Поэтому по ней только
# находятся точки границы на каждом пальце и на предплечье, через них проводится плоскость, и оболочка
# режется по плоскости. Для точек границы копия оболочки мельче делится там, где ткань сменяется кожей
coarse = bm.copy()
for rnd in range(8):
    bm.to_mesh(shell.data)
    shell.data.update()
    pc = posed_coords()
    bm.verts.ensure_lookup_table()
    cls = [sample(p, n) for p, n in pc]
    split = []
    for e in bm.edges:
        a, b = e.verts
        if (a.co - b.co).length < MIN_EDGE:
            continue
        mid = sample((pc[a.index][0] + pc[b.index][0]) / 2, (pc[a.index][1] + pc[b.index][1]).normalized())
        vals = (cls[a.index], mid, cls[b.index])
        if len({v[0] > 0.5 for v in vals}) > 1 or len({v[1] > 0.5 for v in vals}) > 1:
            split.append(e)
    print(f'[Cut] refine {rnd}: verts={len(bm.verts)}, edges to split {len(split)}')
    if not split:
        break
    bmesh.ops.subdivide_edges(bm, edges=split, cuts=1, use_grid_fill=True)
    bmesh.ops.triangulate(bm, faces=bm.faces[:])
bm.to_mesh(shell.data)
shell.data.update()
pc = posed_coords()
bm.verts.ensure_lookup_table()
deform = bm.verts.layers.deform.active
cls = [sample(p, n) for p, n in pc]
seg = [segment(v, deform) for v in bm.verts]
crossings = {}
for e in bm.edges:
    a, b = (v.index for v in e.verts)
    fa, fb = cls[a][0] - 0.5, cls[b][0] - 0.5
    if fa * fb < 0 and seg[a] == seg[b] and seg[a][1] != 'Hand':
        t = fa / (fa - fb)
        crossings.setdefault(seg[a], []).append(pc[a][0].lerp(pc[b][0], t))


def segment_axis(side, part):
    """Направление куска от основания к концу в позе модели."""
    if part == 'Forearm':
        pb = arm_obj.pose.bones[f'{side}LowerArm']
        return (pb.tail - pb.head).normalized()
    return (bone_tip(side, part) - arm_obj.pose.bones[f'{side}{part}Proximal'].head).normalized()


planes = {}
for key, pts in sorted(crossings.items()):
    axis = segment_axis(*key)
    P = np.array([tuple(p) for p in pts])
    if key[1] == 'Forearm':
        # край манжеты чистый (сотни точек): плоскость по точкам, наклон к кости - как у модели
        keep = np.ones(len(P), bool)
        for _ in range(3):
            c = P[keep].mean(0)
            w, V = np.linalg.eigh(np.cov((P[keep] - c).T))
            n = Vector(V[:, 0]).normalized()
            if n.dot(axis) < 0:
                n = -n
            d = np.abs((P - c) @ np.array(n))
            keep = d < max(0.003, np.percentile(d, 80))
        tilt = math.degrees(n.angle(axis))
        if tilt > MAX_TILT:
            n = axis.copy()
        print(f'[Cut] {key[0]}{key[1]}: edge plane from {keep.sum()}/{len(P)} points, tilt {tilt:.0f} deg')
        c = Vector(c)
    else:
        # на пальцах в точки границы попадают кромка ткани, грязь и перепонки между пальцами: плоскость
        # по ним наклонялась на 50-90 градусов. Край - поперёк пальца там, где ткань уже у EDGE_PERCENTILE точек
        along = (P - np.array(pts[0])) @ np.array(axis)
        base = Vector(pts[0]) + axis * float(np.percentile(along, EDGE_PERCENTILE))
        print(f'[Cut] {key[0]}{key[1]}: {len(P)} edge points spread {np.ptp(along) * 1000:.1f} mm along the finger')
        c, n = base, axis.copy()
    # нормаль плоскости смотрит от ткани к коже: на пальце к кончику, на предплечье - к локтю
    planes[key] = (c, -n if key[1] == 'Forearm' else n)

# Ширина серой манжеты: до какого расстояния от края предплечья на модели серое
cuff_width = {}
for side in gen:
    key = (side, 'Forearm')
    c, n = planes[key]
    dist = [(c - pc[i][0]).dot(n) for i in range(len(pc)) if seg[i] == key and cls[i][1] > 0.5 and cls[i][0] > 0.5]
    cuff_width[side] = min(0.02, max(0.004, float(np.percentile(dist, 90)) if dist else 0.0))
    print(f'[Cut] {side}: cuff {len(dist)} verts, width {cuff_width[side] * 1000:.1f} mm')

# Манжета шире, чем у модели, в CUFF_SCALE раз (решено 22.11.2026: у модели слишком узкая): край предплечья
# уходит к локтю, а при запекании полоса манжеты растягивается на новую ширину (cuff_map)
cuff_map = {}
for side in gen:
    key = (side, 'Forearm')
    c, n = planes[key]
    gen_w = cuff_width[side]
    extra = gen_w * (CUFF_SCALE - 1.0)
    planes[key] = (c + n * extra, n)
    cuff_width[side] = gen_w * CUFF_SCALE
    cuff_map[side] = (planes[key][0], n, extra, gen_w, cuff_width[side])
    print(f'[Cut] {side}: cuff widened to {cuff_width[side] * 1000:.1f} mm')


def cuff_sample_point(p, side):
    """Точка, с которой запекается p: в полосе манжеты - сжатая к краю модели, дальше - та же."""
    c, n, extra, gen_w, new_w = cuff_map[side]
    d = (c - p).dot(n)
    if d < 0.0 or d >= new_w:
        return p
    return p + n * ((d - extra) - d * gen_w / new_w)

# Дальше - исходная оболочка с сеткой тела: g - расстояние от плоскости края до вершины в позе модели в сторону
# ткани минус EDGE_BIAS (>0 - ткань). У кисти - расстояние от края манжеты: на длинных рёбрах от кисти к локтю
# обрез иначе шёл бы по условному значению и не туда (дыра на запястье)
bm.free()
bm = coarse
bm.to_mesh(shell.data)
shell.data.update()
pc = posed_coords()
bm.verts.ensure_lookup_table()
deform = bm.verts.layers.deform.active
seg_of, posed_of, g_of = {}, {}, {}
for v in bm.verts:
    key = segment(v, deform)
    seg_of[v], posed_of[v] = key, pc[v.index][0]
    if key[1] == 'Hand':
        key = (key[0], 'Forearm')
    if key in planes:
        c, n = planes[key]
        g_of[v] = (c - posed_of[v]).dot(n) - EDGE_BIAS
    else:
        g_of[v] = 1.0

# Вершины тела под оболочкой: ткань у вершины и у всех соседей по телу (кольцо сужения, как в fit_gear.py)
body_rest = [body.matrix_world @ v.co for v in body.data.vertices]
kd = KDTree(len(bm.verts))
for v in bm.verts:
    kd.insert(v.co, v.index)
kd.balance()
body_fabric = {}
for i, p in enumerate(body_rest):
    if main_bone[i] in zone_bones:
        _, j, d = kd.find(p)
        if d < 1e-6:
            body_fabric[i] = g_of[bm.verts[j]] > 0
body_neigh = {}
for e in body.data.edges:
    a, b = e.vertices
    body_neigh.setdefault(a, []).append(b)
    body_neigh.setdefault(b, []).append(a)
hidden = sorted(i for i, fab in body_fabric.items()
                if fab and all(body_fabric.get(n, False) for n in body_neigh.get(i, [])))
print(f'[Cut] body: under fabric {sum(body_fabric.values())}, hidden after one ring shrink {len(hidden)}')


def slice_at(level, only=None):
    """Делит рёбра, на которых g переходит через level, и соединяет новые вершины в гранях: новая петля рёбер.
    only - только рёбра между этими кусками (полоса манжеты на предплечье и кисти)."""
    new = []
    for e in [e for e in bm.edges if (g_of[e.verts[0]] - level) * (g_of[e.verts[1]] - level) < 0]:
        a, b = e.verts
        if only and not (seg_of[a] in only and seg_of[b] in only):
            continue
        t = (g_of[a] - level) / (g_of[a] - g_of[b])
        _, nv = bmesh.utils.edge_split(e, a, t)
        g_of[nv], seg_of[nv], posed_of[nv] = level, seg_of[a], posed_of[a].lerp(posed_of[b], t)
        new.append(nv)
    bmesh.ops.connect_verts(bm, verts=new, check_degenerate=True)
    return len(new)


# Петли рёбер по манжете (иначе на предплечье тела вершины только у запястья и у локтя и манжете не из чего
# быть), затем обрез по g = 0
for side in gen:
    w = cuff_width[side] - EDGE_BIAS
    for level in (w + CUFF_RAMP, w, w * 0.5):
        slice_at(level, {(side, 'Forearm'), (side, 'Hand')})
print(f'[Cut] edge cut: {slice_at(0.0)} new verts on the edge')
bmesh.ops.delete(bm, geom=[f for f in bm.faces if sum(g_of[v] for v in f.verts) < 0], context='FACES')
bmesh.ops.delete(bm, geom=[v for v in bm.verts if not v.link_faces], context='VERTS')
bmesh.ops.triangulate(bm, faces=bm.faces[:])

# Раздувание по нормалям покоя: оболочка над кожей на INFLATE, манжета на CUFF_EXTRA выше. Точка кожи
# под вершиной запоминается для стенки у края
bm.normal_update()
skin_at = {}
for v in bm.verts:
    off = INFLATE
    side, part = seg_of[v]
    if part in ('Forearm', 'Hand'):
        w = cuff_width[side] - EDGE_BIAS
        off += CUFF_EXTRA * min(1.0, max(0.0, (w + CUFF_RAMP - g_of[v]) / CUFF_RAMP))
    skin = v.co.copy()
    v.co += v.normal * off
    skin_at[v.co.copy().freeze()] = skin
bm.to_mesh(shell.data)
shell.data.update()
print(f'[Cut] shell: verts={len(bm.verts)} faces={len(bm.faces)}')
bm.free()


# ----------------------------------------------------------------------------------------------
# Развёртка и запекание вида модели в позе модели
# ----------------------------------------------------------------------------------------------
bpy.ops.object.select_all(action='DESELECT')
shell.select_set(True)
bpy.context.view_layer.objects.active = shell
bpy.ops.object.mode_set(mode='EDIT')
bpy.ops.mesh.select_all(action='SELECT')
bpy.ops.uv.smart_project(angle_limit=math.radians(60), island_margin=0.01)
bpy.ops.object.mode_set(mode='OBJECT')
for uvl in list(shell.data.uv_layers)[:-1]:
    shell.data.uv_layers.remove(uvl)

mat = bpy.data.materials.new(name)
mat.use_nodes = True
shell.data.materials.clear()
shell.data.materials.append(mat)
tex_node = mat.node_tree.nodes.new('ShaderNodeTexImage')
mat.node_tree.nodes.active = tex_node

# Запекание своё, не Cycles: для каждой точки текстуры оболочки (в позе модели) берётся точка модели по нормали
# оболочки - ближайшее попадание луча наружу или внутрь, как в sample(). Cycles пускал лучи из клетки снаружи
# модели, и у основания большого пальца клетка уходила за палец: туда запекалась его кожа (светлые пятна).
# Нормали модели переводятся из её касательного пространства (MikkTSpace, как у Normal Map в Blender)
# в касательное пространство оболочки (MikkTSpace, его же считает Unity при импорте)
def gen_image(kind):
    img = next(n.image for n in gen_mat.node_tree.nodes if n.type == 'TEX_IMAGE' and n.image and
               (kind in n.image.name if kind else not any(k in n.image.name for k in ('normal', 'roughness', 'metallic'))))
    small = img.copy()
    small.scale(GEN_TEX, GEN_TEX)
    a = np.empty(GEN_TEX * GEN_TEX * 4, np.float32)
    small.pixels.foreach_get(a)
    bpy.data.images.remove(small)
    return a.reshape(GEN_TEX, GEN_TEX, 4)


def loop_arrays(me):
    me.calc_tangents()
    me.calc_loop_triangles()
    return dict(
        pos=np.array([v.co[:] for v in me.vertices]),
        tri_v=np.array([t.vertices[:] for t in me.loop_triangles]),
        tri_l=np.array([t.loops[:] for t in me.loop_triangles]),
        uv=np.array([d.uv[:] for d in me.uv_layers[0].data]),
        nrm=np.array([l.normal[:] for l in me.loops]),
        tan=np.array([l.tangent[:] for l in me.loops]),
        sgn=np.array([l.bitangent_sign for l in me.loops]))


def frame(d, tri, w):
    """Нормаль, касательная и бинормаль в точках треугольников tri с весами w (n, 3)."""
    L = d['tri_l'][tri]
    N = np.einsum('nk,nkc->nc', w, d['nrm'][L])
    N /= np.linalg.norm(N, axis=1, keepdims=True)
    T = np.einsum('nk,nkc->nc', w, d['tan'][L])
    T -= N * (T * N).sum(1, keepdims=True)
    T /= np.linalg.norm(T, axis=1, keepdims=True)
    B = np.cross(N, T) * d['sgn'][L[:, 0]][:, None]
    return N, T, B


def bilinear(img, uv):
    h, w = img.shape[:2]
    x = np.clip(uv[:, 0] * w - 0.5, 0, w - 1.001)
    y = np.clip(uv[:, 1] * h - 0.5, 0, h - 1.001)
    x0, y0 = x.astype(int), y.astype(int)
    fx, fy = (x - x0)[:, None], (y - y0)[:, None]
    return ((img[y0, x0] * (1 - fx) + img[y0, x0 + 1] * fx) * (1 - fy) +
            (img[y0 + 1, x0] * (1 - fx) + img[y0 + 1, x0 + 1] * fx) * fy)


gen_px = {k: gen_image(k) for k in ('', 'normal', 'roughness', 'metallic')}
gen_data = {}
for side, o in gen.items():
    d = loop_arrays(o.data)
    d['bvh'] = BVHTree.FromPolygons([Vector(p) for p in d['pos']], d['tri_v'].tolist())
    gen_data[side] = d
posed_me = shell.evaluated_get(bpy.context.evaluated_depsgraph_get()).to_mesh()
sd = loop_arrays(posed_me)

texels, hits = [], {s: ([], [], []) for s in gen}  # texel; сторона -> (номер точки, треугольник модели, веса)
s_tri, s_w = [], []
for ti in range(len(sd['tri_l'])):
    uvt = sd['uv'][sd['tri_l'][ti]] * TEX
    x0, y0 = np.floor(uvt.min(0)).astype(int)
    x1, y1 = np.ceil(uvt.max(0)).astype(int)
    xs, ys = np.meshgrid(np.arange(max(x0, 0), min(x1, TEX)), np.arange(max(y0, 0), min(y1, TEX)))
    pts = np.stack([xs.ravel() + 0.5, ys.ravel() + 0.5], 1)
    a, b, c = uvt
    m = np.array([[b[0] - a[0], c[0] - a[0]], [b[1] - a[1], c[1] - a[1]]])
    if abs(np.linalg.det(m)) < 1e-9:
        continue
    bc = (pts - a) @ np.linalg.inv(m).T
    w = np.stack([1 - bc[:, 0] - bc[:, 1], bc[:, 0], bc[:, 1]], 1)
    inside = (w >= -1e-6).all(1)
    for (px, py), ww in zip(pts[inside].astype(int), w[inside]):
        texels.append((py, px))
        s_tri.append(ti)
        s_w.append(ww)
s_tri, s_w = np.array(s_tri), np.array(s_w)
P = np.einsum('nk,nkc->nc', s_w, sd['pos'][sd['tri_v'][s_tri]])
SN, ST, SB = frame(sd, s_tri, s_w)
print(f'[Cut] bake: {len(texels)} texels')
missed = skin_hits = 0
for i in range(len(texels)):
    p, n = Vector(P[i]), Vector(SN[i])
    side = 'Left' if p.x > 0 else 'Right'
    p = cuff_sample_point(p, side)
    bvh = gen_data[side]['bvh']
    found = [h for h in (bvh.ray_cast(p, n, 0.02), bvh.ray_cast(p, -n, 0.015)) if h[0] is not None]
    if found:
        loc, _, gti, _ = min(found, key=lambda h: h[3])
    else:
        loc, _, gti, _ = bvh.find_nearest(p, 0.03)
        if loc is None:
            missed += 1
            continue
    tv = gen_data[side]['tri_v'][gti]
    pos = gen_data[side]['pos']
    bw = barycentric_transform(loc, Vector(pos[tv[0]]), Vector(pos[tv[1]]), Vector(pos[tv[2]]), *BARY)
    # у обреза луч иногда попадает в кожу модели: такая точка остаётся пустой и заполняется тканью рядом
    cls_v = gen_tris[side][3]
    if (cls_v[tv[0], 0] * bw.x + cls_v[tv[1], 0] * bw.y + cls_v[tv[2], 0] * bw.z) < 0.5:
        skin_hits += 1
        continue
    hits[side][0].append(i)
    hits[side][1].append(gti)
    hits[side][2].append(tuple(bw))
print(f'[Cut] bake: missed {missed}, skin of the model {skin_hits} (filled from fabric nearby)')

out_px = {k: np.zeros((TEX, TEX, 4), np.float32) for k in ('color', 'normal', 'ms')}
filled = np.zeros((TEX, TEX), bool)
texels = np.array(texels)
for side, (idx, gtri, gw) in hits.items():
    idx, gtri, gw = np.array(idx), np.array(gtri), np.array(gw)
    d = gen_data[side]
    guv = np.einsum('nk,nkc->nc', gw, d['uv'][d['tri_l'][gtri]])
    GN, GT, GB = frame(d, gtri, gw)
    nts = bilinear(gen_px['normal'], guv)[:, :3] * 2 - 1
    nw = GT * nts[:, :1] + GB * nts[:, 1:2] + GN * nts[:, 2:3]
    out_n = np.stack([(nw * ST[idx]).sum(1), (nw * SB[idx]).sum(1), (nw * SN[idx]).sum(1)], 1)
    out_n /= np.linalg.norm(out_n, axis=1, keepdims=True)
    ty, tx = texels[idx, 0], texels[idx, 1]
    out_px['color'][ty, tx] = bilinear(gen_px[''], guv)
    out_px['color'][ty, tx, 3] = 1.0
    out_px['normal'][ty, tx, :3] = out_n * 0.5 + 0.5
    out_px['normal'][ty, tx, 3] = 1.0
    out_px['ms'][ty, tx, 0:3] = bilinear(gen_px['metallic'], guv)[:, :1]
    out_px['ms'][ty, tx, 3] = 1.0 - bilinear(gen_px['roughness'], guv)[:, 0]
    filled[ty, tx] = True
shell.evaluated_get(bpy.context.evaluated_depsgraph_get()).to_mesh_clear()

# Края островов развёртки дополняются цветом соседей: иначе в мип-уровнях на швах тёмная кайма
for _ in range(BAKE_MARGIN):
    grown = filled.copy()
    acc = {k: np.zeros_like(a) for k, a in out_px.items()}
    cnt = np.zeros((TEX, TEX))
    for dy, dx in ((-1, 0), (1, 0), (0, -1), (0, 1), (-1, -1), (-1, 1), (1, -1), (1, 1)):
        sh = np.roll(np.roll(filled, dy, 0), dx, 1)
        for k, a in out_px.items():
            acc[k] += np.roll(np.roll(a, dy, 0), dx, 1) * sh[..., None]
        cnt += sh
    new = ~filled & (cnt > 0)
    for k, a in out_px.items():
        a[new] = acc[k][new] / cnt[new][:, None]
    filled = grown | new

tex_dir = stem + '_Textures'
os.makedirs(tex_dir, exist_ok=True)


def save(kind, file_name, colorspace):
    img = bpy.data.images.new(f'{name}_{kind}', TEX, TEX, alpha=True)
    img.colorspace_settings.name = colorspace
    img.pixels.foreach_set(out_px[kind].ravel())
    img.filepath_raw = os.path.join(tex_dir, file_name)
    img.file_format = 'PNG'
    img.save()
    print(f'[Cut] texture {img.filepath_raw}')
    return img


color_img = save('color', f'{name}.png', 'sRGB')
save('normal', f'{name}_normal.png', 'Non-Color')
save('ms', f'{name}_metallic_smoothness.png', 'Non-Color')
tex_node.image = color_img
mat.node_tree.links.new(tex_node.outputs['Color'], mat.node_tree.nodes['Principled BSDF'].inputs['Base Color'])

# ----------------------------------------------------------------------------------------------
# Стенка у края: от края оболочки внутрь до кожи, чтобы у манжеты и обреза пальцев была толщина.
# UV стенки - узкая полоса внутри острова у края, там цвет края
# ----------------------------------------------------------------------------------------------
bm = bmesh.new()
bm.from_mesh(shell.data)
uv_layer = bm.loops.layers.uv.active
deform = bm.verts.layers.deform.active
bm.normal_update()
boundary = [e for e in bm.edges if len(e.link_faces) == 1]
inner = {}
for e in boundary:
    for v in e.verts:
        if v in inner:
            continue
        skin = skin_at[v.co.copy().freeze()]
        w = bm.verts.new(skin + (skin - v.co).normalized() * 0.0005)
        for gi, wt in v[deform].items():
            w[deform][gi] = wt
        inner[v] = w
for e in boundary:
    f = e.link_faces[0]
    loops = {l.vert: l for l in f.loops}
    # в грани оболочки ребро идёт a -> b; стенка обходит его обратно
    a, b = e.verts if loops[e.verts[0]].link_loop_next.vert == e.verts[1] else (e.verts[1], e.verts[0])
    wall = bm.faces.new((b, a, inner[a], inner[b]))
    center_uv = sum((l[uv_layer].uv for l in f.loops), Vector((0, 0))) / len(f.loops)
    for l, src_v in zip(wall.loops, (b, a, a, b)):
        uv = loops[src_v][uv_layer].uv.copy()
        if l.vert not in (a, b):
            d = center_uv - uv
            if d.length > 0:
                uv += d.normalized() * (2.0 / TEX)
        l[uv_layer].uv = uv
bm.normal_update()
bm.to_mesh(shell.data)
shell.data.update()
bm.free()
print(f'[Cut] edge walls: {len(boundary)} quads')

# Веса: SKIN_BONES кости на вершину, как в игре
limit_bones(shell)
nb = max(len(v.groups) for v in shell.data.vertices)
if nb > SKIN_BONES or any(not v.groups for v in shell.data.vertices):
    raise RuntimeError(f'{name}: bad weights after limit ({nb} bones per vertex)')

os.makedirs(os.path.dirname(out), exist_ok=True)
with open(stem + '_BodyMask.json', 'w', encoding='utf-8') as fh:
    json.dump({'part': args.part, 'bodyVertices': len(body_rest),
               'hidden': [round(c, 6) for i in hidden for c in body_rest[i]]}, fh)
print(f'[Cut] mask {stem}_BodyMask.json')

# ----------------------------------------------------------------------------------------------
# Снимки: в позе модели рядом с моделью, в T-позе на теле без спрятанной зоны
# ----------------------------------------------------------------------------------------------
if args.png:
    bpy.context.view_layer.update()
    cam = setup_render()
    body.hide_render = True
    for side in gen:
        c = arm_obj.pose.bones[f'{side}Hand'].head.copy()
        c.x += math.copysign(0.02, c.x)
        shell.hide_render = True
        for s, o in gen.items():
            o.hide_render = s != side
        render(cam, f'gen_{side}', c, 0.2, ('front', 'back', 'top', 'bottom'))
        for o in gen.values():
            o.hide_render = True
        shell.hide_render = False
        render(cam, f'shell_{side}', c, 0.2, ('front', 'back', 'top', 'bottom'))
        # край оболочки сеткой поверх модели: совпадает ли обрез с перчаткой модели
        wire = shell.copy()
        bpy.context.scene.collection.objects.link(wire)
        wmod = wire.modifiers.new('Wire', 'WIREFRAME')
        wmod.thickness = 0.0004
        wmod.use_even_offset = False
        shell.hide_render = True
        gen[side].hide_render = False
        render(cam, f'overlay_{side}', c, 0.2, ('front', 'back'))
        bpy.data.objects.remove(wire)
        gen[side].hide_render = True
        shell.hide_render = False

for pb in arm_obj.pose.bones:
    pb.matrix_basis = Matrix.Identity(4)
bpy.context.view_layer.update()

if args.png:
    bm2 = bmesh.new()
    bm2.from_mesh(body.data)
    hid = set(hidden)
    bm2.verts.ensure_lookup_table()
    bmesh.ops.delete(bm2, geom=[f for f in bm2.faces if all(v.index in hid for v in f.verts)], context='FACES_ONLY')
    bm2.to_mesh(body.data)
    skin_mat = bpy.data.materials.new('Skin')
    skin_mat.diffuse_color = (0.85, 0.62, 0.52, 1)
    body.data.materials.clear()
    body.data.materials.append(skin_mat)
    body.hide_render = False
    for side in gen:
        c = arm_obj.pose.bones[f'{side}Hand'].head.copy()
        render(cam, f'tpose_{side}', c, 0.24, ('front', 'back', 'top', 'bottom', 'end'))

# ----------------------------------------------------------------------------------------------
# Экспорт: часть на скелете тела, без тела и текстур (материал собирает Unity), как fit_gear.py
# ----------------------------------------------------------------------------------------------
for o in gen.values():
    bpy.data.objects.remove(o)
bpy.data.objects.remove(body)
mat.name = 'Material'
for m in bpy.data.materials:
    m.use_nodes = False
bpy.ops.object.select_all(action='DESELECT')
arm_obj.select_set(True)
shell.select_set(True)
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
print(f'[Cut] exported {out}')
