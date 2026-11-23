"""
Замер надетой части в позах: сколько вершин части уходит внутрь тела и где видна дыра от спрятанной зоны тела.
Читает результат fit_gear.py: FBX части и <часть>_BodyMask.json. Часть ставится на скелет тела.

Запуск (из корня проекта):
    "E:\\Software\\Blender\\blender.exe" --background --factory-startup --python Tools/Blender/measure_gear.py -- ^
        --gear Assets/Models/Gear/ClothChest.fbx [--gear ...] [--png "<папка для снимков>"] [--faces] [--poses idle,aim]

Для каждой позы из gear_poses.py (заданные вручную и записанные в игре) печатает:
  * skin - вершины части глубже 1 мм внутри торса (ближайшая точка кожи в пределах 3 см, вершина за ней
    по нормали), не считая спрятанной кожи: это проступание кожи сквозь часть;
  * limbs - то же для рук и ног: рука или бедро зашли в часть;
  * exposed - спрятанные вершины тела, от которых луч наружу по нормали не упирается в часть: там видна дыра.
  * faces (с --faces) - грани части, сквозь которые проходит видимая кожа: точка внутри грани глубже 1 мм в теле.
    Вершины при этом могут быть снаружи: плоская грань между ними провисает к телу (щели на нижней ленте обмоток
    NomadPants). Медленно, поэтому по ключу; --poses idle,walk - только позы, в имени которых есть эти слова.
С --png рисует тело без спрятанной зоны и части спереди, сбоку и сзади в каждой позе.
"""
import argparse
import json
import os
import sys

import bmesh
import bpy
from mathutils import Vector
from mathutils.bvhtree import BVHTree
from mathutils.kdtree import KDTree

BODY_PATH = 'Assets/Models/20261121003938_8b8745d5.fbx'

sys.dont_write_bytecode = True  # без __pycache__ в Tools/Blender
sys.path.append(os.path.dirname(os.path.abspath(__file__)))
from gear_poses import RECORDED, RECORDED_MESH, all_poses, limit_bones, recorded_error, set_pose  # noqa: E402

INSIDE = 0.001
FACE_SAMPLES = ((1 / 3, 1 / 3, 1 / 3), (0.5, 0.5, 0), (0, 0.5, 0.5), (0.5, 0, 0.5),
                (2 / 3, 1 / 6, 1 / 6), (1 / 6, 2 / 3, 1 / 6), (1 / 6, 1 / 6, 2 / 3))  # точки внутри грани для --faces
TORSO = {'Hips', 'Spine', 'Chest', 'Neck', 'Head', 'LeftShoulder', 'RightShoulder'}

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
parser = argparse.ArgumentParser()
parser.add_argument('--gear', action='append', required=True)
parser.add_argument('--png', default='')
parser.add_argument('--faces', action='store_true')
parser.add_argument('--poses', default='')
args = parser.parse_args(argv)

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=os.path.abspath(BODY_PATH))
arm_obj = bpy.data.objects['Armature']
body = bpy.data.objects['Body']
limit_bones(body)

gears, hidden_pos = [], []
for path in args.gear:
    path = os.path.abspath(path)
    before = set(bpy.data.objects)
    bpy.ops.import_scene.fbx(filepath=path)
    new = [o for o in bpy.data.objects if o not in before]
    for o in new:
        if o.type == 'MESH':
            mw = o.matrix_world.copy()
            o.parent = arm_obj
            o.matrix_world = mw
            o.modifiers[0].object = arm_obj
            limit_bones(o)
            gears.append(o)
    for o in new:
        if o.type == 'ARMATURE':
            bpy.data.objects.remove(o)
    with open(os.path.splitext(path)[0] + '_BodyMask.json', encoding='utf-8') as f:
        flat = json.load(f)['hidden']
        hidden_pos += [Vector(flat[k:k + 3]) for k in range(0, len(flat), 3)]

# Спрятанные вершины тела - по положениям из маски
rest = [body.matrix_world @ v.co for v in body.data.vertices]
kd = KDTree(len(rest))
for i, p in enumerate(rest):
    kd.insert(p, i)
kd.balance()
hidden = set()
for p in hidden_pos:
    _, i, d = kd.find(p)
    if d > 1e-5:
        raise RuntimeError(f'mask vertex {p} not found on body (nearest {d:.6f})')
    hidden.add(i)
