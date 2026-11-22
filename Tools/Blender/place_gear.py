"""
Ставит жёсткую часть снаряжения из генератора на тело Assets/Models/20261121003938_8b8745d5.fbx без смены формы.
Такие части - ботинки ClothBoots и NomadBoots, каждые из модели одного ботинка.

Запуск (из корня проекта):
    "E:\\Software\\Blender\\blender.exe" --background --factory-startup --python Tools/Blender/place_gear.py -- ^
        --part boots --src "<FBX ботинка>" --out Assets/Models/Gear/ClothBoots.fbx [--png "<папка>"]
    (NomadBoots: --part nomad_boots --out Assets/Models/Gear/NomadBoots.fbx)

Что делает (вершины по одной не двигает: на нагруднике это смяло ремни и шнуровку):
  * ботинок - все куски модели (сам ботинок, ремни, шнурки). Носок модели смотрит в -Y;
  * масштаб по замеру ступни тела: длина и ширина ступни вдоль её оси плюс зазоры, высота - средним из двух.
    Ботинок поворачивается по оси ступни (носки у тела развёрнуты наружу) и ставится подошвой на пол;
  * голенище - отдельно: выше подъёма ботинок плавно вытягивается вверх до cap_z, сдвигается на центр голени
    и расширяется, если голень ближе SHAFT_GAP к стенке. Так голенище по голени, а носок по ступне;
  * модель - сплошная оболочка, голенище сверху закрыто неглубокой чашей. Голень входит в эту чашу: щели между
    голенью и голенищем не видно, и позже туда же уйдёт низ штанин;
  * второй ботинок - отражение по X (тело симметрично);
  * веса - с ближайшей кожи тела (как fit_gear.py), SKIN_BONES кости на вершину: ботинок гнётся в щиколотке
    и в носке, как кожа под ним;
  * под ботинком прячется вся ступня и голень ниже чаши на HIDE_BELOW: <out>_BodyMask.json;
  * проверка по позам из gear_poses.py: точки видимой кожи, которые в покое внутри ботинка, не должны выходить
    наружу (кожа сквозь ботинок), и насколько подошва ниже ступни тела (уходит в землю);
  * текстуры модели уменьшаются до TEXTURE_SIZE, металл с гладкостью сводятся в одну картинку, как fit_gear.py;
  * экспорт FBX со скелетом тела, как fit_gear.py.
С --png рисует ботинки на теле с текстурой в T-позе и в позах игры (ходьба, бег, прыжок) и модель генератора рядом.
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
from gear_poses import POSES, RECORDED, SKIN_BONES, limit_bones, set_pose  # noqa: E402

BODY_PATH = 'Assets/Models/20261121003938_8b8745d5.fbx'

# side - сторона, на которую надевается модель (вторая - отражением); main - кусок модели с самим ботинком;
# shaft_z - высоты модели (её единицы), между которыми голенище плавно переходит от ступни к голени;
# cap_z - высота чаши голенища на теле (None - как у модели, без вытягивания); cap_src - высота дна чаши в единицах
# модели, если чаша не самое частое по высоте в голенище (у высокого голенища по медиане вышла бы середина голенища);
# remove_unseen - убрать грани, которых не видно снаружи ни с одной стороны
PARTS = {
    'boots': dict(side='Right', main='part_000', shaft_z=(0.25, 0.5), cap_z=0.135),
    # NomadBoots: голенище по замыслу высокое, у модели оно уже на треть голени - чаша на своей высоте. Внутри голенища
    # у модели поперёк вторая перепонка (52 грани на высоте обмотки): снаружи её не видно, но лучи от голени
    # упираются в неё, и голенище раздувало бы, а проверка «внутри ботинка» по чётности пересечений ошибалась бы
    'nomad_boots': dict(side='Right', main='part_000', shaft_z=(0.25, 0.5), cap_z=None, cap_src=0.73,
                        remove_unseen=True),
}
TOE_GAP = 0.010       # носок ботинка впереди пальцев тела, м (рост тела 1,088; в Unity умножается на 1,662)
HEEL_GAP = 0.006      # пятка ботинка позади пятки тела
SIDE_GAP = 0.006      # ботинок шире ступни на столько с каждой стороны
SHAFT_GAP = 0.004     # стенка голенища не ближе к голени
# Чаша голенища (cap_z в PARTS) - не ниже кольца голени тела на 115 мм (рост 1,088). Ниже него кожа весит частью
# на стопе, и треугольник от щиколотки до этого кольца при сгибе стопы выпирал бугром над задником. Выше - только
# на голени, как голенище
HIDE_BELOW = 0.010    # кожа голени прячется ниже чаши голенища на столько: у края она уходит в чашу
TEXTURE_SIZE = 1024
SAMPLES = ((1 / 3, 1 / 3, 1 / 3), (0.6, 0.2, 0.2), (0.2, 0.6, 0.2), (0.2, 0.2, 0.6),
           (0.5, 0.5, 0), (0.5, 0, 0.5), (0, 0.5, 0.5), (1, 0, 0), (0, 1, 0), (0, 0, 1))

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
side = cfg['side']
other = 'Left' if side == 'Right' else 'Right'
sgn = 1.0 if side == 'Left' else -1.0  # знак X стороны: левая сторона персонажа на +X

bpy.ops.wm.read_factory_settings(use_empty=True)

# ----------------------------------------------------------------------------------------------
# Тело и модель генератора
# ----------------------------------------------------------------------------------------------
bpy.ops.import_scene.fbx(filepath=os.path.abspath(BODY_PATH))
arm_obj = bpy.data.objects['Armature']
body = bpy.data.objects['Body']
limit_bones(body)
set_pose(arm_obj, 'tpose')
body_rest = [body.matrix_world @ v.co for v in body.data.vertices]
group_names = {g.index: g.name for g in body.vertex_groups}
main_bone = [group_names[max(v.groups, key=lambda g: g.weight).group] if v.groups else '' for v in body.data.vertices]

before = set(bpy.data.objects)
bpy.ops.import_scene.fbx(filepath=src)
pieces = []
for o in [o for o in bpy.data.objects if o not in before]:
    if o.type != 'MESH':
        bpy.data.objects.remove(o)
        continue
    o.data.transform(o.matrix_world)
    o.matrix_world = Matrix.Identity(4)
    flag = o.data.attributes.new('main', 'INT', 'FACE')
    flag.data.foreach_set('value', [int(o.name == cfg['main'])] * len(o.data.polygons))
    pieces.append(o)
gen = next(o for o in pieces if o.name == cfg['main'])
# копия модели как есть - для снимков рядом
gen_view = gen.copy()
gen_view.data = gen.data.copy()
bpy.context.scene.collection.objects.link(gen_view)
gen_view.hide_render = True


def remove_unseen(o):
    """Убирает грани, которых не видно снаружи: ни из одной из 600 сторон луч к центру грани и к точкам у её углов
    не попадает в неё первой."""
    bm = bmesh.new()
    bm.from_mesh(o.data)
    bm.faces.ensure_lookup_table()
    tree = BVHTree.FromBMesh(bm)
    n = 600
    dirs = []
    for k in range(n):
        z = 1 - 2 * (k + 0.5) / n
        r = math.sqrt(1 - z * z)
        a = k * math.pi * (3 - math.sqrt(5))
        dirs.append(Vector((r * math.cos(a), r * math.sin(a), z)))

    def seen(f):
        c = f.calc_center_median()
        for p in [c] + [c.lerp(vtx.co, 0.6) for vtx in f.verts]:
            for d in dirs:
                hit = tree.ray_cast(p + d * 2, -d, 2.5)
                if hit[0] is not None and (hit[2] == f.index or (hit[0] - p).length < 1e-6):
                    return True
        return False

    drop = [f for f in bm.faces if not seen(f)]
    zs = [f.calc_center_median().z for f in drop]
    print(f'[Place] unseen faces removed: {len(drop)}' + (f' at model z {min(zs):.3f}..{max(zs):.3f}' if drop else ''))
    bmesh.ops.delete(bm, geom=drop, context='FACES')
    bm.to_mesh(o.data)
    bm.free()


if cfg.get('remove_unseen'):
    remove_unseen(gen)
bpy.ops.object.select_all(action='DESELECT')
for o in pieces:
    o.select_set(True)
bpy.context.view_layer.objects.active = gen
bpy.ops.object.join()
boot = gen
boot.name = name
me = boot.data
print(f'[Place] generator pieces: {len(pieces)}, verts {len(me.vertices)}')

src_co = np.array([tuple(v.co) for v in me.vertices])  # координаты модели: по ним голенище отличается от носка
is_main = np.zeros(len(me.polygons), int)
me.attributes['main'].data.foreach_get('value', is_main)
main_verts = sorted({i for p in me.polygons if is_main[p.index] for i in p.vertices})

# ----------------------------------------------------------------------------------------------
# Замер ступни тела: ось от пятки к носку, длина и ширина вдоль неё
# ----------------------------------------------------------------------------------------------
foot = np.array([tuple(p.xy) for p, b in zip(body_rest, main_bone) if b in (side + 'Foot', side + 'Toes')])
centered = foot - foot.mean(0)
axis = np.linalg.eigh(centered.T @ centered)[1][:, -1]
if axis[1] > 0:
    axis = -axis  # к носку, носок в -Y
fwd = Vector((axis[0], axis[1], 0)).normalized()
across = Vector((-fwd.y, fwd.x, 0))  # поперёк ступни
u = foot @ np.array(fwd.xy)
v = foot @ np.array(across.xy)
foot_len, foot_wid = u.max() - u.min(), v.max() - v.min()
yaw = math.atan2(fwd.x, -fwd.y)  # от -Y к носку
print(f'[Place] {side} foot: length {foot_len * 1000:.1f} mm, width {foot_wid * 1000:.1f} mm, toes out {math.degrees(yaw):.1f} deg')

gen_len = src_co[:, 1].max() - src_co[:, 1].min()
gen_wid = src_co[:, 0].max() - src_co[:, 0].min()
sy = (foot_len + TOE_GAP + HEEL_GAP) / gen_len
sx = (foot_wid + 2 * SIDE_GAP) / gen_wid
sz = (sx + sy) / 2
rot = Matrix.Rotation(yaw, 4, 'Z')
co = np.array([tuple(rot @ Vector((p[0] * sx, p[1] * sy, (p[2] - src_co[:, 2].min()) * sz))) for p in src_co])
# пятка ботинка за пяткой тела на HEEL_GAP, середина по ширине - на середине ступни
bu = co[:, :2] @ np.array(fwd.xy)
bv = co[:, :2] @ np.array(across.xy)
shift = fwd * (u.min() - HEEL_GAP - bu.min()) + across * ((v.max() + v.min()) / 2 - (bv.max() + bv.min()) / 2)
co[:, :2] += np.array(shift.xy)
print(f'[Place] scale x {sx:.4f} y {sy:.4f} z {sz:.4f}: boot {gen_len * sy * 1000:.0f} x {gen_wid * sx * 1000:.0f} '
      f'x {(src_co[:, 2].max() - src_co[:, 2].min()) * sz * 1000:.0f} mm')

# ----------------------------------------------------------------------------------------------
# Голенище: на центр голени и не ближе SHAFT_GAP к ней. Доля сдвига t растёт от подъёма к голенищу
# ----------------------------------------------------------------------------------------------
z0, z1 = cfg['shaft_z']
s = np.clip((src_co[:, 2] - z0) / (z1 - z0), 0, 1)
t = s * s * (3 - 2 * s)
band = [i for i in main_verts if src_co[i, 2] >= z1]


def cap_height():
    """Высота чаши голенища (её края выше): заданное дно чаши модели или медиана голенища."""
    if 'cap_src' in cfg:
        sc = min((cfg['cap_src'] - z0) / (z1 - z0), 1.0)
        return (cfg['cap_src'] - src_co[:, 2].min()) * sz + k * (h * (sc ** 3 - sc ** 4 / 2) + (cfg['cap_src'] - z1) * sz)
    return float(np.percentile(co[band, 2], 50))


# Голенище выше подъёма вытягивается вверх так, чтобы чаша встала на cap_z: растяжение растёт по высоте
# вместе с t (интеграл t), поэтому излома нет, а подошва и носок не меняются
h = (z1 - z0) * sz
lift = h * (s ** 3 - s ** 4 / 2) + np.maximum(src_co[:, 2] - z1, 0) * sz
k = 0.0
cap_z = cap_height()
if cfg['cap_z'] is not None:
    k = (cfg['cap_z'] - cap_z) / (h / 2 + cap_z - (z1 - src_co[:, 2].min()) * sz)
    co[:, 2] += k * lift
    cap_z = cap_height()
print(f'[Place] shaft stretched up by {k * 100:.0f} %: cap at {cap_z * 1000:.1f} mm, top {co[:, 2].max() * 1000:.1f} mm')
shaft_c = Vector(((co[band, 0].max() + co[band, 0].min()) / 2, (co[band, 1].max() + co[band, 1].min()) / 2, 0))
# голень: кольцо кожи ноги над чашей и под ней - центр по рамке их точек
leg = [i for i, p in enumerate(body_rest) if p.x * sgn > 0.005 and cap_z - 0.05 < p.z < cap_z + 0.03]
lo = np.array([min(body_rest[i].x for i in leg), min(body_rest[i].y for i in leg)])
hi = np.array([max(body_rest[i].x for i in leg), max(body_rest[i].y for i in leg)])
shin_c = Vector((*(lo + hi) / 2, 0))
offset = shin_c - shaft_c
co[:, 0] += t * offset.x
co[:, 1] += t * offset.y
print(f'[Place] cap at {cap_z * 1000:.1f} mm, shaft moved {offset.length * 1000:.1f} mm to the shin')

# Зона тела под ботинками: ступня целиком и голень ниже чаши
hidden = sorted(i for i, (p, b) in enumerate(zip(body_rest, main_bone))
                if b.endswith('Foot') or b.endswith('Toes') or (b.endswith('LowerLeg') and p.z < cap_z - HIDE_BELOW))
hid = set(hidden)
print(f'[Place] hidden body vertices {len(hidden)}')

# видимая кожа под чашей (треугольники ноги, спрятанные не целиком) должна быть внутри голенища с зазором
body.data.calc_loop_triangles()


def leg_points(z_max):
    pts = []
    for lt in body.data.loop_triangles:
        ps = [body_rest[i] for i in lt.vertices]
        if not all(p.x * sgn > 0.005 for p in ps) or all(i in hid for i in lt.vertices):
            continue
        for w in SAMPLES:
            p = ps[0] * w[0] + ps[1] * w[1] + ps[2] * w[2]
            if 0.04 < p.z < z_max:
                pts.append(p)
    return pts


def boot_bvh(points):
    bm = bmesh.new()
    vs = [bm.verts.new(Vector(points[i])) for i in range(len(points))]
    for p in me.polygons:
        if is_main[p.index]:
            try:
                bm.faces.new([vs[i] for i in p.vertices])
            except ValueError:
                pass
    bm.normal_update()
    bvh = BVHTree.FromBMesh(bm)
    bm.free()
    return bvh


bvh = boot_bvh(co)
need = 1.0
for p in leg_points(cap_z - 0.002):
    d = Vector((p.x, p.y, 0)) - shin_c
    if d.length < 1e-4:
        continue
    hit = bvh.ray_cast(Vector((shin_c.x, shin_c.y, p.z)), d.normalized(), 0.2)
    if hit[0] is None:
        continue
    need = max(need, (d.length + SHAFT_GAP) / hit[3])
# расширение только там, где голенище: у подъёма t = 0
grow = 1 + (need - 1) * t
co[:, 0] = shin_c.x + (co[:, 0] - shin_c.x) * grow
co[:, 1] = shin_c.y + (co[:, 1] - shin_c.y) * grow
print(f'[Place] shaft widened x{need:.3f} for {SHAFT_GAP * 1000:.0f} mm to the shin')
for i, vtx in enumerate(me.vertices):
    vtx.co = Vector(co[i])
me.update()

# ----------------------------------------------------------------------------------------------
# Второй ботинок - отражение по X
# ----------------------------------------------------------------------------------------------
bm = bmesh.new()
bm.from_mesh(me)
dup = bmesh.ops.duplicate(bm, geom=list(bm.verts) + list(bm.edges) + list(bm.faces))
new_verts = [g for g in dup['geom'] if isinstance(g, bmesh.types.BMVert)]
new_faces = [g for g in dup['geom'] if isinstance(g, bmesh.types.BMFace)]
for vtx in new_verts:
    vtx.co.x = -vtx.co.x
bmesh.ops.reverse_faces(bm, faces=new_faces)
bm.to_mesh(me)
bm.free()
me.update()
nv_half = len(co)
is_main = np.zeros(len(me.polygons), int)
me.attributes['main'].data.foreach_get('value', is_main)

# ----------------------------------------------------------------------------------------------
# Веса с тела
# ----------------------------------------------------------------------------------------------
for vg in list(boot.vertex_groups):
    boot.vertex_groups.remove(vg)
for g in body.vertex_groups:
    boot.vertex_groups.new(name=g.name)
transfer = boot.modifiers.new('WeightTransfer', 'DATA_TRANSFER')
transfer.object = body
transfer.use_vert_data = True
transfer.data_types_verts = {'VGROUP_WEIGHTS'}
transfer.vert_mapping = 'POLYINTERP_NEAREST'
transfer.layers_vgroup_select_src = 'ALL'
transfer.layers_vgroup_select_dst = 'NAME'
bpy.ops.object.select_all(action='DESELECT')
boot.select_set(True)
bpy.context.view_layer.objects.active = boot
bpy.ops.object.modifier_apply(modifier=transfer.name)
limit_bones(boot)
bad = [vtx.index for vtx in me.vertices if not vtx.groups]
used = {boot.vertex_groups[g.group].name for vtx in me.vertices for g in vtx.groups if g.weight > 0}
if bad or max(len(vtx.groups) for vtx in me.vertices) > SKIN_BONES:
    raise RuntimeError(f'{name}: bad weights ({len(bad)} vertices without bones)')
print(f'[Place] bones: {sorted(used)}')
boot.parent = arm_obj
mod = boot.modifiers.new('Armature', 'ARMATURE')
mod.object = arm_obj

# ----------------------------------------------------------------------------------------------
# Проверка по позам: видимая кожа, которая в покое внутри ботинка, не выходит наружу; подошва против ступни
# ----------------------------------------------------------------------------------------------
body.data.calc_loop_triangles()
check = []  # (вершины треугольника, веса точки)
for lt in body.data.loop_triangles:
    if all(i in hid for i in lt.vertices):
        continue
    ps = [body_rest[i] for i in lt.vertices]
    if min(p.z for p in ps) > cap_z + 0.03:
        continue
    for w in SAMPLES:
        check.append((tuple(lt.vertices), w))


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


# вверх и в две стороны: у модели с лишней гранью внутри один луч может ошибиться, решает большинство
RAYS = [Vector(d).normalized() for d in ((0.31, 0.17, 0.93), (0.9, 0.35, 0.12), (-0.45, -0.87, 0.2))]


def inside(bvh_, p):
    """Внутри ли точка замкнутого ботинка: нечётное число пересечений луча (по ближайшей грани и её нормали
    у вогнутых мест под ремнём и у чаши выходило «снаружи» там, где на снимках кожи не видно)."""
    loc, _, _, dist = bvh_.find_nearest(p, 0.05)
    if loc is None:
        return False, 0.0
    odd = 0
    for ray in RAYS:
        hits, o = 0, p.copy()
        while True:
            hit = bvh_.ray_cast(o, ray, 0.5)
            if hit[0] is None:
                break
            hits += 1
            o = hit[0] + ray * 1e-5
        odd += hits % 2
    return odd * 2 > len(RAYS), dist


def point(pos, tri, w):
    return Vector(pos[tri[0]] * w[0] + pos[tri[1]] * w[1] + pos[tri[2]] * w[2])


rest_bvh = boot_bvh(evaluated(boot))
rest_pos = evaluated(body)
# выше чаши кожа внутри только у язычка и края: при сгибе она выходит над краем, это не сквозь стенку
inner = [(tri, w) for tri, w in check
         if inside(rest_bvh, point(rest_pos, tri, w))[0] and point(rest_pos, tri, w).z < cap_z - 0.003]
print(f'[Place] skin points inside boots at rest: {len(inner)} of {len(check)}')
foot_idx = [i for i in range(len(body_rest)) if main_bone[i].endswith('Foot') or main_bone[i].endswith('Toes')]
worst = {'out': (0, 0.0, ''), 'sink': (0.0, '')}
for pose in list(POSES) + list(RECORDED):
    set_pose(arm_obj, pose)
    bpos = evaluated(body)
    gpos = evaluated(boot)
    bvh_p = boot_bvh(gpos)
    n_out, d_out, where = 0, 0.0, []
    for tri, w in inner:
        ins, dist = inside(bvh_p, point(bpos, tri, w))
        if not ins and dist > 0.0005:
            n_out += 1
            d_out = max(d_out, dist)
            p = point(rest_pos, tri, w)
            where.append(f'({p.x * 1000:.0f} {p.y * 1000:.0f} {p.z * 1000:.0f})')
    if d_out > 0.003:
        print(f'[Place]   {pose} skin out at rest points (mm): ' + ' '.join(where))
    # подошва против ступни: самая низкая точка каждого ботинка и ступни под ним
    sink = 0.0
    for half in (slice(0, nv_half), slice(nv_half, None)):
        g = gpos[half]
        sx_ = np.sign(g[:, 0].mean())
        f = bpos[[i for i in foot_idx if np.sign(body_rest[i].x) == sx_]]
        sink = max(sink, f[:, 2].min() - g[:, 2].min())
    if n_out and (n_out > worst['out'][0] or d_out > worst['out'][1]):
        worst['out'] = (max(n_out, worst['out'][0]), max(d_out, worst['out'][1]), pose)
    if sink > worst['sink'][0]:
        worst['sink'] = (sink, pose)
    if n_out or pose in POSES or pose.endswith('@1.0'):
        print(f'[Place] {pose:16s} skin out {n_out:3d} ({d_out * 1000:4.1f} mm), sole below foot {sink * 1000:4.1f} mm')
print(f'[Place] all poses: skin out worst {worst["out"][0]} points, {worst["out"][1] * 1000:.1f} mm ({worst["out"][2]}); '
      f'sole below foot worst {worst["sink"][0] * 1000:.1f} mm ({worst["sink"][1]})')
set_pose(arm_obj, 'tpose')

# ----------------------------------------------------------------------------------------------
# Снимки
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
    return cam


def render(cam, prefix, center, size, views):
    dirs = {'front': (0, -1, 0), 'back': (0, 1, 0), 'out': (sgn, 0, 0), 'in': (-sgn, 0, 0),
            'persp': (0.6 * sgn, -0.7, 0.45), 'persp_back': (-0.5 * sgn, 0.7, 0.6), 'top': (0, -0.05, 1)}
    cam.data.ortho_scale = size
    for view in views:
        d = Vector(dirs[view]).normalized()
        cam.location = center + d * 3
        cam.rotation_euler = (-d).to_track_quat('-Z', 'Y').to_euler()
        bpy.context.scene.render.filepath = os.path.join(os.path.abspath(args.png), f'{prefix}_{view}.png')
        bpy.ops.render.render(write_still=True)


if args.png:
    cam = setup_render()
    # модель генератора отдельно и ботинок на теле в том же масштабе кадра
    bpy.context.view_layer.update()
    for o in bpy.data.objects:
        if o.type == 'MESH':
            o.hide_render = o is not gen_view
    gc = Vector(np.array([tuple(vtx.co) for vtx in gen_view.data.vertices]).mean(0))
    render(cam, 'gen', gc, 1.3, ('out', 'front', 'persp', 'persp_back', 'top'))
    gen_view.hide_render = True
    bm = bmesh.new()
    bm.from_mesh(body.data)
    bm.verts.ensure_lookup_table()
    bmesh.ops.delete(bm, geom=[f for f in bm.faces if all(vtx.index in hid for vtx in f.verts)], context='FACES_ONLY')
    bm.to_mesh(body.data)
    bm.free()
    skin = bpy.data.materials.new('Skin')
    skin.diffuse_color = (0.85, 0.62, 0.52, 1)
    body.data.materials.clear()
    body.data.materials.append(skin)
    body.hide_render = boot.hide_render = False
    c = Vector(np.array([tuple(p) for p in co]).mean(0))
    render(cam, 'tpose', c + Vector((0, 0, 0.01)), 0.23, ('out', 'in', 'front', 'back', 'persp', 'persp_back', 'top'))
    render(cam, 'tpose_legs', Vector((0, 0, 0.2)), 0.5, ('front', 'persp'))
    for pose in ('play:walk@0.8', 'play:walk@1.4', 'play:run@0.8', 'play:run@1.3', 'play:jump@1.0', 'play:idle@1.0',
                 'play:aim_strafe@1.0', 'crouch'):
        if pose not in RECORDED and pose not in POSES:
            continue
        set_pose(arm_obj, pose)
        pts = evaluated(boot)
        c = Vector(pts.mean(0))
        render(cam, pose.replace(':', '_').replace('@', '_'), c, 0.5, ('out', 'front', 'persp_back'))
    set_pose(arm_obj, 'tpose')

# ----------------------------------------------------------------------------------------------
# Маска, текстуры, экспорт
# ----------------------------------------------------------------------------------------------
os.makedirs(os.path.dirname(out), exist_ok=True)
with open(stem + '_BodyMask.json', 'w', encoding='utf-8') as f:
    json.dump({'part': args.part, 'bodyVertices': len(body_rest),
               'hidden': [round(c, 6) for i in hidden for c in body_rest[i]]}, f)
print(f'[Place] mask {stem}_BodyMask.json')

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
    print(f'[Place] texture {img.filepath_raw}')


save(pixels(base), f'{name}.png', 'sRGB')
save(pixels(normal), f'{name}_normal.png', 'Non-Color')
ms = pixels(metallic)
ms[:, 3] = 1.0 - pixels(roughness)[:, 0]
save(ms, f'{name}_metallic_smoothness.png', 'Non-Color')

me.attributes.remove(me.attributes['main'])
bpy.data.objects.remove(gen_view)
bpy.data.objects.remove(body)
boot.matrix_world = Matrix.Identity(4)
me.materials[0].name = 'Material'
for m in bpy.data.materials:
    m.use_nodes = False
bpy.ops.object.select_all(action='DESELECT')
arm_obj.select_set(True)
boot.select_set(True)
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
print(f'[Place] exported {out}')
