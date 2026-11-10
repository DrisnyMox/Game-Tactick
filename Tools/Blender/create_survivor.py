"""
Процедурный лоуполи-персонаж «Выживший» (постапокалипсис) для Blender 5.x.

Запуск (из корня проекта):
    "D:\\Blender 5.2\\blender.exe" --background --factory-startup --python Tools/Blender/create_survivor.py -- ^
        --asset-dir "<куда положить FBX и палитру>" [--blend "<файл .blend>"] [--preview-dir "<папка для рендеров>"]

Что делает:
  * строит один меш из примитивов (грани плоские, стиль low-poly), ~3 тыс. треугольников;
  * красит через палитру-атлас: все грани смотрят в центр ячейки одной маленькой текстуры (один материал,
    один draw call на юнита), палитра пишется как PNG;
  * строит скелет с именами костей Unity Humanoid (T-поза), веса считает по расстоянию до костей
    (жёсткие для снаряжения, мягкие для тела и одежды: не больше двух костей на вершину);
  * экспортирует FBX (Y вверх, лицо в +Z в Unity, масштаб 1 единица = 1 м) и по желанию рендерит превью.
Эскиз задан лицом в -Y и левой стороной на +X; при записи в сцену flip() разворачивает его на 180°
(лицом в +Y, левая рука на -X), и в Unity персонаж смотрит в +Z.
"""
import argparse
import math
import os
import struct
import sys
import zlib

import bmesh
import bpy
from mathutils import Euler, Matrix, Vector

# ----------------------------------------------------------------------------------------------
# Аргументы
# ----------------------------------------------------------------------------------------------
argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
parser = argparse.ArgumentParser()
parser.add_argument('--asset-dir', required=True)
parser.add_argument('--blend', default='')
parser.add_argument('--preview-dir', default='')
parser.add_argument('--name', default='Survivor')
args = parser.parse_args(argv)

NAME = args.name
os.makedirs(args.asset_dir, exist_ok=True)

# Пустая сцена: без стандартных куба, камеры и света
bpy.ops.wm.read_factory_settings(use_empty=True)

# ----------------------------------------------------------------------------------------------
# Палитра (8 x 4 ячеек по 16 пикселей)
# ----------------------------------------------------------------------------------------------
PALETTE = [
    ('skin', '#b98863'), ('skin_dark', '#8f6446'), ('hair', '#2a1d16'), ('jacket', '#59613f'),
    ('jacket_dark', '#3a4029'), ('leather', '#6a4527'), ('leather_dark', '#35251b'), ('pants', '#7a6d4f'),
    ('pants_dirty', '#5a4e38'), ('pants_dark', '#4a4232'), ('boot', '#3a2a1e'), ('sole', '#151515'),
    ('rubber', '#202224'), ('rust', '#93502a'), ('rust_dark', '#60301a'), ('metal', '#7c8284'),
    ('metal_dark', '#3d4143'), ('cloth', '#b8aa88'), ('cloth_dirty', '#8e8063'), ('scarf', '#92301f'),
    ('lens', '#e0a82a'), ('mask', '#383e31'), ('filter', '#8e8e5a'), ('brass', '#ad8836'),
    ('canvas', '#686040'), ('canvas_dark', '#483f29'), ('bedroll', '#4a5d70'), ('bone', '#ddd7c4'),
    ('blood', '#5c1c15'), ('teal', '#3d6e6a'), ('black', '#101010'), ('white', '#e8e8e8'),
]
PAL_INDEX = {name: i for i, (name, _) in enumerate(PALETTE)}
PAL_COLS, PAL_ROWS, CELL = 8, 4, 16


def hex_to_rgb(h):
    h = h.lstrip('#')
    return tuple(int(h[i:i + 2], 16) for i in (0, 2, 4))


def write_png(path, width, height, rows_bottom_first):
    """Мини-писатель PNG (RGBA 8 бит), чтобы не зависеть от цветового менеджмента Blender."""
    raw = b''.join(b'\x00' + bytes(row) for row in reversed(rows_bottom_first))

    def chunk(tag, data):
        body = tag + data
        return struct.pack('>I', len(data)) + body + struct.pack('>I', zlib.crc32(body) & 0xffffffff)

    png = b'\x89PNG\r\n\x1a\n'
    png += chunk(b'IHDR', struct.pack('>IIBBBBB', width, height, 8, 6, 0, 0, 0))
    png += chunk(b'IDAT', zlib.compress(raw, 9))
    png += chunk(b'IEND', b'')
    with open(path, 'wb') as f:
        f.write(png)


