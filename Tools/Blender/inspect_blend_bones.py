"""
Печатает кости из открытого файла .blend: родитель, связь с родителем, начало и конец.

Запуск (из корня проекта):
    "D:\\Blender 5.2\\blender.exe" --background <файл .blend> --python Tools/Blender/inspect_blend_bones.py
"""
import bpy

for obj in bpy.data.objects:
    if obj.type != 'ARMATURE':
        continue
    for b in obj.data.bones:
        print(f'[bones] {b.name} parent={b.parent.name if b.parent else None} connect={b.use_connect} '
              f'deform={b.use_deform} head={tuple(round(c, 3) for c in b.head_local)} '
              f'tail={tuple(round(c, 3) for c in b.tail_local)}')
