"""
Маска цвета качества части снаряжения: где на текстуре светло-серая полоса или манжета, которую игра красит
в цвет качества (шейдер Game Tactick/Gear, свойство _QualityColor).

Запуск (из корня проекта):
    "E:\\Software\\Blender\\blender.exe" --background --factory-startup --python Tools/Blender/quality_mask.py -- ^
        --part NomadChest [--png "<папка для снимков>"]

Читает готовую часть из Assets/Models/Gear: <часть>.fbx (развёртка и форма в позе покоя) и текстуру
<часть>_Textures/<часть>.png. Пишет <часть>_Textures/<часть>_quality.png (белое — красится, серое — частично).
  1. Каждый пиксель текстуры относится к грани, чья развёртка его накрывает.
  2. Светлый пиксель — светлее light и почти без цвета (разница каналов меньше chroma), переход плавный.
     Маска идёт по рисунку, а не по граням: грани крупнее полосы, по граням край выходил пилой.
  3. Пиксель в маске, только если его грань проходит правило части по месту на теле (keep) и сам он
     в нужном месте развёртки (uv): у туники шарф тоже светло-серый, а красится только полоса на краю накидки;
     у штанов светлые пятна есть на обмотках, у ботинок — на верёвочной подошве.
  4. Отдельные светлые точки (меньше 3 × 3 пикселя) убираются.
  5. Маска растягивается на несколько пикселей в пустоту между кусками развёртки: мип-уровни смешивают
     соседей, иначе край полосы темнел бы.
С --png пишет снимок текстуры, где маска залита красным.
Маску SunPants пишет place_pants.py --part sun: по граням, которые пользователь пометил отдельным материалом.
"""
import argparse
import os
import sys

import bpy
import numpy as np

FOLDER = 'Assets/Models/Gear/'


def ring(cu, cv, r0, r1):
    """Кольцо в развёртке: полоса накидки NomadChest лежит кольцом вокруг шарфа"""
    return lambda u, v: (np.hypot(u - cu, v - cv) > r0) & (np.hypot(u - cu, v - cv) < r1)


def gloves_cuff(c):
    """Манжета перчатки — грани не дальше 42 мм от её края у локтя (как CUFF_LEN со светлыми гранями
    в place_gloves.py): дальше к кисти светлые блики на замше и обмотках"""
    ax = np.abs(c[:, 0])
    return ax < ax.min() + 0.042


def anywhere(c):
    """Все грани: место полосы на теле отбирает цвет пикселя"""
    return np.ones(len(c), dtype=bool)


def cool(c):
    """Холодный серый: синий не слабее красного"""
    return c[:, :, 2] > c[:, :, 0] - 0.03