def write_palette(path):
    width, height = PAL_COLS * CELL, PAL_ROWS * CELL
    rows = []
    for y in range(height):
        row = bytearray()
        for x in range(width):
            idx = (y // CELL) * PAL_COLS + (x // CELL)
            r, g, b = hex_to_rgb(PALETTE[idx][1]) if idx < len(PALETTE) else (255, 0, 255)
            row += bytes((r, g, b, 255))
        rows.append(row)
    write_png(path, width, height, rows)


def palette_uv(name):
    idx = PAL_INDEX[name]
    col, row = idx % PAL_COLS, idx // PAL_COLS
    return Vector(((col + 0.5) / PAL_COLS, (row + 0.5) / PAL_ROWS))


# ----------------------------------------------------------------------------------------------
# Скелет (имена как в Unity Humanoid). Координаты Blender: Z вверх, лицом в -Y, левая сторона = +X.
# ----------------------------------------------------------------------------------------------
BONES = []  # (name, head, tail, parent)


def add_bone(name, head, tail, parent=None):
    BONES.append((name, Vector(head), Vector(tail), parent))


add_bone('Hips', (0, 0, 0.95), (0, 0, 1.06))
add_bone('Spine', (0, 0, 1.06), (0, 0, 1.28), 'Hips')
add_bone('Chest', (0, 0, 1.28), (0, 0, 1.50), 'Spine')
add_bone('Neck', (0, 0, 1.50), (0, 0, 1.60), 'Chest')
add_bone('Head', (0, 0, 1.60), (0, 0, 1.88), 'Neck')
for _s, _side in ((1, 'Left'), (-1, 'Right')):
    add_bone(f'{_side}Shoulder', (_s * 0.06, 0, 1.47), (_s * 0.17, 0, 1.48), 'Chest')
    add_bone(f'{_side}UpperArm', (_s * 0.17, 0, 1.48), (_s * 0.45, 0, 1.48), f'{_side}Shoulder')
    add_bone(f'{_side}LowerArm', (_s * 0.45, 0, 1.48), (_s * 0.71, 0, 1.48), f'{_side}UpperArm')
    add_bone(f'{_side}Hand', (_s * 0.71, 0, 1.48), (_s * 0.84, 0, 1.48), f'{_side}LowerArm')
    add_bone(f'{_side}UpperLeg', (_s * 0.10, 0, 0.95), (_s * 0.10, -0.012, 0.52), 'Hips')
    add_bone(f'{_side}LowerLeg', (_s * 0.10, -0.012, 0.52), (_s * 0.10, 0, 0.10), f'{_side}UpperLeg')
    add_bone(f'{_side}Foot', (_s * 0.10, 0, 0.10), (_s * 0.10, -0.14, 0.04), f'{_side}LowerLeg')
    add_bone(f'{_side}Toes', (_s * 0.10, -0.14, 0.04), (_s * 0.10, -0.23, 0.04), f'{_side}Foot')

BONE_NAMES = [b[0] for b in BONES]
BONE_SEG = {b[0]: (b[1], b[2]) for b in BONES}


def side_name(s):
    return 'Left' if s > 0 else 'Right'


def flip(v):
    """Разворот на 180° вокруг вертикали при записи в сцену.

    Эскиз персонажа задан лицом в -Y (как принято в Blender), но Unity при импорте отображает оси как
    (x, z, y) и игнорирует «forward» из FBX, так что такой персонаж смотрел бы в -Z. После разворота он
    смотрит в +Y в Blender, то есть в +Z в Unity, а левая рука оказывается на -X.
    """
    return Vector((-v.x, -v.y, v.z))


# ----------------------------------------------------------------------------------------------
# Геометрия: примитивы возвращают (точки, грани) в мировых координатах
# ----------------------------------------------------------------------------------------------
def rot_mat(deg):
    return Euler([math.radians(a) for a in deg], 'XYZ').to_matrix().to_4x4()


def mrot(rot, s):
    """Повернуть эскиз для зеркальной стороны (s = -1): отражение по X меняет знак поворотов вокруг Y и Z."""
    return (rot[0], rot[1] * s, rot[2] * s)


def box(center, size, rot=(0, 0, 0), top=(1, 1), bot=(1, 1), top_shift=(0, 0)):
    sx, sy, sz = size
    m = Matrix.Translation(Vector(center)) @ rot_mat(rot)
    pts = []
    for zi, z in enumerate((-sz / 2, sz / 2)):
        sc = bot if zi == 0 else top
        sh = (0, 0) if zi == 0 else top_shift
        for x, y in ((-1, -1), (1, -1), (1, 1), (-1, 1)):
            pts.append(m @ Vector((x * sx / 2 * sc[0] + sh[0], y * sy / 2 * sc[1] + sh[1], z)))
    faces = [(0, 3, 2, 1), (4, 5, 6, 7), (0, 1, 5, 4), (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)]
    return pts, faces


def cyl(p0, p1, r0, r1, sides=6, twist=0.0):
    """Усечённый конус от p0 к p1 с круглым сечением."""
    p0, p1 = Vector(p0), Vector(p1)
    a = p1 - p0
    a.normalize()
    helper = Vector((0, 0, 1)) if abs(a.z) < 0.9 else Vector((1, 0, 0))
    u = a.cross(helper)
    u.normalize()
    w = a.cross(u)
    w.normalize()
    pts = []
    for p, r in ((p0, r0), (p1, r1)):
        for i in range(sides):
            t = twist + 2 * math.pi * i / sides
            pts.append(p + u * (math.cos(t) * r) + w * (math.sin(t) * r))
    faces = [(i, (i + 1) % sides, sides + (i + 1) % sides, sides + i) for i in range(sides)]
    faces.append(tuple(reversed(range(sides))))
    faces.append(tuple(range(sides, 2 * sides)))
    return pts, faces


def vcyl(cx, cy, z0, z1, rx0, ry0, rx1, ry1, sides=8, twist=0.0):
    """Вертикальный эллиптический цилиндр (радиусы по X и по Y задаются отдельно)."""
    pts = []
    for z, rx, ry in ((z0, rx0, ry0), (z1, rx1, ry1)):
        for i in range(sides):
            t = twist + 2 * math.pi * i / sides
            pts.append(Vector((cx + math.cos(t) * rx, cy + math.sin(t) * ry, z)))
    faces = [(i, (i + 1) % sides, sides + (i + 1) % sides, sides + i) for i in range(sides)]
    faces.append(tuple(reversed(range(sides))))
    faces.append(tuple(range(sides, 2 * sides)))
    return pts, faces


def sphere(center, radii, seg=8, rings=4, rot=(0, 0, 0)):
    rx, ry, rz = radii
    m = Matrix.Translation(Vector(center)) @ rot_mat(rot)
    pts = [m @ Vector((0, 0, -rz))]
    for r in range(1, rings):
        phi = -math.pi / 2 + math.pi * r / rings
        for i in range(seg):
            t = 2 * math.pi * i / seg
            pts.append(m @ Vector((math.cos(phi) * math.cos(t) * rx, math.cos(phi) * math.sin(t) * ry, math.sin(phi) * rz)))
    pts.append(m @ Vector((0, 0, rz)))
    faces = [(0, 1 + (i + 1) % seg, 1 + i) for i in range(seg)]
    for r in range(rings - 2):
        base = 1 + r * seg
        for i in range(seg):
            j = (i + 1) % seg
            faces.append((base + i, base + j, base + seg + j, base + seg + i))
    top = len(pts) - 1
    base = 1 + (rings - 2) * seg
    faces += [(top, base + i, base + (i + 1) % seg) for i in range(seg)]
    return pts, faces


def strap(p0, p1, width, thick, normal):
    """Плоская лента между двумя точками: ширина поперёк, толщина вдоль normal."""
    p0, p1 = Vector(p0), Vector(p1)
    a = p1 - p0
    length = a.length
    a.normalize()
    n = Vector(normal)
    n = n - a * n.dot(a)
    n.normalize()
    w = a.cross(n)
    w.normalize()
    c = (p0 + p1) / 2
    pts = []
    for sa in (-1, 1):
        for sw in (-1, 1):
            for sn in (-1, 1):
                pts.append(c + a * (sa * length / 2) + w * (sw * width / 2) + n * (sn * thick / 2))
    # индекс = sa*4 + sw*2 + sn с поправкой на (-1,1) -> (0,1)
    faces = [(0, 1, 3, 2), (4, 6, 7, 5), (0, 4, 5, 1), (2, 3, 7, 6), (0, 2, 6, 4), (1, 5, 7, 3)]
    return pts, faces


# ----------------------------------------------------------------------------------------------
# Сборка меша
# ----------------------------------------------------------------------------------------------
def dist_point_segment(p, a, b):
    ab = b - a
    t = max(0.0, min(1.0, (p - a).dot(ab) / ab.length_squared))
    return (p - (a + ab * t)).length


def soft_weights(p, candidates):
    scored = []
    for name in candidates:
        head, tail = BONE_SEG[name]
        d = dist_point_segment(p, head, tail)
        scored.append((1.0 / ((d + 0.03) ** 3), name))
    scored.sort(reverse=True)
    top = scored[:2]
    total = sum(w for w, _ in top)
    result = {n: w / total for w, n in top}
    result = {n: w for n, w in result.items() if w >= 0.04}
    total = sum(result.values())
    return {n: w / total for n, w in result.items()}


class Builder:
    def __init__(self):
        self.bm = bmesh.new()
        self.cidx = self.bm.faces.layers.int.new('cidx')
        self.dvert = self.bm.verts.layers.deform.verify()
        self.parts = 0

    def add(self, geom, color, weights):
        """color: имя из палитры или функция(центр грани) -> имя; weights: имя кости (жёстко) или список костей (мягко)."""
        pts, faces = geom
        verts = []
        for p in pts:
            v = self.bm.verts.new(flip(p))
            w = {weights: 1.0} if isinstance(weights, str) else soft_weights(p, weights)
            for bone, value in w.items():
                v[self.dvert][BONE_NAMES.index(bone)] = value
            verts.append(v)
        for f in faces:
            center = sum((pts[i] for i in f), Vector()) / len(f)
            name = color(center) if callable(color) else color
            face = self.bm.faces.new([verts[i] for i in f])
            face[self.cidx] = PAL_INDEX[name]
        self.parts += 1


B = Builder()


def pants_color(center):
    return 'pants_dirty' if center.z < 0.30 else 'pants'


# ---- Торс -------------------------------------------------------------------------------------
B.add(box((0, 0, 0.99), (0.35, 0.22, 0.17), top=(0.95, 0.95), bot=(0.88, 0.9)), 'pants',
      ['Hips', 'LeftUpperLeg', 'RightUpperLeg'])
B.add(box((0, 0, 1.16), (0.34, 0.21, 0.26), top=(1.0, 1.0), bot=(0.96, 0.96)), 'jacket', ['Hips', 'Spine', 'Chest'])
B.add(vcyl(0, 0, 0.965, 1.07, 0.186, 0.126, 0.176, 0.118, sides=10), 'jacket_dark', ['Hips', 'Spine'])
B.add(box((0, 0, 1.385), (0.40, 0.24, 0.25), top=(1.08, 0.88), bot=(0.92, 0.96)), 'jacket', ['Spine', 'Chest', 'Neck'])
B.add(vcyl(0, 0, 1.50, 1.62, 0.042, 0.042, 0.040, 0.040, sides=6), 'skin', ['Chest', 'Neck', 'Head'])
B.add(vcyl(0, 0, 1.49, 1.575, 0.092, 0.082, 0.076, 0.068, sides=8), 'scarf', ['Chest', 'Neck'])
B.add(box((0.045, 0.085, 1.42), (0.075, 0.02, 0.30), rot=(-4, 0, 6), bot=(0.7, 1)), 'scarf', 'Chest')  # хвост шарфа на спине

# Рваный подол куртки (полосы свисают с подола)
for x, y, h, rz, col in ((-0.10, -0.115, 0.15, 6, 'jacket'), (0.08, -0.12, 0.12, -8, 'jacket_dark'),
                         (0.13, 0.10, 0.16, 10, 'jacket'), (-0.06, 0.12, 0.13, -5, 'jacket_dark')):
    B.add(box((x, y, 0.93 - h / 2 + 0.07), (0.07, 0.014, h), rot=(0, 0, rz), bot=(0.6, 1)), col, 'Hips')

# ---- Голова -----------------------------------------------------------------------------------
B.add(sphere((0, 0.012, 1.735), (0.096, 0.108, 0.108), seg=8, rings=4), 'skin', 'Head')
B.add(box((0, -0.02, 1.655), (0.14, 0.15, 0.09), bot=(0.62, 0.55)), 'skin', 'Head')
B.add(box((0, -0.105, 1.705), (0.03, 0.05, 0.045)), 'skin_dark', 'Head')  # нос
for s in (1, -1):
    B.add(box((s * 0.10, 0.0, 1.70), (0.02, 0.03, 0.045)), 'skin_dark', 'Head')  # уши
    B.add(box((s * 0.04, -0.100, 1.738), (0.025, 0.012, 0.014)), 'hair', 'Head')  # глаза
# Респиратор
B.add(box((0, -0.088, 1.658), (0.135, 0.075, 0.085), bot=(0.8, 0.8)), 'mask', 'Head')
for s in (1, -1):
    B.add(cyl((s * 0.062, -0.118, 1.648), (s * 0.062, -0.160, 1.648), 0.030, 0.030, sides=8), 'filter', 'Head')
    B.add(cyl((s * 0.062, -0.160, 1.648), (s * 0.062, -0.168, 1.648), 0.033, 0.033, sides=8), 'metal_dark', 'Head')
    B.add(strap((s * 0.066, -0.075, 1.662), (s * 0.078, 0.07, 1.678), 0.016, 0.008, (s, 0, 0)), 'leather_dark', 'Head')
B.add(strap((-0.075, 0.088, 1.678), (0.075, 0.088, 1.678), 0.016, 0.010, (0, 1, 0)), 'leather_dark', 'Head')
# Очки на лбу
for s in (1, -1):
    B.add(cyl((s * 0.043, -0.090, 1.778), (s * 0.043, -0.098, 1.778), 0.041, 0.041, sides=8), 'metal_dark', 'Head')
    B.add(cyl((s * 0.043, -0.098, 1.778), (s * 0.043, -0.130, 1.778), 0.034, 0.034, sides=8), 'lens', 'Head')
# Волосы: шапка и торчащие пряди
B.add(sphere((0, 0.02, 1.795), (0.099, 0.112, 0.078), seg=8, rings=4), 'hair', 'Head')
for p0, p1 in (((0, 0.0, 1.875), (0, -0.01, 1.95)), ((0.05, 0.03, 1.86), (0.09, 0.04, 1.93)),
               ((-0.05, 0.03, 1.86), (-0.09, 0.04, 1.92)), ((0, 0.09, 1.82), (0, 0.145, 1.80)),
               ((0.06, -0.02, 1.875), (0.07, -0.07, 1.93)), ((-0.05, -0.03, 1.875), (-0.07, -0.08, 1.92))):
    B.add(cyl(p0, p1, 0.034, 0.004, sides=5), 'hair', 'Head')

# ---- Руки -------------------------------------------------------------------------------------
for s in (1, -1):
    side = side_name(s)
    arm = [f'{side}Shoulder', f'{side}UpperArm', f'{side}LowerArm', f'{side}Hand']
    B.add(cyl((s * 0.17, 0, 1.48), (s * 0.43, 0, 1.48), 0.072, 0.062, sides=6), 'jacket', arm[:3])
    B.add(cyl((s * 0.43, 0, 1.48), (s * 0.46, 0, 1.48), 0.066, 0.066, sides=6), 'jacket_dark', arm[1:3])
    B.add(cyl((s * 0.31, 0, 1.48), (s * 0.37, 0, 1.48), 0.0735, 0.0715, sides=6), 'leather', arm[:3])  # заплата на рукаве
    forearm_color = 'cloth' if s > 0 else 'skin'
    B.add(cyl((s * 0.46, 0, 1.48), (s * 0.71, 0, 1.48), 0.056, 0.044, sides=6), forearm_color, arm[1:])
    if s > 0:
        B.add(cyl((s * 0.57, 0, 1.48), (s * 0.69, 0, 1.48), 0.052, 0.047, sides=6), 'leather_dark', arm[1:])  # наруч
        B.add(cyl((s * 0.50, 0, 1.48), (s * 0.535, 0, 1.48), 0.062, 0.061, sides=6), 'cloth_dirty', arm[1:3])
    else:
        B.add(cyl((s * 0.52, 0, 1.48), (s * 0.56, 0, 1.48), 0.062, 0.059, sides=6), 'cloth_dirty', arm[1:])
        B.add(cyl((s * 0.61, 0, 1.48), (s * 0.645, 0, 1.48), 0.055, 0.052, sides=6), 'cloth', arm[1:])
        B.add(cyl((s * 0.665, 0, 1.48), (s * 0.70, 0, 1.48), 0.049, 0.047, sides=6), 'leather', arm[1:])  # браслет
    # Перчатка без пальцев
    B.add(box((s * 0.775, 0, 1.48), (0.11, 0.05, 0.08)), 'leather_dark', f'{side}Hand')
    B.add(box((s * 0.84, 0, 1.48), (0.03, 0.046, 0.07)), 'skin', f'{side}Hand')
    B.add(box((s * 0.745, -0.04, 1.48), (0.045, 0.03, 0.035), rot=mrot((0, 0, -20), s)), 'leather_dark', f'{side}Hand')
    if s > 0:
        B.add(box((s * 0.78, 0, 1.524), (0.05, 0.056, 0.012)), 'metal', f'{side}Hand')  # пластина на костяшках

# Наплечники: слева — ржавая пластина на резине с шипом, справа — кожаная накладка с ремнём
B.add(box((0.255, 0, 1.535), (0.22, 0.21, 0.025), rot=(0, 20, 0)), 'rubber', 'LeftShoulder')
B.add(box((0.255, 0, 1.562), (0.19, 0.18, 0.03), rot=(0, 20, 0), top=(0.9, 0.9)), 'rust', 'LeftShoulder')
B.add(box((0.255, 0, 1.581), (0.15, 0.14, 0.012), rot=(0, 20, 0)), 'rust_dark', 'LeftShoulder')
B.add(cyl((0.275, 0, 1.585), (0.29, 0, 1.70), 0.028, 0.004, sides=5), 'metal', 'LeftShoulder')
for y in (-0.055, 0.055):
    B.add(cyl((0.205, y, 1.585), (0.205, y, 1.60), 0.011, 0.011, sides=5), 'metal_dark', 'LeftShoulder')
B.add(box((-0.235, 0, 1.54), (0.12, 0.14, 0.03), rot=(0, -15, 0)), 'leather_dark', 'RightShoulder')
B.add(strap((-0.20, -0.07, 1.558), (-0.30, -0.07, 1.505), 0.03, 0.012, (0, 0, 1)), 'leather', 'RightShoulder')

# ---- Ноги -------------------------------------------------------------------------------------
for s in (1, -1):
    side = side_name(s)
    ul, ll, ft, ts = f'{side}UpperLeg', f'{side}LowerLeg', f'{side}Foot', f'{side}Toes'
    B.add(cyl((s * 0.10, 0, 0.96), (s * 0.10, -0.012, 0.52), 0.090, 0.068, sides=6), 'pants', ['Hips', ul, ll])
    B.add(cyl((s * 0.10, -0.012, 0.52), (s * 0.10, 0, 0.12), 0.065, 0.052, sides=6), pants_color, [ul, ll, ft])
    B.add(box((s * 0.10, -0.077, 0.545), (0.10, 0.045, 0.115), top=(0.9, 0.8), bot=(1, 0.8)), 'rubber', [ul, ll])  # наколенник
    B.add(vcyl(s * 0.10, -0.006, 0.445, 0.462, 0.066, 0.066, 0.066, 0.066, sides=6), 'leather_dark', [ul, ll])
    # Ботинок
    B.add(vcyl(s * 0.10, 0, 0.04, 0.22, 0.062, 0.066, 0.056, 0.060, sides=8), 'boot', [ll, ft])
    B.add(vcyl(s * 0.10, 0, 0.205, 0.235, 0.064, 0.068, 0.064, 0.068, sides=8), 'leather_dark', [ll, ft])
    B.add(box((s * 0.10, -0.07, 0.0625), (0.115, 0.25, 0.085), top=(0.9, 0.85)), 'boot', ft)
    B.add(box((s * 0.10, -0.188, 0.055), (0.105, 0.085, 0.07), top=(0.85, 0.8)), 'leather_dark', ts)
    B.add(box((s * 0.10, -0.075, 0.012), (0.125, 0.30, 0.024)), 'sole', ft)
    B.add(box((s * 0.10, 0.045, 0.045), (0.11, 0.07, 0.035)), 'sole', ft)  # каблук
    for y in (-0.095, -0.125):
        B.add(box((s * 0.10, y, 0.1065), (0.10, 0.018, 0.012)), 'cloth', ft)  # шнуровка
    if s < 0:
        B.add(box((-0.172, 0.0, 0.80), (0.045, 0.115, 0.14)), 'pants_dark', [ul])  # накладной карман
        B.add(box((-0.178, 0.0, 0.862), (0.02, 0.12, 0.04)), 'pants_dirty', [ul])
    else:
        B.add(vcyl(0.10, -0.006, 0.70, 0.73, 0.083, 0.083, 0.083, 0.083, sides=6), 'leather', [ul, ll])  # жгут
        B.add(box((0.168, 0, 0.80), (0.03, 0.10, 0.12), rot=(0, 0, 0)), 'canvas', [ul])  # заплата

# ---- Ремень, подсумки, обрывки ткани ----------------------------------------------------------
B.add(vcyl(0, 0, 1.015, 1.055, 0.20, 0.132, 0.20, 0.132, sides=10), 'leather_dark', 'Hips')
B.add(box((0, -0.136, 1.035), (0.055, 0.016, 0.05)), 'brass', 'Hips')
B.add(box((-0.19, -0.02, 0.985), (0.06, 0.09, 0.11)), 'leather', 'Hips')
B.add(box((-0.19, -0.02, 1.04), (0.065, 0.095, 0.02)), 'leather_dark', 'Hips')
B.add(box((0.205, -0.03, 0.935), (0.025, 0.04, 0.20), bot=(0.7, 0.7)), 'leather_dark', 'Hips')  # ножны
B.add(box((0.205, -0.03, 1.065), (0.022, 0.03, 0.075)), 'leather', 'Hips')
B.add(box((0.205, -0.03, 1.03), (0.04, 0.05, 0.012)), 'metal', 'Hips')
B.add(cyl((0.19, 0.07, 0.96), (0.19, 0.125, 0.96), 0.055, 0.055, sides=8), 'canvas', 'Hips')  # фляга
B.add(cyl((0.19, 0.125, 0.96), (0.19, 0.14, 0.96), 0.02, 0.02, sides=6), 'metal_dark', 'Hips')
for x, y, w, h, rx, rz, col in ((-0.06, -0.136, 0.045, 0.15, 8, 6, 'scarf'), (0.06, -0.136, 0.04, 0.12, 6, -8, 'cloth_dirty'),
                                (0.09, 0.128, 0.05, 0.17, -8, 5, 'scarf')):
    B.add(box((x, y, 1.0 - h / 2), (w, 0.012, h), rot=(rx, 0, rz), bot=(0.6, 1)), col, 'Hips')

# ---- Детали куртки: молния, карманы, нашивка --------------------------------------------------
B.add(strap((0.0, -0.1185, 1.50), (0.0, -0.1185, 1.17), 0.014, 0.008, (0, -1, 0)), 'metal_dark', 'Chest')
for s in (1, -1):
    B.add(box((s * 0.095, -0.1225, 1.38), (0.095, 0.014, 0.075)), 'jacket_dark', 'Chest')
    B.add(box((s * 0.095, -0.131, 1.405), (0.10, 0.012, 0.026)), 'leather', 'Chest')
B.add(box((0.105, -0.124, 1.455), (0.055, 0.008, 0.045)), 'white', 'Chest')  # нашивка
B.add(box((0.105, -0.1295, 1.455), (0.03, 0.006, 0.01)), 'blood', 'Chest')
B.add(box((0.105, -0.1295, 1.455), (0.01, 0.006, 0.03)), 'blood', 'Chest')

# ---- Патронташ и рюкзак -----------------------------------------------------------------------
fs, fe = Vector((-0.165, -0.121, 1.50)), Vector((0.12, -0.121, 1.19))
B.add(strap(fs, fe, 0.05, 0.014, (0, -1, 0)), 'leather_dark', 'Chest')
B.add(strap((-0.165, 0.118, 1.50), (0.12, 0.118, 1.19), 0.05, 0.014, (0, 1, 0)), 'leather_dark', 'Chest')
B.add(strap((-0.15, -0.115, 1.516), (-0.15, 0.115, 1.516), 0.05, 0.014, (0, 0, 1)), 'leather_dark', 'Chest')
angle = math.degrees(math.atan2(fs.z - fe.z, fe.x - fs.x))
for t in (0.25, 0.5, 0.75):
    p = fs.lerp(fe, t)
    B.add(box((p.x, p.y - 0.022, p.z), (0.046, 0.04, 0.05), rot=(0, angle, 0)), 'leather', 'Chest')
    B.add(box((p.x, p.y - 0.044, p.z), (0.012, 0.006, 0.012), rot=(0, angle, 0)), 'brass', 'Chest')
B.add(sphere((-0.0225, -0.14, 1.30), (0.024, 0.022, 0.026), seg=6, rings=3), 'bone', 'Chest')  # талисман-череп
for s in (1, -1):
    B.add(strap((s * 0.12, 0.115, 1.526), (s * 0.12, -0.10, 1.526), 0.052, 0.012, (0, 0, 1)), 'canvas_dark', 'Chest')
    B.add(strap((s * 0.12, -0.114, 1.512), (s * 0.105, -0.114, 1.30), 0.05, 0.010, (0, -1, 0)), 'canvas_dark', 'Chest')
    B.add(box((s * 0.175, 0.20, 1.28), (0.06, 0.13, 0.18)), 'canvas_dark', 'Chest')  # боковые карманы
B.add(box((0, 0.20, 1.36), (0.30, 0.17, 0.36), top=(0.92, 0.9)), 'canvas', 'Chest')
B.add(box((0, 0.118, 1.37), (0.26, 0.02, 0.34)), 'canvas_dark', 'Chest')
B.add(box((0, 0.205, 1.55), (0.30, 0.185, 0.06), rot=(6, 0, 0)), 'canvas_dark', 'Chest')
B.add(box((0, 0.29, 1.40), (0.15, 0.03, 0.12)), 'leather', 'Chest')  # внешний карман
B.add(cyl((-0.17, 0.215, 1.13), (0.17, 0.215, 1.13), 0.075, 0.075, sides=8), 'bedroll', 'Chest')  # скатка под рюкзаком
for x in (-0.09, 0.09):
    B.add(cyl((x - 0.012, 0.215, 1.13), (x + 0.012, 0.215, 1.13), 0.080, 0.080, sides=8), 'leather_dark', 'Chest')
B.add(cyl((-0.17, 0.215, 1.13), (-0.178, 0.215, 1.13), 0.05, 0.05, sides=8), 'cloth', 'Chest')  # торцы скатки
B.add(cyl((0.17, 0.215, 1.13), (0.178, 0.215, 1.13), 0.05, 0.05, sides=8), 'cloth_dirty', 'Chest')
B.add(cyl((0.13, 0.29, 1.17), (0.13, 0.29, 1.235), 0.035, 0.032, sides=8), 'metal', 'Chest')  # кружка
B.add(box((0.168, 0.29, 1.205), (0.025, 0.012, 0.04)), 'metal_dark', 'Chest')

# ----------------------------------------------------------------------------------------------
# Меш, UV, нормали
# ----------------------------------------------------------------------------------------------
bm = B.bm

# Голова крупнее: для читаемости на расстоянии (стилизованные пропорции). Масштабируем только детали,
# целиком привязанные к кости Head, относительно основания шеи.
HEAD_SCALE = 1.15
head_pivot = Vector((0, 0, 1.60))
head_index = BONE_NAMES.index('Head')
for v in bm.verts:
    dv = v[B.dvert]
    if head_index in dv and dv[head_index] > 0.999:
        v.co = head_pivot + (v.co - head_pivot) * HEAD_SCALE

bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
uv_layer = bm.loops.layers.uv.verify()
for face in bm.faces:
    uv = palette_uv(PALETTE[face[B.cidx]][0])
    for loop in face.loops:
        loop[uv_layer].uv = uv
    face.smooth = False
bmesh.ops.triangulate(bm, faces=bm.faces[:], quad_method='BEAUTY', ngon_method='BEAUTY')

mesh = bpy.data.meshes.new(f'{NAME}_Body')
bm.to_mesh(mesh)
bm.free()
tris = len(mesh.polygons)
print(f'[Survivor] parts={B.parts} verts={len(mesh.vertices)} tris={tris}')

# ----------------------------------------------------------------------------------------------
# Материал, палитра, объекты, скелет
# ----------------------------------------------------------------------------------------------
palette_path = os.path.join(args.asset_dir, f'{NAME}_Palette.png')
write_palette(palette_path)

collection = bpy.context.scene.collection

mat = bpy.data.materials.new(f'{NAME}_Palette')
mat.use_nodes = True
nodes = mat.node_tree.nodes
links = mat.node_tree.links
bsdf = nodes.get('Principled BSDF')
tex = nodes.new('ShaderNodeTexImage')
tex.image = bpy.data.images.load(palette_path)
tex.interpolation = 'Closest'
links.new(tex.outputs['Color'], bsdf.inputs['Base Color'])
bsdf.inputs['Roughness'].default_value = 0.9
mesh.materials.append(mat)

arm_data = bpy.data.armatures.new('Armature')
arm_obj = bpy.data.objects.new('Armature', arm_data)
collection.objects.link(arm_obj)
body = bpy.data.objects.new(f'{NAME}_Body', mesh)
collection.objects.link(body)
for bone_name in BONE_NAMES:
    body.vertex_groups.new(name=bone_name)

# validate() стирает веса, указывающие на несуществующие группы, поэтому вызываем его только после
# создания групп. Проверяем, что веса дошли до меша: иначе Unity привяжет всё к корневой кости.
mesh.validate()
weighted = sum(1 for v in mesh.vertices if len(v.groups) > 0)
if weighted != len(mesh.vertices):
    raise RuntimeError(f'skin weights lost: {weighted} of {len(mesh.vertices)} vertices have weights')
print(f'[Survivor] weighted vertices: {weighted}/{len(mesh.vertices)}')

bpy.context.view_layer.objects.active = arm_obj
arm_obj.select_set(True)
bpy.ops.object.mode_set(mode='EDIT')
edit_bones = {}
for name, head, tail, parent in BONES:
    eb = arm_data.edit_bones.new(name)
    eb.head, eb.tail = flip(head), flip(tail)
    edit_bones[name] = eb
for name, head, tail, parent in BONES:
    if parent:
        edit_bones[name].parent = edit_bones[parent]
bpy.ops.object.mode_set(mode='OBJECT')

body.parent = arm_obj
mod = body.modifiers.new('Armature', 'ARMATURE')
mod.object = arm_obj

# ----------------------------------------------------------------------------------------------
# Превью (Workbench, без GPU-зависимостей)
# ----------------------------------------------------------------------------------------------
if args.preview_dir:
    os.makedirs(args.preview_dir, exist_ok=True)
    scene = bpy.context.scene
    scene.render.engine = 'BLENDER_WORKBENCH'
    scene.display.shading.light = 'STUDIO'
    scene.display.shading.color_type = 'TEXTURE'
    scene.display.shading.show_object_outline = True
    scene.render.resolution_x, scene.render.resolution_y = 520, 780
    scene.render.image_settings.file_format = 'PNG'
    world = bpy.data.worlds.new('PreviewWorld')
    world.color = (0.20, 0.23, 0.26)
    scene.world = world
    cam_data = bpy.data.cameras.new('PreviewCam')
    cam = bpy.data.objects.new('PreviewCam', cam_data)
    collection.objects.link(cam)
    scene.camera = cam
    target = Vector((0, 0, 0.95))
    views = {
        'front': ((0, 6, 0.95), True, 2.15), 'back': ((0, -6, 0.95), True, 2.15),
        'left': ((-6, 0, 0.95), True, 2.15), 'three_quarter': ((-2.5, 3.2, 1.35), False, 2.15),
        'head': ((-0.9, 1.4, 1.75), False, 2.15),
    }
    for view, (loc, ortho, scale) in views.items():
        cam.location = Vector(loc)
        cam.rotation_euler = (target - cam.location if view != 'head' else Vector((0, 0, 1.72)) - cam.location).to_track_quat('-Z', 'Y').to_euler()
        cam_data.type = 'ORTHO' if ortho else 'PERSP'
        cam_data.ortho_scale = scale
        cam_data.lens = 85 if view == 'head' else 50
        scene.render.filepath = os.path.join(args.preview_dir, f'{view}.png')
        bpy.ops.render.render(write_still=True)
    bpy.data.objects.remove(cam)

# ----------------------------------------------------------------------------------------------
# Экспорт
# ----------------------------------------------------------------------------------------------
bpy.ops.object.select_all(action='DESELECT')
arm_obj.select_set(True)
body.select_set(True)
bpy.context.view_layer.objects.active = arm_obj

fbx_path = os.path.join(args.asset_dir, f'{NAME}.fbx')
bpy.ops.export_scene.fbx(
    filepath=fbx_path,
    use_selection=True,
    object_types={'ARMATURE', 'MESH'},
    use_mesh_modifiers=False,
    mesh_smooth_type='FACE',
    use_triangles=True,
    add_leaf_bones=False,
    use_armature_deform_only=True,
    bake_anim=False,
    apply_unit_scale=True,
    apply_scale_options='FBX_SCALE_ALL',
    bake_space_transform=True,  # убрать поворот осей из узлов: иначе дочерние объекты получают 90° по X
    axis_forward='-Z',  # Unity эту ось игнорирует; лицом в +Z персонажа ставит flip() выше
    axis_up='Y',
    path_mode='STRIP',
)
print(f'[Survivor] exported {fbx_path}')

if args.blend:
    os.makedirs(os.path.dirname(args.blend), exist_ok=True)
    bpy.context.preferences.filepaths.save_version = 0
    bpy.ops.wm.save_as_mainfile(filepath=args.blend, compress=True)
    print(f'[Survivor] saved {args.blend}')
