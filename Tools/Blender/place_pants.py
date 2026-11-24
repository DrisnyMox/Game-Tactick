"""
Надевает меш штанов на тело Assets/Models/20261121003938_8b8745d5.fbx. Настройки штанов - PARTS:
  * rope - тканевые штаны ClothPantsRope с верёвочным поясом (базовый набор), штанины заправлены в ClothBoots;
  * nomad - штаны комплекта Островных Кочевников NomadPants: шорты до колена с подвёрнутой светло-серой манжетой,
    серый пояс-кушак, бирюзовые обмотки крест-накрест на голенях с узлами сзади. Колено голое.
  * sun - штаны комплекта Культа Солнца SunPants: во всю длину, индиго-чёрная ткань с узором солнц, серый пояс-повязка,
    повязки и обмотки (помечены пользователем материалом под цвет качества), латунные наколенники и панель на бедре.
    Вся ткань садится оболочкой, текстура чистится gear_texture.clean, маска цвета качества - по помеченным граням.

Запуск (из корня проекта):
    "E:\\Software\\Blender\\blender.exe" --background --factory-startup --python Tools/Blender/place_pants.py -- ^
        --src "<FBX штанов>" --out Assets/Models/Gear/ClothPantsRope.fbx [--part rope] [--gen "<модель генератора>"] [--png "<папка>"]

Источник - штаны, вырезанные пользователем в 3ds Max из модели генератора персонажа в штанах (ClothPantsRope -
20261121192122_1a909266.fbx, NomadPants - 20261123044328_6e1b4d19.fbx). Вершины в FBX - в координатах модели
генератора (масштаб 2,54 и сдвиг объекта от экспорта Max отбрасываются). Ставится сам меш штанов с его развёрткой
и текстурами:
  * у модели таз на 3 см впереди тела и уже, ноги ближе. Каждый срез штанов сдвигается на центр среза тела
    (ниже шага - каждой ноги, выше - таза) и расширяется, если тело ближе GAP к ткани. Сдвиг и расширение плавно
    меняются по высоте, у шага ноги плавно переходят в таз;
  * шорты nomad (shell) после сдвига садятся на тело оболочкой: длинные рёбра делятся (split), гладкая копия
    шорт (сглаживание Таубина) ставится на кожу на зазоре GAP..loose, сдвиг сглаживается по соседям, затем
    возвращаются складки, швы, карманы и манжета (разница меша и гладкой копии). Каждая вершина двигается своим
    сдвигом, но соседние - почти одинаково, поэтому форма деталей сохраняется. Пояс (follow) идёт за ближайшими
    вершинами шорт. Без этого сзади под шортами оставалось до 17 мм пустоты;
  * обмотки (nomad) облегают голень: под ними у модели генератора кожи нет, а голень тела тоньше и на 2 см дальше
    назад. Ось обмоток ставится на ось голени тела, срез сжимается или растягивается по секторам угла так, чтобы
    внутренняя сторона обмоток была на WRAP_GAP над кожей;
  * верх пояса над краем каждого нагрудника из chests прижимается внутрь нагрудника (на CHEST_CLEAR под его
    тканью), ниже края - плавно на RAMP: иначе пояс вылезает сквозь нагрудник;
  * низ штанин ниже cap сжимается к голени до ANKLE_GAP над кожей, выше плавно до ankle_top возвращается
    к ширине модели: штанина заправлена в ботинок и не торчит сквозь голенище;
  * веса - с ближайшей кожи тела, SKIN_BONES кости на вершину;
  * маска тела <out>_BodyMask.json: rope - вся кожа таза и ног до края пояса; nomad - кожа, которую штаны закрывают
    и которую не видно снаружи (колено, кожа у края шорт и между обмотками остаётся). Маска nomad считается только
    по штанам и телу: штаны надеваются и без нагрудника и ботинок;
  * текстуры модели уменьшаются до TEXTURE_SIZE, гладкость - из шероховатости, металла у модели нет; nomad: кожа
    модели на текстуре под лентами обмоток и вокруг них заливается цветом лент (иначе на голени розовые полосы);
Общие шаги посадки (оболочка, следование за тканью, отодвигание от кожи, текстура, маска) - в gear_fit.py.
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
import gear_texture  # noqa: E402
from gear_poses import RECORDED, SKIN_BONES, limit_bones, set_pose  # noqa: E402
from gear_fit import (add_covered, covered_triangles, fill_skin_on_texture, follow, neighbours,  # noqa: E402
                      push_faces_off_skin, push_skin_bumps, seen_from_outside, shell_fit, smoothstep, sphere_dirs,
                      split_long_edges, world_bm)

BODY_PATH = 'Assets/Models/20261121003938_8b8745d5.fbx'
GEAR = 'Assets/Models/Gear/'
ZONE = {'Hips', 'Spine', 'LeftUpperLeg', 'RightUpperLeg', 'LeftLowerLeg', 'RightLowerLeg'}
PARTS = {
    # chests - нагрудники, под край которых уходит пояс; boots - ботинки, в которые заправлен низ (первые - для снимков);
    # main - куски модели, по срезам которых считается сдвиг; wraps - ленты обмоток на голенях, knots - их узлы;
    # z_range - срезы модели выше и ниже рваные (край пояса, завязки у щиколотки): там крайние значения; widen_top -
    # до этой высоты срезы расширяются; max_widen - больше срез не растягивается; first_hit - расширять по первому
    # попаданию луча в ткань, а не по последнему (outer_dist); cap - ниже низ сжат к голени, выше плавно до ankle_top
    # возвращается к своей ширине; mask - какая кожа прячется
    'rope': dict(chests=('ClothChest',), boots=('ClothBoots',), main=('part_000',), wraps=(), knots=(),
                 z_range=(0.12, 0.62), widen_top=0.62, max_widen=1.25, first_hit=False,
                 cap=0.135, ankle_top=0.165, mask='below_waist', shell=None),
    # Чаша NomadBoots на 144 мм, край манжеты на 155 мм; у ClothBoots чаша на 135 мм и стенка у голени ближе: сжатие
    # ниже 145 мм годится обоим. Таз тела сзади больше, чем у модели: сзади у пояса срез растягивается до 1,45
    'nomad': dict(chests=('NomadChest', 'ClothChest'), boots=('NomadBoots', 'ClothBoots'), main=('part_003',),
                  wraps=('part_001', 'part_002'), knots=('part_007', 'part_008'),
                  z_range=(0.08, 0.57), widen_top=0.65, max_widen=1.5, first_hit=True,
                  cap=0.145, ankle_top=0.18, mask='unseen',
                  shell=dict(pieces=('part_003',), follow=('part_006',), split=0.016, loose=0.008)),
    # Культ Солнца: штаны во всю длину, part_001 - ткань с поясом-повязкой, повязками на бёдрах, обмотками голеней
    # и наколенниками одной сеткой, садится оболочкой целиком. Остальное идёт за тканью: панель на правом бедре
    # (part_005), пряжка (part_010), шнур с бусинами, провода, скобы. part_024..041 - кубики по 3 мм у низа штанин
    # (мусор выреза, под ботинком). Грани материала quality пользователь пометил под цвет качества (пояс, узел,
    # повязки, обмотки). Текстура чистится gear_texture.clean (texture)
    'sun': dict(chests=('NomadChest', 'ClothChest'), boots=('NomadBoots', 'ClothBoots'), main=('part_001',),
                wraps=(), knots=(), z_range=(0.10, 0.58), widen_top=0.65, max_widen=1.5, first_hit=True,
                cap=0.145, ankle_top=0.18, mask='unseen', drop=tuple(f'part_{k:03d}' for k in range(24, 42)),
                shell=dict(pieces=('part_001',), follow=('part_002', 'part_005', 'part_006', 'part_007', 'part_008',
                                                           'part_009', 'part_010', 'part_011', 'part_012', 'part_013',
                                                           'part_014', 'part_015', 'part_016', 'part_017', 'part_018',
                                                           'part_019', 'part_020', 'part_021', 'part_022', 'part_023'),
                           split=0.016, loose=0.008),
                # узел пояса сзади (part_015) и две ленточки из него (part_011, part_012) висят от пояса: веса с ближайшей
                # кожи (ягодицы, бёдра) вели их за ногами
                hips_only=('part_011', 'part_012', 'part_015'),
                hanging=dict(pieces=('part_011', 'part_012'), knot=('part_015',)),
                face_gap=True, quality_material='01 - Default',
                texture=dict(
                    quality_material='01 - Default',
                    # латунь и провода: тонкие линии, только цвет к картинке; panel - открытая коробка с окнами
                    hard=('part_005', 'part_006', 'part_007', 'part_008', 'part_010', 'part_013', 'part_014'),
                    panel=('part_005',), fabric_pieces=('part_001',),
                    # цвета - медианы по картинкам генератора SunPants_front/back.webp (ткань, серое, латунь, ремень)
                    palette=dict(fabric=(0.165, 0.178, 0.215), grey=(0.67, 0.645, 0.62), brass=(0.62, 0.49, 0.31),
                                 leather=(0.31, 0.224, 0.14), copper=(0.72, 0.36, 0.16), sun=(0.33, 0.35, 0.42)),
                    # сглаживание яркости (пикселей) и её пределы от медианы класса
                    shade=dict(fabric=(2.5, 0.75, 1.2), grey=(0.8, 0.5, 1.35), brass=(0.8, 0.5, 1.4),
                               leather=(1.0, 0.6, 1.3), copper=(0.5, 0.5, 1.4)),
                    brass_share=0.25, window_area=0.003, behind_reach=0.03, sun_clear=0.022,
                    sun_radius=0.012, sun_spacing=0.042, sun_alpha=0.7)),
}
GAP = 0.003            # ткань не ближе к коже, м (рост тела 1,088; в Unity умножается на 1,662)
SHELL_FLOOR = 0.002    # nomad: шорты после возврата складок не ближе к коже
SHELL_SMOOTH = 40      # проходов сглаживания шорт в оболочку (складки, карманы, манжета уходят в отступ)
SHELL_DISP_SMOOTH = 10  # проходов сглаживания сдвига оболочки по сетке
WRAP_GAP = 0.003       # внутренняя сторона обмоток над кожей
WRAP_FIT = (0.09, 0.26)  # по этим высотам ставится ось обмоток (ниже и выше - край, узлы)
CROTCH = (0.46, 0.52)  # ниже - срезы по ногам, выше - по тазу, между - плавный переход
CHEST_CLEAR = 0.0015   # верх пояса под тканью нагрудника на столько
WAIST_FLOOR = 0.0015   # nomad: и над кожей не меньше чем на столько
PASSES_WAIST = 3       # nomad: проходов, отодвигающих пояс от кожи по её нормали
POKE_CLEAR = 0.001     # nomad: кожа, вышедшая сквозь пояс, после отодвигания под ним на столько
POKE_RADIUS = 0.03     # nomad: на этом расстоянии от неё вершины пояса отодвигаются плавно
RAMP = 0.02            # ниже края нагрудника пояс возвращается к своей ширине на этой высоте
ANKLE_GAP = 0.002      # низ штанин в голенище - над кожей на столько (голенище от голени не ближе 4 мм)
ANKLE_LOW_GAP = 0.006  # nomad: у щиколотки ниже ANKLE_LOW[0] - на столько (плавно до ANKLE_LOW[1])
ANKLE_LOW = (0.105, 0.13)
TEXTURE_SIZE = 2048    # штаны на экране крупнее ботинок и перчаток
WRAP_FACE_GAP = 0.001  # nomad: точки внутри граней лент обмоток над кожей не ближе
SHELL_FACE_GAP = 0.001  # sun: и точки внутри граней ткани
HANG_GAP = 0.002       # sun: ленточки узла не ближе к ткани штанов
HANG_STEP = 0.002      # sun: ленточки отходят от ткани ступенями по высоте
HANG_MAX = 0.04        # sun: и не дальше
WRAP_BLEED = 6        # nomad: кожа на текстуре вокруг лент обмоток заливается на столько пикселей
STEP = 0.01            # шаг срезов по высоте
LEG_REACH = 0.085      # луч от центра ноги дальше этого уходит к другой ноге
SECTORS = 24           # расширение среза задаётся по секторам угла
MIN_WRAP = 0.70        # меньше срез обмоток не сжимается
WRAP_PASSES = 4        # проходов подгонки обмоток
SMOOTH = 1.5           # сглаживание сдвига и расширения по высоте, шагов
HIDE_BONES = ZONE      # маска nomad: кожа этих костей, закрытая штанами

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
parser = argparse.ArgumentParser()
parser.add_argument('--src', required=True)
parser.add_argument('--out', required=True)
parser.add_argument('--part', default='rope', choices=sorted(PARTS))
parser.add_argument('--gen', default='')
parser.add_argument('--png', default='')
args = parser.parse_args(argv)
src, out = os.path.abspath(args.src), os.path.abspath(args.out)
stem = os.path.splitext(out)[0]
name = os.path.basename(stem)
cfg = PARTS[args.part]
Z_RANGE, CAP_Z, ANKLE_TOP, WIDEN_TOP = cfg['z_range'], cfg['cap'], cfg['ankle_top'], cfg['widen_top']
MAX_WIDEN = cfg['max_widen']  # больше срез не растягивается

bpy.ops.wm.read_factory_settings(use_empty=True)


def import_new(path):
    before = set(bpy.data.objects)
    bpy.ops.import_scene.fbx(filepath=os.path.abspath(path))
    return [o for o in bpy.data.objects if o not in before]


# ----------------------------------------------------------------------------------------------
# Тело, нагрудники, ботинки и штаны
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
    mat = bpy.data.materials.new(os.path.basename(stem_))
    mat.use_nodes = True
    node = mat.node_tree.nodes.new('ShaderNodeTexImage')
    node.image = bpy.data.images.load(os.path.join(stem_ + '_Textures', os.path.basename(stem_) + '.png'))
    mat.node_tree.links.new(node.outputs['Color'], mat.node_tree.nodes['Principled BSDF'].inputs['Base Color'])
    mesh.data.materials.clear()
    mesh.data.materials.append(mat)
    return mesh


def mask_of(part):
    """Вершины тела под частью - по её маске."""
    with open(os.path.abspath(GEAR + part + '_BodyMask.json'), encoding='utf-8') as f:
        pts = np.array(json.load(f)['hidden']).reshape(-1, 3)
    return {int(np.argmin(((body_rest - p) ** 2).sum(1))) for p in pts}


chests = []  # (имя, объект, BVH, точки нижнего края)
for part in cfg['chests']:
    o = worn(GEAR + part + '.fbx')
    cbm = world_bm(o)
    bottom = np.array([tuple(v.co) for e in cbm.edges if len(e.link_faces) == 1 for v in e.verts if v.co.z < 0.66])
    chests.append((part, o, BVHTree.FromBMesh(cbm), bottom))
    cbm.free()
boots = []  # (имя, объект, BVH)
for part in cfg['boots']:
    o = worn(GEAR + part + '.fbx')
    bbm_ = world_bm(o)
    boots.append((part, o, BVHTree.FromBMesh(bbm_)))
    bbm_.free()

# Вершины штанов - в координатах модели (Y вверх) -> Z вверх Blender, как у тела. Номер куска каждой вершины
# хранится в атрибуте: после объединения кусков иначе не узнать, где обмотки
pieces = [o for o in import_new(src) if o.type == 'MESH']
for o in [o for o in bpy.data.objects if o.type == 'EMPTY']:
    bpy.data.objects.remove(o)
for o in [o for o in pieces if o.name in cfg.get('drop', ())]:
    pieces.remove(o)
    bpy.data.objects.remove(o)
piece_names = sorted(o.name for o in pieces)
for o in pieces:
    o.parent = None
    o.data.transform(Matrix.Rotation(math.radians(90), 4, 'X'))
    o.matrix_world = Matrix.Identity(4)
    attr = o.data.attributes.new('piece', 'INT', 'POINT')
    attr.data.foreach_set('value', [piece_names.index(o.name)] * len(o.data.vertices))
if cfg['shell']:
    # Посадка оболочкой (nomad): сзади у шорт ряды вершин через 3 см, ткань не может прогнуться под поясницу
    for o in pieces:
        if o.name in cfg['shell']['pieces']:
            n_ = split_long_edges(o, cfg['shell']['split'])
            o.data.attributes['piece'].data.foreach_set('value', [piece_names.index(o.name)] * n_)
            print(f'[Pants] {o.name}: verts after splitting long edges {n_}')


def mesh_arrays(names):
    """Вершины и рёбра кусков names одной сеткой."""
    V, E = [], []
    for o in pieces:
        if o.name in names:
            E.append(np.array([tuple(e.vertices) for e in o.data.edges]) + sum(len(v) for v in V))
            V.append(np.array([tuple(v.co) for v in o.data.vertices]))
    return np.concatenate(V), np.concatenate(E)


main_co, main_edges = mesh_arrays(cfg['main'])
if cfg['wraps']:
    wrap_co, wrap_edges = mesh_arrays(cfg['wraps'])
main_piece = next(o for o in pieces if o.name == cfg['main'][0])
bpy.ops.object.select_all(action='DESELECT')
for o in pieces:
    o.select_set(True)
bpy.context.view_layer.objects.active = main_piece
bpy.ops.object.join()
pants = main_piece
pants.name = name
me = pants.data
piece_of = np.empty(len(me.vertices), int)
me.attributes['piece'].data.foreach_get('value', piece_of)
is_band = np.isin(piece_of, [piece_names.index(n) for n in cfg['wraps']])
# узлы обмоток идут вместе с обмотками, но луч по ним не меряется: они снаружи лент
is_wrap = is_band | np.isin(piece_of, [piece_names.index(n) for n in cfg['knots']])
shell_pieces = cfg['shell']['pieces'] if cfg['shell'] else ()
is_shell = np.isin(piece_of, [piece_names.index(n) for n in shell_pieces])
is_follow = np.isin(piece_of, [piece_names.index(n) for n in (cfg['shell']['follow'] if cfg['shell'] else ())])
is_hips_only = np.isin(piece_of, [piece_names.index(n) for n in cfg.get('hips_only', ())])
me.attributes.remove(me.attributes['piece'])
src_co_pre = np.array([tuple(v.co) for v in me.vertices])
# Грани под цвет качества пользователь пометил отдельным материалом (sun): запоминаются, затем у штанов один материал
# с текстурами - иначе в Unity две части меша
poly_piece = np.array(piece_names)[piece_of[[p.vertices[0] for p in me.polygons]]]
mat_names = [m.name if m else '' for m in me.materials]
quality_poly = np.array([mat_names[p.material_index] == cfg.get('quality_material') for p in me.polygons])
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
tex_dir = stem + '_Textures'


def pixels(img):
    img = img.copy()
    img.scale(TEXTURE_SIZE, TEXTURE_SIZE)
    a = np.empty(TEXTURE_SIZE * TEXTURE_SIZE * 4, np.float32)
    img.pixels.foreach_get(a)
    bpy.data.images.remove(img)
    return a.reshape(-1, 4)


def save(a, file_name, colorspace):
    os.makedirs(tex_dir, exist_ok=True)
    img = bpy.data.images.new(file_name, TEXTURE_SIZE, TEXTURE_SIZE, alpha=True)
    img.colorspace_settings.name = colorspace
    img.pixels.foreach_set(a.ravel())
    img.filepath_raw = os.path.join(tex_dir, file_name)
    img.file_format = 'PNG'
    img.save()
    print(f'[Pants] texture {img.filepath_raw}')
    return img


tex_px = {'base': pixels(base), 'normal': pixels(normal), 'roughness': pixels(roughness)}
if 'texture' in cfg:
    # sun: текстура модели - целого персонажа, на штанах шум, царапины, пятна кожи (gear_texture.clean). Чистится до
    # посадки: узор солнц рисуется по форме модели, а снимки --png показывают уже чистую текстуру
    S_ = TEXTURE_SIZE
    cleaned = gear_texture.clean(me, src_co_pre, poly_piece, quality_poly, *(tex_px[k].reshape(S_, S_, 4) for k in
                                 ('base', 'normal', 'roughness')), S_, cfg['texture'], tag='[Pants]')
    tex_px = {k: a.reshape(-1, 4) for k, a in zip(('base', 'normal', 'roughness'), cleaned)}
    view = bpy.data.images.new(name + '_clean', S_, S_, alpha=True)
    view.pixels.foreach_set(tex_px['base'].ravel())
    tex_nodes[base.name].image = view
gen_view = pants.copy()  # модель как есть - для снимков рядом
gen_view.data = me.copy()
bpy.context.scene.collection.objects.link(gen_view)
src_co = np.array([tuple(v.co) for v in me.vertices])
print(f'[Pants] {args.part}: {len(pieces)} pieces {piece_names}, verts {len(src_co)}, wraps {is_wrap.sum()}')
gen_body = []
if args.gen:
    gen_body = [o for o in import_new(args.gen) if o.type == 'MESH']
    for o in gen_body:
        o.parent = None
        o.data.transform(Matrix.Rotation(math.radians(90), 4, 'X'))
        o.matrix_world = Matrix.Identity(4)


def cut(V, E, z):
    """Точки пересечения рёбер с плоскостью на высоте z."""
    a, b = V[E[:, 0]], V[E[:, 1]]
    m = (a[:, 2] - z) * (b[:, 2] - z) < 0
    t = ((z - a[m, 2]) / (b[m, 2] - a[m, 2]))[:, None]
    return a[m] + (b[m] - a[m]) * t


GROUPS = {'L': lambda x: x > 0, 'R': lambda x: x < 0, 'T': lambda x: np.ones_like(x, bool)}
levels = np.arange(0.06, 0.70 + 1e-6, STEP)
marks = [i for i, z in enumerate(levels) if round(z * 100) % 10 == 0]


def mid(P):
    return (P[:, :2].min(0) + P[:, :2].max(0)) / 2


def circle_center(P):
    """Центр окружности по точкам среза (наименьшие квадраты): у обмоток срез - дуги лент крест-накрест, и середина
    рамки по ним уходит в сторону той ленты, что ниже."""
    A = np.c_[P[:, 0], P[:, 1], np.ones(len(P))]
    sol = np.linalg.lstsq(A, -(P[:, 0] ** 2 + P[:, 1] ** 2), rcond=None)[0]
    return np.array([-sol[0] / 2, -sol[1] / 2])


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
# Сдвиг срезов на центр тела. Обмотки - по оси голени: прямая через центры окружностей срезов обмоток
# на ось голени тела (прямая через центры окружностей срезов голени)
# ----------------------------------------------------------------------------------------------
wrap_axis = {}
if cfg['wraps']:
    for g, pick in (('L', GROUPS['L']), ('R', GROUPS['R'])):
        zs, cw, cb_ = [], [], []
        for z in np.arange(WRAP_FIT[0], WRAP_FIT[1] + 1e-6, STEP):
            b = cut(body_rest, body_edges, z)
            p = cut(wrap_co, wrap_edges, z)
            b, p = b[pick(b[:, 0])], p[pick(p[:, 0])]
            if len(b) < 6 or len(p) < 6:
                continue
            zs.append(z)
            cw.append(circle_center(p))
            cb_.append(circle_center(b))
        zs, cw, cb_ = np.array(zs), np.array(cw), np.array(cb_)
        fit = lambda C: [np.polyfit(zs, C[:, k], 1) for k in range(2)]
        wrap_axis[g] = (fit(cb_), fit(cw))
        bz, wz = wrap_axis[g]
        print(f'[Pants] {g} shin axis: body ({np.polyval(bz[0], 0.1) * 1000:.0f} {np.polyval(bz[1], 0.1) * 1000:.0f})'
              f'..({np.polyval(bz[0], 0.25) * 1000:.0f} {np.polyval(bz[1], 0.25) * 1000:.0f}), wraps '
              f'({np.polyval(wz[0], 0.1) * 1000:.0f} {np.polyval(wz[1], 0.1) * 1000:.0f})'
              f'..({np.polyval(wz[0], 0.25) * 1000:.0f} {np.polyval(wz[1], 0.25) * 1000:.0f}) mm at z 0.10..0.25')

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
# Обмотки - отдельные группы Lw и Rw: со срезами шорт через голое колено они не смешиваются (сглаживание по высоте
# растягивало верхнюю ленту обмоток по манжете шорт)
for g, (bz, wz) in wrap_axis.items():
    center_b[g + 'w'] = tuple(np.polyval(bz[k], levels) for k in range(2))
    shift[g + 'w'] = tuple(np.polyval(bz[k], levels) - np.polyval(wz[k], levels) for k in range(2))
for g in shift:
    print(f'[Pants] {g} shift (mm) by height: ' +
          ', '.join(f'{levels[i]:.1f}:({shift[g][0][i] * 1000:+.0f} {shift[g][1][i] * 1000:+.0f})' for i in marks))


def at(arr, z):
    return np.interp(z, levels, arr)


def blend_weights(P):
    """Доли (правая нога, левая нога, таз) для точек P: ноги плавно по x у шага, таз плавно по высоте."""
    wt = smoothstep(CROTCH[0], CROTCH[1], P[:, 2])
    wl = smoothstep(-0.02, 0.02, P[:, 0])
    return (1 - wt) * (1 - wl), (1 - wt) * wl, wt


def group_of(i, p):
    """Группа срезов вершины i в точке p: таз, нога или обмотка на ноге."""
    if blend_weights(p[None])[2][0] > 0.5:
        return 'T'
    return ('L' if p[0] > 0 else 'R') + ('w' if is_wrap[i] else '')


def transform(P, scale=None):
    out = np.zeros_like(P)
    for g, w in zip('RLT', blend_weights(P)):
        gw = np.where(is_wrap, g + 'w', g) if g + 'w' in shift else np.full(len(P), g)
        for key in set(gw):
            m = gw == key
            z = P[m, 2]
            q = P[m].copy()
            q[:, 0] += at(shift[key][0], z)
            q[:, 1] += at(shift[key][1], z)
            if scale is not None:
                c = np.stack([at(center_b[key][0], z), at(center_b[key][1], z)], 1)
                d = q[:, :2] - c
                s = lookup(scale[key], z, np.arctan2(d[:, 0], -d[:, 1]))[:, None]
                q[:, :2] = c + d * s
            out[m] += q * w[m, None]
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


def circular_fill(row):
    """Секторы без данных (луч прошёл между лентами обмоток) - по соседним по кругу."""
    good = ~np.isnan(row)
    if not good.any():
        return row
    k = np.flatnonzero(good)
    return np.interp(np.arange(SECTORS), np.r_[k - SECTORS, k, k + SECTORS], np.r_[row[k], row[k], row[k]])


# ----------------------------------------------------------------------------------------------
# Расширение: где кожа ближе GAP к ткани, срез растягивается от центра в эту сторону. Таблица по высоте
# и по сектору угла, плавно по обоим: отдельные вершины не двигаются, только срез целиком. Одно расширение
# на весь срез раздуло бёдра в стороны: его задавали ягодицы по диагонали. Обмотки (Lw, Rw) и сжимаются:
# их внутренняя сторона (первое попадание) ложится на WRAP_GAP над кожей
# ----------------------------------------------------------------------------------------------
me.calc_loop_triangles()
tris = [tuple(t.vertices) for t in me.loop_triangles]
shifted = transform(src_co)
# у шорт - без обмоток: луч от центра бедра на высоте голени иначе находил бы обмотки
bvh = BVHTree.FromPolygons([Vector(p) for p in shifted], [t for t in tris if not any(is_wrap[list(t)])])
if cfg['wraps']:
    wrap_bvh = BVHTree.FromPolygons([Vector(p) for p in shifted], [t for t in tris if all(is_band[list(t)])])
    wrap_z = (src_co[is_band, 2].min() - STEP, src_co[is_band, 2].max() + STEP)


def outer_dist(c, d, reach, first=False):
    """Расстояние до ткани по лучу от центра среза. rope - последнее попадание (внешняя ткань): первое бывает
    изнанкой узла верёвки или заклёпки внутри ткани. nomad - первое: последнее у бедра было клапаном кармана поверх
    шорт, срез растягивался по нему, и кожа таза выходила сквозь шорты под карманом на 2 мм. У шага и ниже - тоже
    последнее: первым там попадается внутренняя сторона шорт между ног."""
    last, start, left = None, c, reach
    while True:
        hit = bvh.ray_cast(start, d, left)
        if hit[0] is None or (last is not None and first):
            return last
        last = (hit[0] - c).length
        start = hit[0] + d * 1e-5
        left = reach - last


def finish(need):
    """Строки без данных - по ближайшему срезу; по кругу не меньше соседних секторов, затем сглаживание; по высоте -
    так же."""
    rows = np.flatnonzero(~np.isnan(need[:, 0]))
    need = need[[rows[np.argmin(np.abs(rows - i))] for i in range(len(levels))]]
    need = np.array([circular_smooth(np.maximum(np.maximum(r, np.roll(r, 1)), np.roll(r, -1))) for r in need])
    need = np.array([need[max(0, i - 2):i + 3].max(0) for i in range(len(need))])
    return np.stack([table(list(need[:, k])) for k in range(SECTORS)], 1)


scale = {}
for g in GROUPS:
    pick = GROUPS[g]
    need = np.full((len(levels), SECTORS), np.nan)
    for i, z in enumerate(levels):
        use = (z >= CROTCH[0]) if g == 'T' else (z <= CROTCH[1])
        if not use or not (Z_RANGE[0] <= z <= WIDEN_TOP):
            continue
        b = cut(body_rest, body_edges, z)
        b = b[pick(b[:, 0])]
        c = Vector((center_b[g][0][i], center_b[g][1][i], z))
        need[i] = 1.0
        hits = 0
        for q in b:
            d = Vector((q[0] - c.x, q[1] - c.y, 0))
            if d.length < 1e-4:
                continue
            k = int((math.atan2(d.x, -d.y) + math.pi) / (2 * math.pi) * SECTORS) % SECTORS
            r = outer_dist(c, d.normalized(), 0.3 if g == 'T' else LEG_REACH, cfg['first_hit'] and z > CROTCH[1])
            if r is None:
                continue
            hits += 1
            need[i, k] = max(need[i, k], min((d.length + GAP) / r, MAX_WIDEN))
        if not hits and z > Z_RANGE[1]:
            need[i] = np.nan  # выше края пояса ткани нет: верх пояса берёт расширение последнего среза с тканью
    scale[g] = finish(need)
    print(f'[Pants] {g} widen by height (min..max over sides): ' +
          ', '.join(f'{levels[i]:.2f}:{scale[g][i].min():.2f}..{scale[g][i].max():.2f}' for i in range(0, len(levels), 3)))


# Обмотки: внутренняя сторона лент (первое попадание луча от оси голени) - на WRAP_GAP над кожей, срез и сжимается,
# и растягивается. Сглаживание срезает пики, поэтому несколько проходов: каждый поправляет то, что осталось
# после предыдущего (как отодвигание в place_chest.py). С одним проходом и запасом «не меньше соседей» верхняя лента
# у колена вставала на 1 см впереди голени: колено у модели генератора на 2,5 см впереди оси голени, у тела - на 1,2
def wrap_positions(g, tab):
    m = is_wrap & GROUPS[g[0]](src_co[:, 0])
    P = src_co.copy()
    z = P[m, 2]
    q = P[m].copy()
    q[:, 0] += at(shift[g][0], z)
    q[:, 1] += at(shift[g][1], z)
    c = np.stack([at(center_b[g][0], z), at(center_b[g][1], z)], 1)
    d = q[:, :2] - c
    q[:, :2] = c + d * lookup(tab, z, np.arctan2(d[:, 0], -d[:, 1]))[:, None]
    P[m] = q
    return P


band_tris = [t for t in tris if all(is_band[list(t)])]
for g in [g + 'w' for g in wrap_axis]:
    pick = GROUPS[g[0]]
    tab = np.ones((len(levels), SECTORS))
    for it in range(WRAP_PASSES):
        tree = BVHTree.FromPolygons([Vector(p) for p in wrap_positions(g, tab)], band_tris)
        ratio = np.full((len(levels), SECTORS), np.nan)
        for i, z in enumerate(levels):
            if not (wrap_z[0] <= z <= wrap_z[1]):
                continue
            b = cut(body_rest, body_edges, z)
            b = b[pick(b[:, 0])]
            c = Vector((center_b[g][0][i], center_b[g][1][i], z))
            for q in b:
                d = Vector((q[0] - c.x, q[1] - c.y, 0))
                if d.length < 1e-4:
                    continue
                k = int((math.atan2(d.x, -d.y) + math.pi) / (2 * math.pi) * SECTORS) % SECTORS
                hit = tree.ray_cast(c, d.normalized(), LEG_REACH)
                if hit[0] is not None:
                    v = (d.length + WRAP_GAP) / hit[3]
                    ratio[i, k] = v if np.isnan(ratio[i, k]) else max(ratio[i, k], v)
        worst = np.nanmax(ratio)
        # сектор без попадания (лента на этой высоте с другой стороны: верхняя лента сзади выше, чем спереди) - по
        # ближайшему срезу в том же секторе. По кругу от других сторон заполнять нельзя: спереди у колена бралось
        # растяжение икры сзади, и верхняя лента вставала на 5-10 мм от голени
        for k in range(SECTORS):
            have = np.flatnonzero(~np.isnan(ratio[:, k]))
            if len(have):
                ratio[:, k] = ratio[have[np.argmin(np.abs(have[None, :] - np.arange(len(levels))[:, None]), 1)], k]
        ratio = np.array([circular_fill(r) for r in ratio])
        ratio[np.isnan(ratio)] = 1.0
        # пик не срезается: не меньше соседних секторов и срезов (иначе сглаживание с соседями, которые сжимаются,
        # каждый проход возвращало одно и то же)
        ratio = np.array([circular_smooth(np.maximum(np.maximum(r, np.roll(r, 1)), np.roll(r, -1))) for r in ratio])
        ratio = np.array([ratio[max(0, i - 1):i + 2].max(0) for i in range(len(ratio))])
        ratio = np.stack([table(list(ratio[:, k])) for k in range(SECTORS)], 1)
        tab = np.clip(tab * ratio, MIN_WRAP, MAX_WIDEN)
        print(f'[Pants] {g} pass {it + 1}: worst ratio before {worst:.3f}')
    scale[g] = tab
    print(f'[Pants] {g} widen by height (min..max over sides): ' +
          ', '.join(f'{levels[i]:.2f}:{scale[g][i].min():.2f}..{scale[g][i].max():.2f}' for i in range(0, len(levels), 3)
                    if levels[i] <= wrap_z[1]))
co = transform(src_co, scale)
if cfg['shell']:
    # Посадка оболочкой (nomad, gear_fit.shell_fit): срезы по высоте не могут прогнуться под поясницу, шорты сзади
    # стояли от тела на несколько сантиметров. Шорты - от положения после сдвига срезов без расширения: расширение
    # делает сама посадка. Кушак поверх шорт идёт за ближайшими вершинами шорт
    idx = np.flatnonzero(is_shell)
    start = transform(src_co)
    sbm = world_bm(body)
    skin_bvh = BVHTree.FromBMesh(sbm)
    sbm.free()
    fitted = shell_fit(start[idx], neighbours([tuple(e.vertices) for e in me.edges], idx), skin_bvh,
                       GAP, cfg['shell']['loose'], SHELL_FLOOR, SHELL_SMOOTH, SHELL_DISP_SMOOTH, tag='[Pants]')
    fol = np.flatnonzero(is_follow)
    co[fol] = follow(start[idx], fitted, start[fol])
    co[idx] = fitted

# ----------------------------------------------------------------------------------------------
# Верх пояса - под нагрудниками, низ штанин - в голенище
# ----------------------------------------------------------------------------------------------


def chest_edge_z(bottom, a):
    """Нижний край нагрудника в стороне a (угол от -Y к +X): самая низкая из 12 точек края, ближайших по углу."""
    ang = np.arctan2(bottom[:, 0], -bottom[:, 1])
    d = np.abs((ang - a + np.pi) % (2 * np.pi) - np.pi)
    return bottom[np.argsort(d)[:12], 2].min()


body_bm = world_bm(body)  # с пересчётом нормалей: после transform у граней остаются нормали до поворота
body_bvh = BVHTree.FromBMesh(body_bm)
body_bm.free()
if cfg['first_hit']:
    # nomad: пояс над кожей по нормали кожи. Верх пояса сзади лежит на склоне ягодиц (кожа смотрит назад и вверх),
    # срез по горизонтальному лучу этого не видит: верх пояса уходил под кожу на 3,7 мм. Нужный сдвиг - по
    # ближайшей точке кожи, как в measure_gear.py, таблицей по высоте и сектору угла (вершины по одной не двигаются),
    # несколько проходов: сглаживание срезает пик
    for it in range(PASSES_WAIST):
        need = np.zeros((len(levels), SECTORS))
        cy = at(center_b['T'][1], co[:, 2])
        for i, p in enumerate(co):
            if group_of(i, p) != 'T' or p[2] < CROTCH[1]:
                continue
            loc, n, _, _ = body_bvh.find_nearest(Vector(p), 0.03)
            if loc is None:
                continue
            d = (Vector(p) - loc).dot(n)
            if d >= WAIST_FLOOR:
                continue
            radial = Vector((p[0], p[1] - cy[i], 0)).normalized()
            k = int((math.atan2(radial.x, -radial.y) + math.pi) / (2 * math.pi) * SECTORS) % SECTORS
            li = int(round((p[2] - levels[0]) / STEP))
            need[li, k] = max(need[li, k], min((WAIST_FLOOR - d) / max(radial.dot(n), 0.3), 0.02))
        worst = need.max()
        need = np.array([circular_smooth(np.maximum(np.maximum(r, np.roll(r, 1)), np.roll(r, -1))) for r in need])
        need = np.array([need[max(0, i - 1):i + 2].max(0) for i in range(len(need))])
        need = np.stack([table(list(need[:, k])) for k in range(SECTORS)], 1)
        for i, p in enumerate(co):
            if group_of(i, p) != 'T':
                continue
            radial = np.array([p[0], p[1] - cy[i]])
            r = np.linalg.norm(radial)
            co[i, :2] += radial / max(r, 1e-6) * lookup(need, p[2:3], np.array([math.atan2(radial[0], -radial[1])]))[0]
        print(f'[Pants] waist pass {it + 1}: push over the skin needed up to {worst * 1000:.1f} mm')
tucked_waist = tucked_ankle = 0
for i in range(len(co)):
    p = co[i]
    g = group_of(i, p)
    c = Vector((at(center_b[g][0], p[2]), at(center_b[g][1], p[2]), p[2]))
    d = Vector((p[0] - c.x, p[1] - c.y, 0))
    r = d.length
    if r < 1e-5:
        continue
    d.normalize()
    if g == 'T' and p[2] > 0.5:
        tucked, r0 = False, r
        for _, _, chest_bvh, bottom in chests:
            edge = chest_edge_z(bottom, math.atan2(d.x, -d.y))
            b = float(smoothstep(edge - RAMP, edge, p[2]))
            hit = chest_bvh.ray_cast(c, d, 0.3)
            if b > 0 and hit[0] is not None and r > hit[3] - CHEST_CLEAR:
                r += (hit[3] - CHEST_CLEAR - r) * b
                if cfg["first_hit"]:
                    # nomad: не ближе WAIST_FLOOR к коже по её нормали - пояс носится и без нагрудника. Сзади нижний
                    # край ClothChest ближе к коже, чем толщина кушака: там кушак прижимается, но не в кожу
                    loc, n, _, _ = body_bvh.find_nearest(Vector(p), 0.03)
                    if loc is not None:
                        room = max(0.0, (Vector(p) - loc).dot(n) - WAIST_FLOOR) / max(d.dot(n), 0.3)
                        r = max(r, r0 - room)
                tucked = True
        if tucked:
            co[i, :2] = np.array(c.xy) + np.array(d.xy) * r
            tucked_waist += 1
    if g != 'T' and p[2] < ANKLE_TOP:
        b = 1.0 - float(smoothstep(CAP_Z - 0.005, ANKLE_TOP, p[2]))
        hit = body_bvh.ray_cast(c, d, 0.2)
        if b > 0 and hit[0] is not None:
            # у щиколотки (ниже ANKLE_LOW) - больше места: грани кожи там по 44 мм, нижняя вершина на стопе, и при
            # повороте стопы кожа выходила сквозь нижнюю ленту (стоя на 1,4 мм, с шагом вбок в прицеле на 3,8 мм)
            low = 1.0 - float(smoothstep(ANKLE_LOW[0], ANKLE_LOW[1], p[2])) if cfg["wraps"] else 0.0
            gap = ANKLE_GAP + (ANKLE_LOW_GAP - ANKLE_GAP) * low
            r_new = r + (hit[3] + gap - r) * b
            co[i, :2] = np.array(c.xy) + np.array(d.xy) * r_new
            tucked_ankle += 1
print(f'[Pants] waist tucked under the chest: {tucked_waist} verts, legs tucked into boots: {tucked_ankle} verts')
if cfg['shell']:
    # Кушак крупными гранями (221 вершина): после посадки шорт оболочкой бугор кожи на пояснице проходил между его
    # вершинами насквозь на 1-2 мм (серые пятна сзади, видно без нагрудника). Делить кушак нельзя: мелкие куски
    # прижатого под нагрудник края торчали зубцами сквозь рваный край ClothChest. Каждая точка кожи снаружи ткани
    # отодвигает вершины пояса рядом наружу по своей нормали (gear_fit.push_skin_bumps).
    bbm1 = world_bm(body)
    body_n = np.array([tuple(v.normal) for v in bbm1.verts])
    bbm1.free()
    at_waist = [i for i, p in enumerate(body_rest) if main_bone[i] in ZONE and p[2] >= 0.55]
    push_skin_bumps(co, np.flatnonzero(co[:, 2] > 0.5), tris, body_rest[at_waist], body_n[at_waist],
                    POKE_CLEAR, POKE_RADIUS, tag='[Pants]')
if cfg['wraps']:
    # Ленты обмоток крупными гранями: между вершинами плоская грань провисает к оси голени, и кожа у щиколотки
    # проходила сквозь неё - щели на нижней ленте (gear_fit.push_faces_off_skin)
    push_faces_off_skin(co, [t for t in tris if all(is_wrap[k] for k in t)], body_bvh, WRAP_FACE_GAP,
                        tag='[Pants]', what='wrap faces')
if cfg.get('face_gap'):
    # sun: ткань крупными гранями (после деления до 16 мм) - между вершинами грань провисает к коже
    # ниже ANKLE_TOP низ штанин в голенище: ближайшая кожа там - верх стопы, и край штанины уходил по её нормали
    # плоским кольцом сквозь ботинок
    push_faces_off_skin(co, [t for t in tris if all(is_shell[k] and co[k][2] > ANKLE_TOP for k in t)], body_bvh,
                        SHELL_FACE_GAP, tag='[Pants]', what='cloth faces')
if cfg.get('hanging'):
    # sun: ленточки узла висят отвесно, как на модели. Вслед за тканью по вершинам они повторяли форму ягодиц до
    # самого низа и смотрелись приклеенными. Здесь каждая сдвигается целиком, как узел, затем по высоте отходит
    # назад (+Y) настолько, чтобы не заходить в ткань и кожу, и ниже не возвращается: лента лежит на ягодице до
    # самой выпуклой точки, дальше висит отвесно
    knot = np.isin(piece_of, [piece_names.index(n) for n in cfg['hanging']['knot']])
    move = co[knot].mean(0) - src_co[knot].mean(0)
    cloth_bvh = BVHTree.FromPolygons([Vector(p) for p in co], [t for t in tris if all(is_shell[k] for k in t)])

    def clear_of(P):
        # внутри - если сзади (+Y) от точки ещё есть ткань или кожа. По нормали ближайшей грани нельзя: у пояса
        # несколько слоёв ткани, ближайшим оказывался внутренний, и ленточка уходила назад до предела
        back = Vector((0, 1, 0))
        for p in P:
            for bvh, gap in ((cloth_bvh, HANG_GAP), (body_bvh, GAP)):
                if bvh.ray_cast(Vector(p), back, 0.1)[0] is not None:
                    return False
                loc, _, _, _ = bvh.find_nearest(Vector(p), gap)
                if loc is not None:
                    return False
        return True

    for pname in cfg['hanging']['pieces']:
        m = np.flatnonzero(piece_of == piece_names.index(pname))
        P = src_co[m] + move
        top = P[:, 2].max()
        level = ((top - P[:, 2]) / HANG_STEP).astype(int)
        need = np.zeros(level.max() + 1)
        for li in range(len(need)):
            at_level = P[level == li]
            while need[li] < HANG_MAX and not clear_of(at_level + [0, need[li], 0]):
                need[li] += 0.001
        need = np.maximum.accumulate(need)  # сверху вниз: отошедшая лента назад не возвращается
        co[m] = P + np.stack([np.zeros(len(P)), need[level], np.zeros(len(P))], 1)
        print(f'[Pants] {pname} hangs from the knot: back from the knot by height (mm) ' +
              ', '.join(f'{li * HANG_STEP * 1000:.0f}:{need[li] * 1000:.0f}' for li in range(0, len(need), 4)))
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
# Проверки в покое: обмотки над кожей, низ штанин внутри голенища каждого ботинка, пояс под каждым нагрудником
# ----------------------------------------------------------------------------------------------
fit_bm = bmesh.new()
fit_bm.from_mesh(me)
fit_bm.normal_update()
fit_normal = np.array([tuple(v.normal) for v in fit_bm.verts])
fit_bm.free()
fit_bvh = BVHTree.FromPolygons([Vector(p) for p in co], tris)
if cfg['wraps']:
    dist = []
    for i in np.flatnonzero(is_band):
        loc, n, _, _ = body_bvh.find_nearest(Vector(co[i]), 0.05)
        if loc is not None:
            dist.append(((Vector(co[i]) - loc).dot(n), co[i][2]))
    dist = np.array(dist)
    for z0 in np.arange(0.07, 0.29, 0.02):
        m = (dist[:, 1] >= z0) & (dist[:, 1] < z0 + 0.02)
        if m.any():
            print(f'[Pants]   wraps over skin z {z0:.2f}..{z0 + 0.02:.2f}: min {dist[m, 0].min() * 1000:.1f} '
                  f'median {np.median(dist[m, 0]) * 1000:.1f} max {dist[m, 0].max() * 1000:.1f} mm')
for part, _, boot_bvh in boots:
    # штанина сквозь голенище: вершина ноги дальше от центра ноги, чем последнее попадание в ботинок по тому же лучу
    poke = []
    for i, p in enumerate(co):
        if p[2] > 0.17 or abs(p[0]) < 0.02:
            continue
        g = group_of(i, p)
        c = Vector((at(center_b[g][0], p[2]), at(center_b[g][1], p[2]), p[2]))
        d = Vector((p[0] - c.x, p[1] - c.y, 0))
        r = d.length
        last, start = None, c
        while True:
            hit = boot_bvh.ray_cast(start, d.normalized(), 0.15)
            if hit[0] is None:
                break
            last = (hit[0] - c).length
            start = hit[0] + d.normalized() * 1e-5
        if last is not None and r > last - 0.0003:
            poke.append((p[2], r - last))
    print(f'[Pants]   {part}: leg verts through the shaft {len(poke)}' +
          (f' at z {min(z for z, _ in poke):.3f}..{max(z for z, _ in poke):.3f}, up to {max(d for _, d in poke) * 1000:.1f} mm'
           if poke else ''))
# кожа снаружи ткани: луч от кожи внутрь по нормали упирается в штаны раньше, чем луч наружу
bbm0 = world_bm(body)
skin_normal = np.array([tuple(v.normal) for v in bbm0.verts])
bbm0.free()
skin_out = {}
skin_poke = set()  # кожа снаружи ткани (номера вершин тела)
for i, p in enumerate(body_rest):
    if main_bone[i] not in ZONE:
        continue
    n = Vector(skin_normal[i])
    hin = fit_bvh.ray_cast(Vector(p), -n, 0.03)
    hout = fit_bvh.ray_cast(Vector(p), n, 0.08)
    if hin[0] is not None and (hout[0] is None or hin[3] < hout[3]):
        key = (main_bone[i], round(p[2] * 50) / 50)
        cnt, dd = skin_out.get(key, (0, 0.0))
        skin_out[key] = (cnt + 1, max(dd, hin[3]))
        skin_poke.add(i)
        if hin[3] > 0.001:
            print(f"[Pants]     skin out ({p[0] * 1000:.0f} {p[1] * 1000:.0f} {p[2] * 1000:.0f}) {main_bone[i]} depth {hin[3] * 1000:.1f}")
print("[Pants]   skin outside the pants: " + ", ".join(f"{b} {z:.2f}:{n_} ({d_ * 1000:.1f} mm)" for (b, z), (n_, d_) in sorted(skin_out.items())))
for part, _, chest_bvh, bottom in chests:
    out_ = [i for i, p in enumerate(co) if p[2] > 0.5 and
            (fit_normal[i][0] * p[0] + fit_normal[i][1] * (p[1] - at(center_b['T'][1], p[2]))) > 0 and
            p[2] > chest_edge_z(bottom, math.atan2(p[0], -(p[1] - at(center_b['T'][1], p[2])))) and
            chest_bvh.ray_cast(Vector(p) + Vector(fit_normal[i]) * 0.0005, Vector(fit_normal[i]), 0.08)[0] is None]
    print(f'[Pants]   {part}: waist verts above its edge not under it {len(out_)}')

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
if is_hips_only.any():
    # sun: узел и ленточки целиком на тазу - висят от пояса и не повторяют движения бёдер
    for g in pants.vertex_groups:
        g.remove([int(i) for i in np.flatnonzero(is_hips_only)])
    pants.vertex_groups['Hips'].add([int(i) for i in np.flatnonzero(is_hips_only)], 1.0, 'REPLACE')
    print(f'[Pants] knot and ribbons on Hips only: {int(is_hips_only.sum())} verts')
bad = [v.index for v in me.vertices if not v.groups]
if bad or max(len(v.groups) for v in me.vertices) > SKIN_BONES:
    raise RuntimeError(f'{name}: bad weights ({len(bad)} vertices without bones)')
used = {pants.vertex_groups[g.group].name for v in me.vertices for g in v.groups if g.weight > 0}
print(f'[Pants] bones: {sorted(used)}')
pants.parent = arm_obj
pants.modifiers.new('Armature', 'ARMATURE').object = arm_obj

# ----------------------------------------------------------------------------------------------
# Зона тела под штанами
# ----------------------------------------------------------------------------------------------
body.data.calc_loop_triangles()
bbm = world_bm(body)
body_normal = np.array([tuple(v.normal) for v in bbm.verts])
bbm.free()
if cfg['mask'] == 'below_waist':
    # rope: кожа таза и ног ниже края пояса
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
    chest_hidden = mask_of(cfg['chests'][0])
    # треугольники на краю пояса: часть вершин под штанами, остальные под нагрудником - прячет штаны
    edge_extra = set()
    for lt in body.data.loop_triangles:
        vs = set(lt.vertices)
        if vs & under and vs <= (under | chest_hidden):
            edge_extra |= vs - under
    hidden = sorted(under | edge_extra)
    hid = set(hidden)
    # голая кожа: треугольники ниже края нагрудника, которые не прячет ни одна часть (выше края кожа под нагрудником,
    # там она была и до штанов)
    boots_hidden = mask_of(cfg['boots'][0])
    bottom0 = chests[0][3]
    bare = 0
    for lt in body.data.loop_triangles:
        vs = set(lt.vertices)
        low = all(body_rest[i][2] < chest_edge_z(bottom0, math.atan2(body_rest[i][0], -body_rest[i][1])) for i in vs)
        if low and not (vs <= hid or vs <= chest_hidden or vs <= boots_hidden) and \
                all(main_bone[i] in ZONE | {'LeftFoot', 'RightFoot', 'LeftToes', 'RightToes'} for i in vs):
            bare += 1
    print(f'[Pants] hidden body vertices {len(hidden)} (of them on the waist edge {len(edge_extra)}); '
          f'body triangles below the chest edge not hidden by any part: {bare}')
else:
    # nomad: кожа, над которой по нормали есть штаны и которую не видно снаружи ни в одну щель (край шорт, между
    # лентами обмоток): лучи во все стороны, кроме круче 30° вниз (камера не смотрит снизу вверх под шорты) и круче
    # 30° вверх: шорты сидят плотно, и сверху луч уходит только по зазору в 1-3 мм между тканью и животом до края
    # пояса - кожа в паху спереди считалась видной и в приседе выходила сквозь шорты на 8 мм.
    # Обзор закрывают только штаны и тело: штаны надеваются и без нагрудника и ботинок (Docs/Gear.md, «Маска туники
    # в расчёте на штаны»)
    dirs = sphere_dirs()

    # Кожа под обмотками и узлами не прячется: между лентами меш удалён, там видна голень тела. Треугольники тела на
    # голени крупнее лент: треугольник со всеми вершинами под лентами закрывает и просвет, и на его месте была дыра.
    def covered_by_shorts(p, n):
        hit = fit_bvh.ray_cast(Vector(p) + Vector(n) * 0.0005, Vector(n), 0.08)
        return hit[0] is not None and not is_wrap[tris[hit[2]][0]]

    covered = [i for i, p in enumerate(body_rest) if main_bone[i] in HIDE_BONES and covered_by_shorts(p, body_normal[i])]
    under = {i for i in covered if not seen_from_outside(body_rest[i], body_normal[i], dirs, (fit_bvh, body_bvh))}
    # кожа, вышедшая сквозь шорты на 1-2 мм (впадина ткани у кармана), прячется: под ней сами шорты. Кроме края
    # пояса: кожа над ним остаётся
    poke_hidden = {i for i in skin_poke if main_bone[i] in HIDE_BONES and body_rest[i][2] < 0.6}
    under |= poke_hidden
    print(f"[Pants] skin through the pants hidden: {len(poke_hidden)}")
    kept = {}
    for i in set(covered) - under:
        key = (main_bone[i], round(body_rest[i][2] * 50) / 50)
        kept[key] = kept.get(key, 0) + 1
    print('[Pants] kept (seen through openings): ' + ', '.join(f'{b} {z:.2f}:{n}' for (b, z), n in sorted(kept.items())))
    # одиночная вершина среди спрятанных (луч по нормали прошёл мимо ткани) оставила бы треугольники кожи под штанами
    nbrs = {}
    for a, b in body_edges:
        nbrs.setdefault(a, set()).add(b)
        nbrs.setdefault(b, set()).add(a)
    filled = {i for i in range(len(body_rest)) if i not in under and main_bone[i] in HIDE_BONES and nbrs[i] <= under}
    under |= filled
    # Под лентами обмоток прячутся только треугольники тела, целиком закрытые лентой (луч по нормали из 10 точек
    # треугольника упирается в ленту): иначе в просвете между лентами дыра. Без этого кожа у щиколотки в позах игры
    # выходила сквозь нижнюю ленту (сзади над пяткой и сбоку у косточки).
    def under_band(p, n):
        hit = fit_bvh.ray_cast(p + n * 0.0005, n, 0.03)
        return hit[0] is not None and is_wrap[tris[hit[2]][0]]

    band_tris = covered_triangles(body, body_rest, body_normal, lambda i: main_bone[i] in HIDE_BONES, under_band)
    under, added, left = add_covered(body, under, band_tris)
    print(f'[Pants] skin under the wrap bands hidden: {added} '
          f'(triangles fully under a band {len(band_tris)}, vertices left visible {left})')
    hidden = sorted(under)
    hid = set(hidden)
    print(f'[Pants] skin covered by the pants: {len(covered)}, seen through openings (kept): {len(covered) - len(under) + len(filled)}, '
          f'filled {len(filled)}, hidden {len(hidden)}')

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
             'below_back': (0.2, 0.8, -0.35), 'persp': (-0.6, -0.7, 0.3), 'game': (0.15, 0.9, 0.42)}

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

    if gen_body:
        show(*gen_body)
        render('model', Vector((0, 0, 0.37)), 0.75, ('front', 'back', 'left', 'persp'))
    show(gen_view)
    render('gen', Vector((0, 0, 0.37)), 0.75)
    skin_mat = bpy.data.materials.new('Skin')
    skin_mat.diffuse_color = (0.85, 0.62, 0.52, 1)

    def masked_body(sets):
        """Копия тела без спрятанной кожи частей sets, как в игре."""
        o = body.copy()
        o.data = body.data.copy()
        scene.collection.objects.link(o)
        bm2 = bmesh.new()
        bm2.from_mesh(o.data)
        bm2.verts.ensure_lookup_table()
        bmesh.ops.delete(bm2, geom=[f for f in bm2.faces if any(all(v.index in s for v in f.verts) for s in sets)],
                         context='FACES_ONLY')
        bm2.to_mesh(o.data)
        bm2.free()
        o.data.materials.clear()
        o.data.materials.append(skin_mat)
        return o

    # наборы: со своим нагрудником и ботинками; nomad - ещё с базовыми и одни штаны
    sets = [('', 0)]
    if args.part != 'rope':
        sets += [('alt_', 1), ('alone_', None)]
    bodies = []
    for k, (prefix, n) in enumerate(sets):
        chest_o, boot_o = (None, None) if n is None else (chests[n][1], boots[n][1])
        sets[k] = (prefix, chest_o, boot_o)
        parts_ = [o for o in (chest_o, boot_o) if o is not None]
        b_o = masked_body([hid, chest_hidden, boots_hidden]) if args.part == 'rope' else \
            masked_body([hid] + ([] if n is None else [mask_of(chests[n][0]), mask_of(boots[n][0])]))
        bodies.append(b_o)
        show(b_o, pants, *parts_)
        render(prefix + 'tpose', Vector((0, 0, 0.42)), 0.9, ('front', 'back', 'left', 'persp_back', 'below_back'))
        render(prefix + 'waist', Vector((0, 0, 0.6)), 0.3, ('front', 'back', 'left', 'below_back'))
        render(prefix + 'ankle', Vector((0.065, 0.03, 0.14)), 0.16, ('front', 'back', 'left'))
        if args.part != 'rope':
            render(prefix + 'knee', Vector((0.065, 0.02, 0.29)), 0.24, ('front', 'back', 'left', 'persp', 'game'))
    for pose in [p for p in RECORDED if p.split(':')[1].split('@')[0] in ('walk', 'run', 'jump', 'aim_strafe')][::6]:
        set_pose(arm_obj, pose)
        hips = arm_obj.pose.bones['Hips'].head
        for (prefix, chest_o, boot_o), b_o in zip(sets, bodies):
            if prefix == 'alt_':
                continue
            show(b_o, pants, *[o for o in (chest_o, boot_o) if o is not None])
            render(prefix + pose.replace(':', '_').replace('@', '_'), Vector((hips.x, hips.y, 0.4)), 0.95,
                   ('left', 'persp_back'))
    set_pose(arm_obj, 'tpose')
    for o in bodies:
        bpy.data.objects.remove(o)

# ----------------------------------------------------------------------------------------------
# Маска, текстуры, экспорт
# ----------------------------------------------------------------------------------------------
os.makedirs(os.path.dirname(out), exist_ok=True)
with open(stem + '_BodyMask.json', 'w', encoding='utf-8') as f:
    json.dump({'part': 'pants', 'bodyVertices': len(body_rest),
               'hidden': [round(float(c), 6) for i in hidden for c in body_rest[i]]}, f)
print(f'[Pants] mask {stem}_BodyMask.json')

base_px = tex_px['base']
if cfg['wraps']:
    # на текстуре модели между лентами обмоток - кожа голени: розовые полосы на лентах (gear_fit.fill_skin_on_texture)
    base_px = fill_skin_on_texture(base_px, TEXTURE_SIZE, me, is_band, WRAP_BLEED, tag='[Pants]', what='wraps texture')
save(base_px, f'{name}.png', 'sRGB')
save(tex_px['normal'], f'{name}_normal.png', 'Non-Color')
ms = np.zeros((TEXTURE_SIZE * TEXTURE_SIZE, 4), np.float32)
ms[:, 3] = 1.0 - tex_px['roughness'][:, 0]
save(ms, f'{name}_metallic_smoothness.png', 'Non-Color')
if cfg.get('quality_material'):
    # маска цвета качества - по граням, которые пометил пользователь (у sun полосы - сами грани повязок и обмоток)
    q = gear_texture.face_mask(me, quality_poly, TEXTURE_SIZE)
    save((np.repeat(q.reshape(-1, 1), 4, 1) * [1, 1, 1, 0] + [0, 0, 0, 1]).astype(np.float32), f'{name}_quality.png',
         'Non-Color')

for o in [gen_view, body] + [c[1] for c in chests] + [b[1] for b in boots] + gen_body:
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
print(f'[Pants] exported {out}, verts {len(me.vertices)}')