# Правила частей. light, chroma — пороги светлого пикселя (значения sRGB 0..1); keep(C) — какие грани годятся
# по центрам C (координаты Blender: x влево персонажа, -y вперёд, z вверх, рост 1,088); uv(u, v) — место
# пикселя в развёртке; px(rgb) — ещё условие на цвет пикселя
PARTS = {
    # Полоса на краю накидки — кольцо вокруг шарфа в развёртке (центр 0,530 0,458, полоса на радиусе 0,176–0,192).
    # Шарф и светлые складки подола такие же светло-серые, но в цвет качества не красятся
    'NomadChest': dict(light=0.55, chroma=0.10, keep=lambda c: c[:, 2] > 0.74, uv=ring(0.530, 0.458, 0.155, 0.215)),
    # Подвёрнутая манжета шорт (0,33–0,40); на обмотках ниже — светлые блики ткани
    'NomadPants': dict(light=0.55, chroma=0.10, keep=lambda c: c[:, 2] > 0.30),
    # Манжета на верху голенища (0,12–0,155) и чаша. Манжета в тенях складок темнее (0,49–0,64), но без цвета;
    # выгоревший холст голенища той же яркости желтее (разница каналов 0,13–0,16), ниже — светлые нити подошвы
    'NomadBoots': dict(light=0.42, chroma=0.07, keep=lambda c: c[:, 2] > 0.10),
    # Светлая манжета на предплечье (0,65 0,61 0,59): тёплая, а серо-бирюзовые обмотки рядом холодные
    # (синий не меньше красного) и темнее
    'NomadGloves': dict(light=0.45, chroma=0.12, keep=gloves_cuff, px=lambda c: c[:, :, 2] < c[:, :, 0] - 0.02),
    # Базовый набор: серые полосы холодные (синий не слабее красного), а хаки, заплатки и кожа рядом тёплые
    # Серые ремни по бокам бёдер: сзади и в тенях складок полоса тёмная (0,27–0,30) и чуть теплее, но хаки и заплатка на колене
    # теплее намного (синий слабее красного на 0,15 и больше). Выше 0,55 — пояс и верёвка, там полос нет
    'ClothPantsRope': dict(light=0.22, chroma=0.14, keep=lambda c: c[:, 2] < 0.55,
                           px=lambda c: c[:, :, 2] > c[:, :, 0] - 0.07),
    # Серые ремни и окантовка на крупных панелях развёртки (v < 0,37); вверху развёртки — мелкие куски пряжек
    # и шнуровки с серыми бликами
    'ClothChest': dict(light=0.42, chroma=0.16, keep=anywhere, px=cool, uv=lambda u, v: v < 0.37),
    # Два серых ремня и петля шнуровки; холст голенища тёплый
    'ClothBoots': dict(light=0.42, chroma=0.16, keep=anywhere, px=cool),
    # Серая манжета на предплечье: на ней пятна розоватого отсвета, поэтому без условия на холодный цвет;
    # замша перчатки темнее и с цветом
    'ClothGloves': dict(light=0.55, chroma=0.10, keep=anywhere),
}
SOFT = 0.06
GUTTER = 8

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
parser = argparse.ArgumentParser()
parser.add_argument('--part', required=True)
parser.add_argument('--png', default='')
args = parser.parse_args(argv)
rule = PARTS[args.part]

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=os.path.abspath(FOLDER + args.part + '.fbx'))
meshes = [o for o in bpy.data.objects if o.type == 'MESH']

tex_path = os.path.abspath(f'{FOLDER}{args.part}_Textures/{args.part}.png')
image = bpy.data.images.load(tex_path)
W, H = image.size
rgb = np.array(image.pixels[:], dtype=np.float32).reshape(H, W, 4)[:, :, :3]


def smooth(edge0, edge1, x):
    t = np.clip((x - edge0) / (edge1 - edge0), 0.0, 1.0)
    return t * t * (3 - 2 * t)


value = rgb.max(axis=2)
chroma = np.ptp(rgb, axis=2)
light = smooth(rule['light'] - SOFT, rule['light'] + SOFT, value) * smooth(rule['chroma'] + 0.03, rule['chroma'] - 0.03, chroma)

# Грани: треугольники развёртки и центр в мире
tris_uv, centers = [], []
for o in meshes:
    me = o.data
    me.calc_loop_triangles()
    uv = me.uv_layers.active.data
    mw = o.matrix_world
    for t in me.loop_triangles:
        tris_uv.append([tuple(uv[li].uv) for li in t.loops])
        centers.append(tuple(mw @ t.center))
tris_uv = np.array(tris_uv, dtype=np.float64)
centers = np.array(centers)
print(f'[Mask] {args.part}: {len(tris_uv)} triangles, texture {W}x{H}')

