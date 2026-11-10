"""
Печатает, что лежит в FBX: объекты, меши, вершины, габариты, кости, материалы.

Запуск (из корня проекта):
    "D:\\Blender 5.2\\blender.exe" --background --factory-startup --python Tools/Blender/inspect_fbx.py -- <путь к FBX>
"""
import sys

import bpy
from mathutils import Vector

path = sys.argv[sys.argv.index('--') + 1]
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=path)

for obj in bpy.context.scene.objects:
    print(f'[inspect] {obj.type} "{obj.name}" parent={obj.parent.name if obj.parent else None} '
          f'loc={tuple(round(c, 3) for c in obj.location)} rot={tuple(round(c, 3) for c in obj.rotation_euler)} '
          f'scale={tuple(round(c, 4) for c in obj.scale)}')
    if obj.type == 'MESH':
        me = obj.data
        corners = [obj.matrix_world @ Vector(c) for c in obj.bound_box]
        lo = Vector((min(c.x for c in corners), min(c.y for c in corners), min(c.z for c in corners)))
        hi = Vector((max(c.x for c in corners), max(c.y for c in corners), max(c.z for c in corners)))
        print(f'[inspect]   verts={len(me.vertices)} polys={len(me.polygons)} uv={len(me.uv_layers)} '
              f'groups={len(obj.vertex_groups)} mats={[m.name if m else None for m in me.materials]}')
        print(f'[inspect]   world min={tuple(round(c, 3) for c in lo)} max={tuple(round(c, 3) for c in hi)}')
    if obj.type == 'ARMATURE':
        for b in obj.data.bones:
            print(f'[inspect]   bone {b.name} parent={b.parent.name if b.parent else None} '
                  f'head={tuple(round(c, 3) for c in b.head_local)} tail={tuple(round(c, 3) for c in b.tail_local)}')

for mat in bpy.data.materials:
    if mat.node_tree:
        for link in mat.node_tree.links:
            src = link.from_node
            name = src.image.name if src.type == 'TEX_IMAGE' and src.image else src.type
            print(f'[inspect] material "{mat.name}": {name}.{link.from_socket.name} -> '
                  f'{link.to_node.type}.{link.to_socket.name}')

for img in bpy.data.images:
    print(f'[inspect] image "{img.name}" size={tuple(img.size)} packed={img.packed_file is not None}')