print(f'[Measure] hidden body vertices {len(hidden)}')
# главная кость каждой вершины тела: торс или рука и нога
group_names = {g.index: g.name for g in body.vertex_groups}
main_bone = [group_names[max(v.groups, key=lambda g: g.weight).group] if v.groups else '' for v in body.data.vertices]



def world_bmesh(o):
    bm = bmesh.new()
    bm.from_object(o, bpy.context.evaluated_depsgraph_get())
    bm.transform(o.matrix_world)
    bm.normal_update()
    return bm


def measure(pose):
    body_bm = world_bmesh(body)
    body_bm.verts.ensure_lookup_table()
    body_bm.faces.ensure_lookup_table()
    body_bvh = BVHTree.FromBMesh(body_bm)
    skin = limbs = 0
    skin_depth = limb_depth = 0.0
    gear_bms = [world_bmesh(o) for o in gears]
    where = {}
    for bm in gear_bms:
        for v in bm.verts:
            loc, normal, fi, _ = body_bvh.find_nearest(v.co, 0.03)
            if loc is None:
                continue
            depth = -(v.co - loc).dot(normal)
            face = body_bm.faces[fi]
            # кожа под частью, спрятанная целиком, не видна
            if depth <= INSIDE or all(bv.index in hidden for bv in face.verts):
                continue
            bone = main_bone[face.verts[0].index]
            if bone in TORSO:
                skin += 1
                skin_depth = max(skin_depth, depth)
            else:
                limbs += 1
                limb_depth = max(limb_depth, depth)
            n, d = where.get(bone, (0, 0.0))
            where[bone] = (n + 1, max(d, depth))
    faces, face_depth = 0, 0.0
    if args.faces:
        for bm in gear_bms:
            for tri in bm.calc_loop_triangles():
                pts = [l.vert.co for l in tri]
                deepest = 0.0
                for a, b, c in FACE_SAMPLES:
                    x = pts[0] * a + pts[1] * b + pts[2] * c
                    loc, normal, fi, _ = body_bvh.find_nearest(x, 0.03)
                    if loc is None:
                        continue
                    depth = -(x - loc).dot(normal)
                    if depth > INSIDE and not all(bv.index in hidden for bv in body_bm.faces[fi].verts):
                        deepest = max(deepest, depth)
                if deepest > 0:
                    faces += 1
                    face_depth = max(face_depth, deepest)
    gear_bm = bmesh.new()
    for bm in gear_bms:
        tmp = bpy.data.meshes.new('tmp')
        bm.to_mesh(tmp)
        gear_bm.from_mesh(tmp)
        bpy.data.meshes.remove(tmp)
    gear_bvh = BVHTree.FromBMesh(gear_bm)
    exposed = sum(1 for i in hidden
                  if gear_bvh.ray_cast(body_bm.verts[i].co + body_bm.verts[i].normal * 0.0005,
                                       body_bm.verts[i].normal, 0.08)[0] is None)
    total = sum(len(bm.verts) for bm in gear_bms)
    # Поза из игры: меши здесь должны совпасть с запечёнными в игре (выборка вершин из ProbeGear.cs)
    if pose in RECORDED_MESH:
        for key, bm in [('body', body_bm)] + [(o.name, b) for o, b in zip(gears, gear_bms)]:
            if key not in RECORDED_MESH[pose]:
                continue
            kd = KDTree(len(bm.verts))
            for v in bm.verts:
                kd.insert(v.co, v.index)
            kd.balance()
            diff = max(kd.find(p)[2] for p in RECORDED_MESH[pose][key])
            if diff > worst['game_diff'].get(key, (0.0, ''))[0]:
                worst['game_diff'][key] = (diff, pose)
    for key, value in (('skin', skin), ('limbs', limbs), ('exposed', exposed), ('faces', faces)):
        worst[key] = max(worst[key], value)
    worst['skin_depth'] = max(worst['skin_depth'], skin_depth)
    worst['limb_depth'] = max(worst['limb_depth'], limb_depth)
    worst['face_depth'] = max(worst['face_depth'], face_depth)
    # позы из игры с одной рукой в части повторяются десятками: печатаются только с кожей или дырой
    if pose.startswith('play:') and not skin and not exposed and not faces:
        return
    print(f'[Measure] {pose:16s} skin {skin:3d} ({skin_depth * 1000:4.1f} mm) limbs {limbs:3d} ({limb_depth * 1000:4.1f} mm) '
          f'exposed {exposed}/{len(hidden)} of {total}' +
          (f' faces {faces} ({face_depth * 1000:.1f} mm)' if args.faces else '') + ': ' +
          ', '.join(f'{b} {n}' for b, (n, d) in sorted(where.items(), key=lambda x: -x[1][0])[:4]))