# Какая грань накрывает пиксель (по центру пикселя); -1 — пустота между кусками развёртки
owner = np.full((H, W), -1, dtype=np.int32)
for i, t in enumerate(tris_uv):
    p = t * [W, H] - 0.5
    x0, y0 = np.floor(p.min(axis=0)).astype(int)
    x1, y1 = np.ceil(p.max(axis=0)).astype(int)
    x0, y0 = max(x0, 0), max(y0, 0)
    x1, y1 = min(x1, W - 1), min(y1, H - 1)
    if x1 < x0 or y1 < y0:
        continue
    xs, ys = np.meshgrid(np.arange(x0, x1 + 1), np.arange(y0, y1 + 1))
    (ax, ay), (bx, by), (cx, cy) = p
    d = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy)
    if abs(d) < 1e-12:
        continue
    l1 = ((by - cy) * (xs - cx) + (cx - bx) * (ys - cy)) / d
    l2 = ((cy - ay) * (xs - cx) + (ax - cx) * (ys - cy)) / d
    inside = (l1 >= -1e-6) & (l2 >= -1e-6) & (1 - l1 - l2 >= -1e-6)
    owner[ys[inside], xs[inside]] = i
covered = owner >= 0

kept = rule['keep'](centers)
region = covered & kept[np.maximum(owner, 0)]
if 'px' in rule:
    region &= rule['px'](rgb)
if 'uv' in rule:
    us, vs = np.meshgrid((np.arange(W) + 0.5) / W, (np.arange(H) + 0.5) / H)
    region &= rule['uv'](us, vs)
mask = np.where(region, light, 0.0).astype(np.float32)


def shift_max(b):
    return np.maximum.reduce([b, np.roll(b, 1, 0), np.roll(b, -1, 0), np.roll(b, 1, 1), np.roll(b, -1, 1),
                              np.roll(np.roll(b, 1, 0), 1, 1), np.roll(np.roll(b, 1, 0), -1, 1),
                              np.roll(np.roll(b, -1, 0), 1, 1), np.roll(np.roll(b, -1, 0), -1, 1)])


# Отдельные точки: что не переживает сжатие на пиксель и расширение обратно
solid = mask > 0.5
opened = ~shift_max(~solid)
opened = shift_max(opened)
mask *= shift_max(opened)

# Отчёт: где на теле маска (по граням с пикселями маски)
hit = np.bincount(owner[covered & (mask > 0.5)], minlength=len(centers)) > 0
c = centers[hit]
for z0 in np.arange(0, 1.1, 0.05):
    sel = (c[:, 2] >= z0) & (c[:, 2] < z0 + 0.05)
    if sel.any():
        print(f'[Mask]   z {z0:.2f}-{z0 + 0.05:.2f}: {sel.sum():5d} triangles  |x| {np.abs(c[sel, 0]).min():.3f}-{np.abs(c[sel, 0]).max():.3f}')

# Растяжение в пустоту развёртки: пиксель без грани берёт маску соседа
free = ~covered
for _ in range(GUTTER):
    mask = np.where(free, np.maximum(mask, shift_max(mask)), mask)
print(f'[Mask] mask pixels {(mask > 0.5).sum()} ({(mask > 0.5).mean() * 100:.1f} % of texture)')

out_path = os.path.abspath(f'{FOLDER}{args.part}_Textures/{args.part}_quality.png')
out = bpy.data.images.new(args.part + '_quality', W, H, alpha=False)
out.colorspace_settings.name = 'Non-Color'
px = np.ones((H, W, 4), dtype=np.float32)
px[:, :, 0] = px[:, :, 1] = px[:, :, 2] = mask
out.pixels[:] = px.ravel()
out.filepath_raw = out_path
out.file_format = 'PNG'
out.save()
print(f'[Mask] wrote {out_path}')

if args.png:
    os.makedirs(args.png, exist_ok=True)
    view = np.ones((H, W, 4), dtype=np.float32)
    view[:, :, :3] = rgb
    m = mask[:, :, None] * 0.7
    view[:, :, :3] = view[:, :, :3] * (1 - m) + np.array([1.0, 0.0, 0.0]) * m
    shot = bpy.data.images.new(args.part + '_shot', W, H, alpha=True)
    shot.pixels[:] = view.ravel()
    shot.filepath_raw = os.path.join(os.path.abspath(args.png), args.part + '_mask.png')
    shot.file_format = 'PNG'
    shot.save()
    print(f'[Mask] shot {shot.filepath_raw}')
