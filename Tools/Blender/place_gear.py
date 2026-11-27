"""
Ставит жёсткую часть снаряжения из генератора на тело Assets/Models/20261121003938_8b8745d5.fbx без смены формы.
Такие части - ботинки ClothBoots, NomadBoots и кеды SunShoes, каждые из модели одного ботинка.

Запуск (из корня проекта):
    "E:\\Software\\Blender\\blender.exe" --background --factory-startup --python Tools/Blender/place_gear.py -- ^
        --part boots --src "<FBX ботинка>" --out Assets/Models/Gear/ClothBoots.fbx [--png "<папка>"] [--texture-only]
    (NomadBoots: --part nomad_boots --out Assets/Models/Gear/NomadBoots.fbx;
     SunShoes: --part sun_shoes --out Assets/Models/Gear/SunShoes.fbx)

SunShoes вырезал из модели генератора пользователь, места под цвет качества пометил материалом quality_material:
материалы сводятся в один, маска цвета качества - по помеченным граням (<out>_Textures/<часть>_quality.png). Текстура
чистится gear_texture.clean, как у SunPants (texture в PARTS), после посадки: узор солнц рисуется по форме ботинка
на ноге. С --png снимки текстуры до и после (tex_before_*, tex_after_*); --texture-only - только они.

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
import gear_texture  # noqa: E402
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
    # SunShoes (Культ Солнца): высокие кеды с раздвоенным носком, тёмно-синяя ткань с солнцами, серые шнурки и два
    # витка серой обмотки у щиколотки (помечены '01 - Default'), латунные накладки на носке с медными заклёпками,
    # латунная полоса на пятке, латунное солнце с солнечной панелью сбоку. Модель - левый кед (большой палец справа,
    # солнце снаружи слева): side Left, правый - отражением, солнце на обоих снаружи. Кеды ставятся в своей форме:
    # верх 104 мм, чаша на 87 мм (cap_z None). Вытягивание до 125 мм по правилу ClothBoots сделало из них сапог - отвергнуто
    # пользователем 27.11.2026; кожа сквозь кеды на родной высоте в позах игры не выходит.
    # main_largest - ботинок без лучей солнца: лучи - отдельные треугольники, для «внутри ботинка» по чётности
    # пересечений они лишние. drop - «стрелка» в середине солнца (мусор генератора, на картинке там панель).
    # laces - куски шнурков (серые целиком), hard - заклёпки (свой рисунок). sole - подошва по высоте модели:
    # грани ниже side_z - коричневый бок, смотрящие вниз (нормаль z < bottom_nz) - чёрный низ. panel - середина
    # солнца: белый «циферблат» модели перекрашивается в солнечную панель с картинки
    'sun_shoes': dict(side='Left', main='part_000', shaft_z=(0.40, 0.80), cap_z=None, cap_src=1.27,
                      shaft_sectors=True, main_largest=True, drop=('part_010',), quality_material='01 - Default',
                      laces=('part_001', 'part_002', 'part_003', 'part_004', 'part_005'),
                      sole=dict(side_z=0.09, bottom_nz=-0.6),
                      panel=dict(color=(0.106, 0.122, 0.157), line=(0.30, 0.34, 0.42), rows=6),
                      texture=dict(
                          # медианы по картинкам генератора SunShoes_side.webp и SunShoes_front.webp; leather - бок
                          # подошвы (тёмно-коричневый), black - низ подошвы, sun - узор (как у SunPants)
                          palette=dict(fabric=(0.17, 0.18, 0.21), grey=(0.70, 0.69, 0.66), brass=(0.575, 0.486, 0.33),
                                       leather=(0.19, 0.175, 0.155), copper=(0.62, 0.36, 0.22), black=(0.11, 0.11, 0.11),
                                       sun=(0.33, 0.35, 0.42)),
                          shade=dict(fabric=(2.5, 0.75, 1.2), grey=(0.8, 0.5, 1.35), brass=(0.8, 0.5, 1.4),
                                     leather=(1.0, 0.6, 1.3), copper=(0.5, 0.5, 1.4), black=(1.5, 0.7, 1.3)),
                          hard=('part_006', 'part_007', 'part_008', 'part_009'), panel=(), fabric_pieces=('part_000',),
                          brass_share=0.25, window_area=0.0005, behind_reach=0.03, sun_clear=0.010,
                          sun_radius=0.011, sun_spacing=0.034, sun_alpha=0.7)),
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
CLEAN_SIZE = 2048     # чистка текстуры - на 2048 (пороги gear_texture в пикселях подобраны на нём), сохранение - 1024
SAMPLES = ((1 / 3, 1 / 3, 1 / 3), (0.6, 0.2, 0.2), (0.2, 0.6, 0.2), (0.2, 0.2, 0.6),
           (0.5, 0.5, 0), (0.5, 0, 0.5), (0, 0.5, 0.5), (1, 0, 0), (0, 1, 0), (0, 0, 1))

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
parser = argparse.ArgumentParser()
parser.add_argument('--part', required=True, choices=sorted(PARTS))
parser.add_argument('--src', required=True)
parser.add_argument('--out', required=True)
parser.add_argument('--png', default='')
parser.add_argument('--texture-only', action='store_true')
parser.add_argument('--shape-only', action='store_true')  # только посадка и снимки ботинка на ноге, без текстуры
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
piece_names = []  # имя куска модели по номеру в атрибуте piece
for o in [o for o in bpy.data.objects if o not in before]:
    if o.type != 'MESH' or o.name in cfg.get('drop', ()):
        bpy.data.objects.remove(o)
        continue
    o.data.transform(o.matrix_world)
    o.matrix_world = Matrix.Identity(4)
    nf = len(o.data.polygons)
    main_f = [int(o.name == cfg['main'])] * nf
    if o.name == cfg['main'] and cfg.get('main_largest'):
        # только самый большой связный кусок: лучи солнца SunShoes - отдельные треугольники снаружи ботинка
        bm_ = bmesh.new()
        bm_.from_mesh(o.data)
        bm_.faces.ensure_lookup_table()
        comp = np.full(nf, -1)
        for f0 in bm_.faces:
            if comp[f0.index] >= 0:
                continue
            stack = [f0]
            comp[f0.index] = f0.index
            while stack:
                f = stack.pop()
                for e in f.edges:
                    for g in e.link_faces:
                        if comp[g.index] < 0:
                            comp[g.index] = f0.index
                            stack.append(g)
        bm_.free()
        big = np.bincount(comp).argmax()
        main_f = [int(c == big) for c in comp]
        print(f'[Place] {o.name}: shell {sum(main_f)} faces, {nf - sum(main_f)} faces in small pieces')
    flag = o.data.attributes.new('main', 'INT', 'FACE')
    flag.data.foreach_set('value', main_f)
    pid_attr = o.data.attributes.new('piece', 'INT', 'FACE')
    pid_attr.data.foreach_set('value', [len(piece_names)] * nf)
    piece_names.append(o.name)
    if cfg.get('quality_material'):
        # пометка пользователя под цвет качества - отдельный материал (суффикс .001 - от импорта)
        names = [m.name.split('.')[0] if m else '' for m in o.data.materials]
        q = o.data.attributes.new('quality', 'INT', 'FACE')
        q.data.foreach_set('value', [int(names[p.material_index] == cfg['quality_material']) for p in o.data.polygons])
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
if cfg.get('quality_material'):
    # один материал: второй без текстур дал бы в Unity вторую часть меша без текстуры (как у SunPants)
    tex_slot = next(i for i, m in enumerate(me.materials)
                    if m and m.node_tree and any(n.type == 'TEX_IMAGE' for n in m.node_tree.nodes))
    for k_ in reversed(range(len(me.materials))):
        if k_ != tex_slot:
            me.materials.pop(index=k_)
    for p in me.polygons:
        p.material_index = 0
    qa = np.zeros(len(me.polygons), int)
    me.attributes['quality'].data.foreach_get('value', qa)
    print(f'[Place] marked faces ({cfg["quality_material"]}): {int(qa.sum())} of {len(me.polygons)}')

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
print(f'[Place] cap at {cap_z * 1000:.1f} mm, shaft moved {offset.length * 1000:.1f} mm to the shin '
      f'(x {offset.x * 1000:.1f}, y {offset.y * 1000:.1f})')

# Зона тела под ботинками: ступня целиком и голень ниже чаши
hide_below = cfg.get('hide_below', HIDE_BELOW)
hidden = sorted(i for i, (p, b) in enumerate(zip(body_rest, main_bone))
                if b.endswith('Foot') or b.endswith('Toes') or (b.endswith('LowerLeg') and p.z < cap_z - hide_below))
hid = set(hidden)
rings = sorted({round(p.z * 1000) for p, b in zip(body_rest, main_bone)
                if b == side + 'LowerLeg' and p.z < 0.2})
print(f'[Place] hidden body vertices {len(hidden)} (shin below {(cap_z - hide_below) * 1000:.0f} mm); '
      f'shin vertex heights below 200 mm: {rings}')

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
by_sector = {}  # (высота 10 мм, сектор 45°) -> нужное расширение: где голень упирается в стенку
for p in leg_points(cap_z - 0.002):
    d = Vector((p.x, p.y, 0)) - shin_c
    if d.length < 1e-4:
        continue
    hit = bvh.ray_cast(Vector((shin_c.x, shin_c.y, p.z)), d.normalized(), 0.2)
    if hit[0] is None:
        continue
    k_need = (d.length + SHAFT_GAP) / hit[3]
    need = max(need, k_need)
    key = (int(p.z * 100), int(((math.degrees(math.atan2(d.y, d.x * sgn)) + 22.5) % 360) // 45))
    by_sector[key] = max(by_sector.get(key, 0), k_need)
tight = sorted(((k_, v_) for k_, v_ in by_sector.items() if v_ > 1.02), key=lambda kv: -kv[1])[:8]
print('[Place] shaft tight at (cm, sector: 0 out, 2 back, 4 in, 6 front): ' +
      ', '.join(f'{z}cm/{s}: x{v:.2f}' for (z, s), v in tight))
if cfg.get('shaft_sectors'):
    # SunShoes: голень упирается в задник только сзади на 7 см (x1,12); одно расширение на весь круг раздуло бы всё
    # голенище (с голенищем, вытянутым до 125 мм, так и было: x1,45 из-за переда на 11 см, ступенька над пяткой).
    # Стенка отходит по секторам угла вокруг голени: где тесно, плавно на 60° в стороны
    NSEC = 24
    for it in range(3):
        bvh = boot_bvh(co)
        sec = np.ones(NSEC)
        for p in leg_points(cap_z - 0.002):
            d = Vector((p.x, p.y, 0)) - shin_c
            if d.length < 1e-4:
                continue
            hit = bvh.ray_cast(Vector((shin_c.x, shin_c.y, p.z)), d.normalized(), 0.2)
            if hit[0] is None:
                continue
            k_ = int((math.atan2(d.y, d.x) % (2 * math.pi)) / (2 * math.pi) * NSEC) % NSEC
            sec[k_] = max(sec[k_], (d.length + SHAFT_GAP) / hit[3])
        if sec.max() < 1.005:
            break
        ang = np.arctan2(co[:, 1] - shin_c.y, co[:, 0] - shin_c.x)
        sec_ang = (np.arange(NSEC) + 0.5) / NSEC * 2 * np.pi
        diff = np.abs((ang[:, None] - sec_ang[None, :] + np.pi) % (2 * np.pi) - np.pi)
        fall = np.clip(1 - diff / math.radians(60), 0, 1)
        fall = fall * fall * (3 - 2 * fall)
        grow = 1 + ((sec[None, :] - 1) * fall).max(1) * t
        co[:, 0] = shin_c.x + (co[:, 0] - shin_c.x) * grow
        co[:, 1] = shin_c.y + (co[:, 1] - shin_c.y) * grow
        print(f'[Place] shaft widened by sectors (pass {it + 1}): max x{sec.max():.3f} at '
              f'{math.degrees(sec_ang[sec.argmax()]):.0f} deg (90 back, 270 front), wider than x1.02 in '
              f'{int((sec > 1.02).sum())} of {NSEC} sectors')
else:
    # расширение только там, где голенище: у подъёма t = 0
    grow = 1 + (need - 1) * t
    co[:, 0] = shin_c.x + (co[:, 0] - shin_c.x) * grow
    co[:, 1] = shin_c.y + (co[:, 1] - shin_c.y) * grow
    print(f'[Place] shaft widened x{need:.3f} for {SHAFT_GAP * 1000:.0f} mm to the shin')
for i, vtx in enumerate(me.vertices):
    vtx.co = Vector(co[i])
me.update()


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
    cam.data.clip_start = 0.001
    return cam


def render(cam, prefix, center, size, views):
    dirs = {'front': (0, -1, 0), 'back': (0, 1, 0), 'out': (sgn, 0, 0), 'in': (-sgn, 0, 0),
            'persp': (0.6 * sgn, -0.7, 0.45), 'persp_back': (-0.5 * sgn, 0.7, 0.6), 'top': (0, -0.05, 1),
            'persp_in': (-0.6 * sgn, -0.7, 0.45), 'below': (0.3 * sgn, -0.3, -0.9)}
    cam.data.ortho_scale = size
    for view in views:
        d = Vector(dirs[view]).normalized()
        cam.location = center + d * 3
        cam.rotation_euler = (-d).to_track_quat('-Z', 'Y').to_euler()
        bpy.context.scene.render.filepath = os.path.join(os.path.abspath(args.png), f'{prefix}_{view}.png')
        bpy.ops.render.render(write_still=True)


cam = setup_render() if args.png else None


def face_flags(attr):
    a = np.zeros(len(me.polygons), int)
    me.attributes[attr].data.foreach_get('value', a)
    return a


# ----------------------------------------------------------------------------------------------
# Текстура: чистка gear_texture.clean (как у SunPants), середина солнца - солнечная панель
# ----------------------------------------------------------------------------------------------
tex_nodes = {n.image.name: n for n in me.materials[0].node_tree.nodes if n.type == 'TEX_IMAGE' and n.image}
base = next(n.image for k_, n in tex_nodes.items() if not any(s in k_ for s in ('normal', 'roughness', 'metallic')))
normal = next(n.image for k_, n in tex_nodes.items() if 'normal' in k_)
roughness = next(n.image for k_, n in tex_nodes.items() if 'roughness' in k_)
metallic = next((n.image for k_, n in tex_nodes.items() if 'metallic' in k_), None)
cleaned = None


def image_pixels(img, size):
    img = img.copy()
    img.scale(size, size)
    a = np.empty(size * size * 4, np.float32)
    img.pixels.foreach_get(a)
    bpy.data.images.remove(img)
    return a.reshape(size, size, 4)


def shoe_shots(prefix):
    for o in bpy.data.objects:
        if o.type == 'MESH':
            o.hide_render = o is not boot
    c = Vector(np.array(co).mean(0))
    render(cam, prefix, c + Vector((0, 0, 0.01)), 0.21, ('out', 'in', 'front', 'back', 'top', 'persp', 'persp_in',
                                                         'below'))


if args.shape_only:
    if args.png:
        bm = bmesh.new()
        bm.from_mesh(body.data)
        bmesh.ops.delete(bm, geom=[f for f in bm.faces if all(v_.index in hid for v_ in f.verts)], context='FACES_ONLY')
        bm.to_mesh(body.data)
        bm.free()
        for o in bpy.data.objects:
            if o.type == 'MESH':
                o.hide_render = o not in (boot, body)
        c = Vector(np.array(co).mean(0))
        render(cam, 'shape', c + Vector((0, 0, 0.03)), 0.28, ('out', 'in', 'front', 'back', 'persp', 'persp_in'))
    sys.exit(0)
if 'texture' in cfg:
    S_ = CLEAN_SIZE
    px = {k_: image_pixels(img, S_) for k_, img in (('base', base), ('normal', normal), ('roughness', roughness))}
    if args.png:
        shoe_shots('tex_before')
    poly_piece = np.array(piece_names)[face_flags('piece')]
    quality_poly = face_flags('quality').astype(bool)
    force = {'grey': np.isin(poly_piece, cfg['laces'])}
    # подошва: по высоте модели (src_co), бок - коричневый, низ - чёрный; латунная полоса на пятке - деталь (hard)
    sc = cfg['sole']
    pz = np.array([src_co[list(p.vertices), 2].max() for p in me.polygons])
    pnz = np.array([p.normal.z for p in me.polygons])
    on_main = poly_piece == cfg['main']
    force['leather'] = on_main & (pz < sc['side_z']) & (pnz >= sc['bottom_nz'])
    force['black'] = on_main & (pnz < sc['bottom_nz']) & (pz < sc['side_z'])

    # цвет граней модели: доля светлого и латуни
    lt_of, w_, tri_, poly_ = gear_texture.texel_map(me, S_)
    inside_px = lt_of >= 0
    pid = poly_[np.maximum(lt_of, 0)]
    rgb0 = px['base'][..., :3]
    _, _, _, lum0, sat0 = gear_texture.features(rgb0)
    nf_ = len(me.polygons)
    cnt = np.maximum(np.bincount(pid[inside_px], minlength=nf_), 1)
    whitish_px = (lum0 > 0.5) & (sat0 < 0.25)
    f_white = np.bincount(pid[inside_px & whitish_px], minlength=nf_) / cnt
    f_brass = np.bincount(pid[inside_px & gear_texture.brass_mask(rgb0, cfg['texture'], lum_min=True)],
                          minlength=nf_) / cnt
    windows = gear_texture.enclosed(me, f_brass > cfg['texture']['brass_share'], cfg['texture']['window_area'])
    pnx = np.array([p.normal.x for p in me.polygons]) * sgn
    shaft_f = on_main & (pz > sc['side_z']) & ~quality_poly & ~force['grey']

    def largest_patch(mask):
        """Самый большой связный (по рёбрам) кусок граней mask."""
        faces_of = {}
        for p in me.polygons:
            if mask[p.index]:
                for ek in p.edge_keys:
                    faces_of.setdefault(ek, []).append(p.index)
        seen_, best_ = set(), []
        for s0 in np.flatnonzero(mask):
            if s0 in seen_:
                continue
            stack, patch = [s0], []
            seen_.add(s0)
            while stack:
                a_ = stack.pop()
                patch.append(a_)
                for ek in me.polygons[a_].edge_keys:
                    for b_ in faces_of.get(ek, ()):
                        if b_ not in seen_:
                            seen_.add(b_)
                            stack.append(b_)
            if len(patch) > len(best_):
                best_ = patch
        return best_

    # Солнце изнутри (рисунок генератора на ткани: светлая середина, лучи, медный провод) - стирается: на картинке
    # солнце одно, снаружи, а изнутри кеды смотрят друг на друга. Середина - светлые пиксели граней, смотрящих
    # внутрь, вокруг неё на PLAIN_R грани - не детали; латунь и медь на остальной ткани изнутри - ткань
    PLAIN_R = 0.016
    inner = shaft_f & (pnx < -0.3)
    wpx = inside_px & whitish_px & inner[pid]
    plain = np.zeros(nf_, bool)
    if wpx.sum() > 50:
        V_ = np.asarray(co)
        pos_w = (V_[tri_[np.maximum(lt_of, 0)][wpx]] * w_[wpx][..., None]).sum(1)
        ic = np.median(pos_w, 0)
        fc = np.array([tuple(p.center) for p in me.polygons])
        # середина солнца - бугорок: его грани смотрят во все стороны, кроме наружу
        plain = shaft_f & (pnx < 0.3) & (np.linalg.norm(fc - ic, axis=1) < PLAIN_R)
        print(f'[Place] inner painted sun at ({ic[0] * 1000:.0f} {ic[1] * 1000:.0f} {ic[2] * 1000:.0f}) mm: '
              f'{int(plain.sum())} faces become cloth')
    force['fabric'] = plain | (inner & (f_white < 0.3))  # светлые грани изнутри - петли шнурков у люверсов
    # Серое - только шнурки и обмотка. Подкладка внутри голенища у модели в белёсых пятнах (на картинке тёмно-синяя):
    # грань смотрит внутрь ботинка, если луч по её нормали упирается в ботинок; дно чаши смотрит вверх
    shell_bvh = BVHTree.FromPolygons([Vector(p) for p in co], [tuple(p.vertices) for p in me.polygons if is_main[p.index]])
    lining = np.zeros(nf_, bool)
    for p in me.polygons:
        if shaft_f[p.index]:
            c_ = p.center + p.normal * 0.0005
            lining[p.index] = shell_bvh.ray_cast(c_, p.normal, 0.2)[0] is not None or \
                (p.normal.z > 0.6 and pz[p.index] > cfg['cap_src'] - 0.1)
    grey_poly = ~lining
    print(f'[Place] texture classes by faces: laces {int(force["grey"].sum())}, sole side {int(force["leather"].sum())}, '
          f'sole bottom {int(force["black"].sum())}, inner side cloth {int(force["fabric"].sum())}, '
          f'lining {int(lining.sum())}')
    cleaned = gear_texture.clean(me, co, poly_piece, quality_poly, px['base'], px['normal'], px['roughness'], S_,
                                 cfg['texture'], tag='[Place]', grey_poly=grey_poly, force_poly=force, plain_poly=plain)
    cleaned = [a.copy() for a in cleaned]

    # Солнечная панель в середине солнца: у модели там белый «циферблат» с трещинами, на картинке - тёмно-синяя
    # панель с линиями ячеек. Грани - окно в латунном кольце, светлые, смотрят наружу
    best = largest_patch(windows & (f_white > 0.5) & (pnx > 0.3) & shaft_f)
    panel_poly = np.zeros(nf_, bool)
    panel_poly[best] = True
    pv = co[sorted({i for f in best for i in me.polygons[f].vertices})]
    pc_ = pv.mean(0)
    pn = np.array(sum((me.polygons[f].normal * me.polygons[f].area for f in best), Vector()).normalized())
    e1 = np.cross([0, 0, 1], pn)
    e1 /= np.linalg.norm(e1)
    e2 = np.cross(pn, e1)
    rad = np.linalg.norm(pv - pc_, axis=1).max()
    print(f'[Place] solar panel: {len(best)} faces, center ({pc_[0] * 1000:.0f} {pc_[1] * 1000:.0f} {pc_[2] * 1000:.0f}) mm, '
          f'radius {rad * 1000:.1f} mm')
    m_px = inside_px & panel_poly[pid]
    V_ = np.asarray(co)
    pos = (V_[tri_[np.maximum(lt_of, 0)]] * w_[..., None]).sum(2)
    d_ = pos[m_px] - pc_
    x_, y_ = d_ @ e1, d_ @ e2
    pcfg = cfg['panel']
    texel = rad * 2 / max(np.sqrt(m_px.sum()), 1)
    lw = max(texel * 0.9, rad * 0.035)
    rows = (np.arange(pcfg['rows']) + 0.5) / pcfg['rows'] * 2 - 1
    line = np.min(np.abs(y_[:, None] / rad - rows[None, :]), 1) * rad < lw
    line |= np.abs(x_) < lw
    col = np.where(line[:, None], np.array(pcfg['line'], np.float32), np.array(pcfg['color'], np.float32))
    col *= (1.08 - 0.16 * (y_ / rad + 1) / 2)[:, None]  # свет сверху
    cleaned[0][m_px, :3] = col
    cleaned[1][m_px, :3] = (0.5, 0.5, 1.0)  # ровная панель: трещины «циферблата» - и в нормалях
    cleaned[2][m_px, :3] = 0.35
    # пустота развёртки вокруг панели - заново цветом панели (иначе мип-уровни подмешают белое)
    near = m_px.copy()
    for _ in range(12):
        near |= np.roll(near, 1, 0) | np.roll(near, -1, 0) | np.roll(near, 1, 1) | np.roll(near, -1, 1)
    gut = near & ~inside_px
    for a_ in cleaned:
        a_[..., :3] = gear_texture.bleed(a_[..., :3].copy(), ~gut, 12)
    view1 = bpy.data.images.new(name + '_clean', S_, S_, alpha=True)
    view1.pixels.foreach_set(cleaned[0].ravel())
    tex_nodes[base.name].image = view1
    if args.png:
        shoe_shots('tex_after')
    if args.texture_only:
        print('[Place] texture only: done')
        sys.exit(0)

# ----------------------------------------------------------------------------------------------
# Второй ботинок - отражение по X
# ----------------------------------------------------------------------------------------------
# Свои нормали углов граней из FBX (у SunShoes) при отражении не отражаются: второй ботинок выходил мятым. Нормаль
# угла запоминается по (грань, вершина) модели, у отражения - та же с -x
corner_n = None
if me.has_custom_normals:
    corner_n = {}
    for p in me.polygons:
        for li in p.loop_indices:
            corner_n[(p.index, me.loops[li].vertex_index)] = Vector(me.corner_normals[li].vector)
npoly_half = len(me.polygons)
bm = bmesh.new()
bm.from_mesh(me)
src_f = bm.faces.layers.int.new('src_f')
src_v = bm.verts.layers.int.new('src_v')
for f in bm.faces:
    f[src_f] = f.index
for vtx in bm.verts:
    vtx[src_v] = vtx.index
dup = bmesh.ops.duplicate(bm, geom=list(bm.verts) + list(bm.edges) + list(bm.faces))
new_verts = [g for g in dup['geom'] if isinstance(g, bmesh.types.BMVert)]
new_faces = [g for g in dup['geom'] if isinstance(g, bmesh.types.BMFace)]
for vtx in new_verts:
    vtx.co.x = -vtx.co.x
bmesh.ops.reverse_faces(bm, faces=new_faces)
bm.to_mesh(me)
bm.free()
me.update()
if corner_n is not None:
    sf = np.zeros(len(me.polygons), int)
    me.attributes['src_f'].data.foreach_get('value', sf)
    sv = np.zeros(len(me.vertices), int)
    me.attributes['src_v'].data.foreach_get('value', sv)
    loop_n = [None] * len(me.loops)
    for p in me.polygons:
        for li in p.loop_indices:
            n = corner_n[(sf[p.index], sv[me.loops[li].vertex_index])].copy()
            if p.index >= npoly_half:
                n.x = -n.x
            loop_n[li] = n
    me.normals_split_custom_set(loop_n)
    print(f'[Place] custom normals mirrored for the second boot ({len(loop_n)} corners)')
me.attributes.remove(me.attributes['src_f'])
me.attributes.remove(me.attributes['src_v'])
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

if args.png:
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

tex_dir = stem + '_Textures'
os.makedirs(tex_dir, exist_ok=True)


def pixels(img):
    img.scale(TEXTURE_SIZE, TEXTURE_SIZE)
    a = np.empty(TEXTURE_SIZE * TEXTURE_SIZE * 4, np.float32)
    img.pixels.foreach_get(a)
    return a.reshape(-1, 4)


def downsized(a):
    """Очищенная текстура (CLEAN_SIZE) - в TEXTURE_SIZE."""
    img = bpy.data.images.new('tmp', a.shape[1], a.shape[0], alpha=True, float_buffer=True)
    img.pixels.foreach_set(a.ravel())
    out_ = pixels(img)
    bpy.data.images.remove(img)
    return out_


def save(a, file_name, colorspace):
    img = bpy.data.images.new(file_name, TEXTURE_SIZE, TEXTURE_SIZE, alpha=True)
    img.colorspace_settings.name = colorspace
    img.pixels.foreach_set(a.ravel())
    img.filepath_raw = os.path.join(tex_dir, file_name)
    img.file_format = 'PNG'
    img.save()
    print(f'[Place] texture {img.filepath_raw}')


if cleaned is not None:
    save(downsized(cleaned[0]), f'{name}.png', 'sRGB')
    save(downsized(cleaned[1]), f'{name}_normal.png', 'Non-Color')
    ms = np.zeros((TEXTURE_SIZE * TEXTURE_SIZE, 4), np.float32)  # металла у модели нет, гладкость - из шероховатости
    ms[:, 3] = 1.0 - downsized(cleaned[2])[:, 0]
else:
    save(pixels(base), f'{name}.png', 'sRGB')
    save(pixels(normal), f'{name}_normal.png', 'Non-Color')
    ms = pixels(metallic)
    ms[:, 3] = 1.0 - pixels(roughness)[:, 0]
save(ms, f'{name}_metallic_smoothness.png', 'Non-Color')
if cfg.get('quality_material'):
    # маска цвета качества - по граням, которые пометил пользователь (обмотка у щиколотки), на обоих ботинках
    q = gear_texture.face_mask(me, face_flags('quality').astype(bool), TEXTURE_SIZE)
    save((np.repeat(q.reshape(-1, 1), 4, 1) * [1, 1, 1, 0] + [0, 0, 0, 1]).astype(np.float32), f'{name}_quality.png',
         'Non-Color')
    me.attributes.remove(me.attributes['quality'])
me.attributes.remove(me.attributes['piece'])

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
