"""
Рисует FBX с нескольких сторон в PNG (Workbench, ортокамера), чтобы посмотреть позу и кости.

Запуск (из корня проекта):
    "D:\\Blender 5.2\\blender.exe" --background --factory-startup --python Tools/Blender/preview_fbx.py -- <FBX> <папка для PNG> [поза]
Поза - имя набора поворотов костей из POSES ниже; без неё кости стоят как в файле.
"""
import math
import os
import sys

import bpy
from mathutils import Euler, Vector

args = sys.argv[sys.argv.index('--') + 1:]
path, out_dir = args[0], os.path.abspath(args[1])
pose_name = args[2] if len(args) > 2 else ''
os.makedirs(out_dir, exist_ok=True)

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=path)

# Повороты костей в градусах (локальные оси кости, XYZ) для проверки весов
POSES = {
    'test': {
        'LeftUpperArm': (0, 0, -60), 'RightUpperArm': (0, 0, 60),
        'LeftLowerArm': (0, 0, -60), 'RightLowerArm': (0, 0, 60),
        'LeftUpperLeg': (-50, 0, 0), 'RightUpperLeg': (30, 0, 0),
        'LeftLowerLeg': (70, 0, 0), 'Spine': (15, 0, 0), 'Head': (0, 30, 0),
    },
}
arm = next((o for o in bpy.context.scene.objects if o.type == 'ARMATURE'), None)
if pose_name:
    for bone, deg in POSES[pose_name].items():
        pb = arm.pose.bones[bone]
        pb.rotation_mode = 'XYZ'
        pb.rotation_euler = Euler([math.radians(a) for a in deg], 'XYZ')
    bpy.context.view_layer.update()

meshes = [o for o in bpy.context.scene.objects if o.type == 'MESH']
deps = bpy.context.evaluated_depsgraph_get()
pts = []
for o in meshes:
    ev = o.evaluated_get(deps)
    pts += [ev.matrix_world @ v.co for v in ev.data.vertices]
lo = Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts)))
hi = Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))
center = (lo + hi) / 2
size = max(hi - lo) * 1.15

scene = bpy.context.scene
scene.render.engine = 'BLENDER_WORKBENCH'
scene.display.shading.light = 'STUDIO'
scene.display.shading.color_type = 'TEXTURE'
scene.render.resolution_x = scene.render.resolution_y = 700
scene.render.image_settings.file_format = 'PNG'
world = bpy.data.worlds.new('PreviewWorld')
world.color = (0.20, 0.23, 0.26)
scene.world = world
if arm:
    arm.show_in_front = True
    arm.data.display_type = 'STICK'

cam_data = bpy.data.cameras.new('PreviewCam')
cam = bpy.data.objects.new('PreviewCam', cam_data)
scene.collection.objects.link(cam)
scene.camera = cam
cam_data.type = 'ORTHO'
cam_data.ortho_scale = size
views = {'y_minus': Vector((0, -5, 0)), 'y_plus': Vector((0, 5, 0)), 'x_plus': Vector((5, 0, 0)),
         'persp': Vector((-3, -4, 1.5))}
for view, offset in views.items():
    cam.location = center + offset
    cam.rotation_euler = (center - cam.location).to_track_quat('-Z', 'Y').to_euler()
    scene.render.filepath = os.path.join(out_dir, f'{pose_name or "rest"}_{view}.png')
    bpy.ops.render.render(write_still=True)
print('[preview] done')