def setup_render():
    scene = bpy.context.scene
    scene.render.engine = 'BLENDER_WORKBENCH'
    scene.display.shading.light = 'STUDIO'
    scene.display.shading.color_type = 'MATERIAL'
    scene.render.resolution_x = scene.render.resolution_y = 600
    world = bpy.data.worlds.new('PreviewWorld')
    world.color = (0.20, 0.23, 0.26)
    scene.world = world
    skin = bpy.data.materials.new('Skin')
    skin.diffuse_color = (0.85, 0.6, 0.5, 1)
    body.data.materials.clear()
    body.data.materials.append(skin)
    cloth = bpy.data.materials.new('Cloth')
    cloth.diffuse_color = (0.35, 0.45, 0.3, 1)
    for o in gears:
        o.data.materials.clear()
        o.data.materials.append(cloth)
    # спрятанная зона - отдельным материалом с нулевой прозрачностью нельзя в Workbench, поэтому удаляется
    bm = bmesh.new()
    bm.from_mesh(body.data)
    bm.verts.ensure_lookup_table()
    bmesh.ops.delete(bm, geom=[f for f in bm.faces if all(v.index in hidden for v in f.verts)], context='FACES_ONLY')
    bm.to_mesh(body.data)
    cam = bpy.data.objects.new('Cam', bpy.data.cameras.new('Cam'))
    scene.collection.objects.link(cam)
    scene.camera = cam
    cam.data.type = 'ORTHO'
    cam.data.ortho_scale = 1.15
    return cam


def render(cam, pose):
    center = Vector((0, 0, 0.62))
    for view, offset in {'front': Vector((0, -3, 0)), 'side': Vector((3, 0, 0)), 'back': Vector((0, 3, 0)),
                         'persp': Vector((-2, -2.5, 1.2))}.items():
        cam.location = center + offset
        cam.rotation_euler = (center - cam.location).to_track_quat('-Z', 'Y').to_euler()
        bpy.context.scene.render.filepath = os.path.join(os.path.abspath(args.png), f"{pose.replace(':', '_').replace('@', '_')}_{view}.png")
        bpy.ops.render.render(write_still=True)


# Маска удаляет грани тела, поэтому сначала замер во всех позах, потом снимки
worst = {'skin': 0, 'limbs': 0, 'exposed': 0, 'faces': 0, 'skin_depth': 0.0, 'limb_depth': 0.0, 'face_depth': 0.0,
         'game_diff': {}}
print(f'[Measure] poses from game: {len(RECORDED)}, worst axis error '
      f'{max((recorded_error(arm_obj, p) for p in RECORDED), default=0.0) * 1000:.2f} mm')
poses = [p for p in all_poses() if not args.poses or any(k in p for k in args.poses.split(','))]
for pose in poses:
    set_pose(arm_obj, pose)
    measure(pose)
print(f'[Measure] all poses: worst skin {worst["skin"]} ({worst["skin_depth"] * 1000:.1f} mm), '
      f'limbs {worst["limbs"]} ({worst["limb_depth"] * 1000:.1f} mm), exposed {worst["exposed"]}/{len(hidden)}' +
      (f', faces {worst["faces"]} ({worst["face_depth"] * 1000:.1f} mm)' if args.faces else '') + '; '
      f'{len(RECORDED_MESH)} game poses, meshes differ from game: ' +
      ', '.join(f'{k} {d * 1000:.2f} mm ({p})' for k, (d, p) in worst['game_diff'].items()))
if args.png:
    cam = setup_render()
    for pose in all_poses(10):
        set_pose(arm_obj, pose)
        render(cam, pose)
print('[Measure] done')
