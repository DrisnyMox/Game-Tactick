"""
Режет кисти плоскостями поперёк руки (x = const) и печатает каждый замкнутый контур среза.
Пальцы, разделённые в меше, дают на срезе отдельные контуры: по ним видно, где кончается ладонь и где лежат пальцы.
Координаты как в исходном FBX: лицом в -Y, левая сторона персонажа на +X.

Запуск (из корня проекта):
    "D:\\Blender 5.2\\blender.exe" --background --factory-startup --python Tools/Blender/slice_hands.py -- <путь к FBX>
"""
import sys

import bmesh
import bpy
from mathutils import Vector

path = sys.argv[sys.argv.index('--') + 1]
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=path)
obj = next(o for o in bpy.context.scene.objects if o.type == 'MESH')
source = bmesh.new()
source.from_mesh(obj.data)
source.transform(obj.matrix_world)

for sign in (1, -1):
    x = 0.40
    while x < 0.54:
        bm = source.copy()
        res = bmesh.ops.bisect_plane(bm, geom=bm.verts[:] + bm.edges[:] + bm.faces[:],
                                     plane_co=Vector((sign * x, 0, 0)), plane_no=Vector((1, 0, 0)))
        cut_edges = [e for e in res['geom_cut'] if isinstance(e, bmesh.types.BMEdge)]
        # Контуры: связные куски из рёбер среза, только выше пояса
        seen, loops = set(), []
        for start in cut_edges:
            if start in seen:
                continue
            stack, verts = [start], set()
            seen.add(start)
            cut_set = set(cut_edges)
            while stack:
                e = stack.pop()
                verts.update(e.verts)
                for v in e.verts:
                    for n in v.link_edges:
                        if n in cut_set and n not in seen:
                            seen.add(n)
                            stack.append(n)
            pts = [v.co.copy() for v in verts]
            if min(p.z for p in pts) > 0.78:
                loops.append(pts)
        loops.sort(key=lambda pts: min(p.y for p in pts))
        desc = '  '.join(f'y[{min(p.y for p in l):+.3f},{max(p.y for p in l):+.3f}] '
                         f'z[{min(p.z for p in l):+.3f},{max(p.z for p in l):+.3f}]' for l in loops)
        print(f'[hand {"+" if sign > 0 else "-"}] |x|={x:.3f} loops={len(loops)} {desc}')
        bm.free()
        x += 0.006
