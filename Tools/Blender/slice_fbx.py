"""
Режет меш из FBX и печатает сечения: по высоте для тела и ног, по X для рук.
По срезам видно, где кончаются ноги, где руки, где шея, и куда смотрит лицо.

Запуск (из корня проекта):
    "D:\\Blender 5.2\\blender.exe" --background --factory-startup --python Tools/Blender/slice_fbx.py -- <путь к FBX>
"""
import sys

import bpy

path = sys.argv[sys.argv.index('--') + 1]
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=path)
obj = next(o for o in bpy.context.scene.objects if o.type == 'MESH')
pts = [obj.matrix_world @ v.co for v in obj.data.vertices]
top = max(p.z for p in pts)


def rng(layer, axis):
    vals = [getattr(p, axis) for p in layer]
    return f'{axis}[{min(vals):+.3f},{max(vals):+.3f}] c={sum(vals) / len(vals):+.3f}'


# Ноги: каждая сторона отдельно, по высоте
for side, test in (('x<0', lambda p: p.x < 0.005), ('x>0', lambda p: p.x > 0.005)):
    z = 0.0
    while z < 0.80:
        layer = [p for p in pts if z <= p.z < z + 0.04 and test(p) and abs(p.x) < 0.2]
        if layer:
            print(f'[leg {side}] z={z:.2f} n={len(layer):4d} {rng(layer, "x")} {rng(layer, "y")}')
        z += 0.04

# Руки: по X, только вершины выше пояса
for sign in (-1, 1):
    x = 0.10
    while x < 0.55:
        layer = [p for p in pts if x <= p.x * sign < x + 0.03 and p.z > 0.72]
        if layer:
            print(f'[arm {"+" if sign > 0 else "-"}] |x|={x:.2f} n={len(layer):4d} {rng(layer, "z")} {rng(layer, "y")}')
        x += 0.03

# Тело и голова: по высоте, только середина
z = 0.56
while z < top:
    layer = [p for p in pts if z <= p.z < z + 0.03 and abs(p.x) < 0.22]
    if layer:
        print(f'[body] z={z:.2f} n={len(layer):4d} {rng(layer, "x")} {rng(layer, "y")}')
    z += 0.03
