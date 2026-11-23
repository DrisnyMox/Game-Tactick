"""
Общая посадка части, вырезанной из модели генератора, на тело игрока (Docs/Gear.md, «Вырезание из тела»).
Скрипты частей (place_pants.py) ставят часть на место по своим правилам, а дальше зовут эти шаги.
Порядок работы с новой частью - скилл .claude/skills/gear-fit.

Правило посадки: каждую вершину двигать можно, но соседние вершины одной детали (ткань, ремень, складка, край)
сдвигаются почти одинаково. Иначе ремни и шнуровка мнутся (так было у fit_gear.py на ClothChest).
Координаты - мировые Blender: лицом в -Y, Z вверх, рост тела 1,088.
"""
import math

import bmesh
import bpy
import numpy as np
from mathutils import Vector
from mathutils.bvhtree import BVHTree
from mathutils.kdtree import KDTree

TAUBIN = (0.5, -0.53)  # сглаживание без усадки: шаг к соседям и шаг обратно


def world_bm(o):
    """Сетка объекта после модификаторов, в мировых координатах, с пересчитанными нормалями: после bm.transform
    у граней остаются нормали до поворота, и нормаль ближайшей точки кожи была бы неверной."""
    bm = bmesh.new()
    bm.from_object(o, bpy.context.evaluated_depsgraph_get())
    bm.transform(o.matrix_world)
    bm.normal_update()
    return bm


def bvh_of(o):
    bm = world_bm(o)
    tree = BVHTree.FromBMesh(bm)
    bm.free()
    return tree


def smoothstep(e0, e1, x):
    t = np.clip((np.asarray(x, float) - e0) / (e1 - e0), 0.0, 1.0)
    return t * t * (3 - 2 * t)


def split_long_edges(o, max_len, rounds=3):
    """Делит рёбра длиннее max_len пополам (развёртка делится вместе с ними), пока не станут короче. Крупные грани
    не могут прогнуться по телу: сзади у шорт ряды вершин шли через 3 см. Возвращает число вершин."""
    for _ in range(rounds):
        bm = bmesh.new()
        bm.from_mesh(o.data)
        long_edges = [e for e in bm.edges if e.calc_length() > max_len]
        if long_edges:
            bmesh.ops.subdivide_edges(bm, edges=long_edges, cuts=1, use_grid_fill=True)
            bmesh.ops.triangulate(bm, faces=[fc for fc in bm.faces if len(fc.verts) > 4])
        bm.to_mesh(o.data)
        bm.free()
        if not long_edges:
            break
    return len(o.data.vertices)


def neighbours(edges, idx):
    """Соседи по рёбрам среди вершин idx, в номерах внутри idx."""
    pos = {v: k for k, v in enumerate(idx)}
    nb = [[] for _ in idx]
    for a, b in edges:
        if a in pos and b in pos:
            nb[pos[a]].append(pos[b])
            nb[pos[b]].append(pos[a])
    return nb


def average(X, nb):
    return np.array([X[n_].mean(0) if n_ else X[k] for k, n_ in enumerate(nb)])


def nearest_skin(skin_bvh, X, reach=0.2):
    """Для каждой точки: ближайшая точка кожи, нормаль там и расстояние над кожей по нормали (внутри - меньше 0)."""
    out = []
    for x in X:
        loc, n, _, _ = skin_bvh.find_nearest(Vector(x), reach)
        out.append((np.array(loc), np.array(n), (Vector(x) - loc).dot(n)))
    return out


def shell_fit(P, nb, skin_bvh, gap, loose, floor, smooth=40, disp_smooth=10, passes=4, tag='[Fit]'):
    """Посадка оболочкой. P - вершины детали после того, как она поставлена на место, nb - их соседи.
      1. гладкая копия детали (сглаживание Таубина) - оболочка: складки, швы, карманы, манжета уходят в отступ;
      2. оболочка ложится на кожу: на столько же над кожей, сколько была, но не ближе gap и не дальше loose;
         сдвиг сглаживается по сетке, чтобы соседние вершины не расходились;
      3. каждой вершине возвращается её отступ от оболочки - деталь сохраняет свою форму;
      4. что ушло ближе floor к коже, отодвигается по нормали кожи, не меньше соседей и сглаженно.
    Возвращает новые положения вершин."""
    S = P.copy()
    for _ in range(smooth):
        S = S + TAUBIN[0] * (average(S, nb) - S)
        S = S + TAUBIN[1] * (average(S, nb) - S)
    detail = P - S
    near = nearest_skin(skin_bvh, S)
    gap_was = np.array([d for _, _, d in near])
    target = np.clip(gap_was, gap, loose)
    disp = np.array([loc + n * t for (loc, n, _), t in zip(near, target)]) - S
    for _ in range(disp_smooth):
        disp = 0.5 * disp + 0.5 * average(disp, nb)
    fitted = S + disp + detail
    print(f'{tag} shell: {len(P)} verts, shell over the skin was {np.percentile(gap_was, 5) * 1000:.0f}..'
          f'{np.percentile(gap_was, 95) * 1000:.0f} mm (5..95 %), now {gap * 1000:.0f}..{loose * 1000:.0f}; '
          f'details from the shell up to {np.linalg.norm(detail, axis=1).max() * 1000:.0f} mm')
    for it in range(passes):
        near = nearest_skin(skin_bvh, fitted)
        need = np.array([max(0.0, floor - d) for _, _, d in near])
        normals = np.array([n for _, n, _ in near])
        worst = need.max()
        need = np.array([max([need[k]] + [need[j] for j in n_]) for k, n_ in enumerate(nb)])  # не меньше соседей
        for _ in range(3):
            need = 0.5 * need + 0.5 * average(need, nb)
        fitted += normals * need[:, None]
        print(f'{tag} shell pass {it + 1}: closer to the skin than {floor * 1000:.1f} mm up to {worst * 1000:.1f} mm')
    return fitted


def follow(P, fitted, X, k=6):
    """Куски поверх ткани (кушак, ремни, пряжки) идут за ней: каждая точка X сдвигается как k ближайших вершин
    ткани (P - до посадки, fitted - после), с весом 1/d². Возвращает новые положения X."""
    kd = KDTree(len(P))
    for i, p in enumerate(P):
        kd.insert(p, i)
    kd.balance()
    move = fitted - P
    out = np.empty_like(X)
    for j, x in enumerate(X):
        found = kd.find_n(Vector(x), k)
        w = np.array([1.0 / max(d_, 1e-4) ** 2 for _, _, d_ in found])
        out[j] = x + (move[[i for _, i, _ in found]] * w[:, None]).sum(0) / w.sum()
    return out


def push_skin_bumps(co, movable, tris, skin_pts, skin_normals, clear, radius, passes=4, tag='[Fit]', what='waist'):
    """Бугор кожи проходит между вершинами крупных граней (кушак на пояснице). Каждая точка кожи skin_pts снаружи
    ткани (луч от кожи внутрь упирается в ткань раньше, чем наружу) отодвигает вершины movable рядом наружу по своей
    нормали: на глубину и clear, плавно до radius. Делить такие грани нельзя, если их край прижат под другую часть:
    мелкие куски торчат зубцами (кушак сквозь рваный край ClothChest). co меняется на месте."""
    for it in range(passes):
        now_bvh = BVHTree.FromPolygons([Vector(p) for p in co], tris)
        pokes = []
        for p, n_ in zip(skin_pts, skin_normals):
            n = Vector(n_)
            hin = now_bvh.ray_cast(Vector(p), -n, 0.03)
            hout = now_bvh.ray_cast(Vector(p), n, 0.08)
            if hin[0] is not None and (hout[0] is None or hin[3] < hout[3]):
                pokes.append((p, n_, hin[3]))
        print(f'{tag} {what} pass over skin bumps {it + 1}: skin outside {len(pokes)}' +
              (f', up to {max(d_ for _, _, d_ in pokes) * 1000:.1f} mm' if pokes else ''))
        if not pokes:
            break
        amount = np.zeros(len(movable))
        push = np.zeros((len(movable), 3))
        for p, n, depth in pokes:
            w = np.clip(1.0 - np.linalg.norm(co[movable] - p, axis=1) / radius, 0.0, 1.0)
            a = (3 * w * w - 2 * w * w * w) * (depth + clear)
            more = a > amount
            amount[more] = a[more]
            push[more] = n * a[more, None]
        co[movable] += push


FACE_SAMPLES = np.array([(a / 4, b / 4, 1 - a / 4 - b / 4) for a in range(5) for b in range(5 - a)])  # 15 на грань


def push_faces_off_skin(co, face_tris, skin_bvh, gap, passes=5, tag='[Fit]', what='faces'):
    """Плоская грань между вершинами провисает к телу, и кожа проходит сквозь неё, хотя все вершины над кожей
    (щели на нижней ленте обмоток). Точки внутри каждой грани face_tris - не ближе gap к коже: где ближе, вершины
    грани отодвигаются по нормали кожи. co меняется на месте."""
    for it in range(passes):
        need = np.zeros(len(co))
        dirs = np.zeros((len(co), 3))
        for t in face_tris:
            for x in FACE_SAMPLES @ co[list(t)]:
                loc, n, _, _ = skin_bvh.find_nearest(Vector(x), 0.03)
                if loc is None:
                    continue
                short = gap - (Vector(x) - loc).dot(n)
                for k in t:
                    if short > need[k]:
                        need[k] = short
                        dirs[k] = tuple(n)
        print(f'{tag} {what} over the skin, pass {it + 1}: closer than {gap * 1000:.1f} mm '
              f'{int((need > 0).sum())} verts, up to {need.max() * 1000:.1f} mm')
        if need.max() < 0.0001:
            break
        co += dirs * need[:, None]


def fill_skin_on_texture(a, size, me, target, bleed, tag='[Fit]', what='texture'):
    """На текстуре модели генератора рядом с вырезанной частью - кожа, и края части в развёртке её захватывают:
    на теле игрока розовые полосы, похожие на дыры (обмотки). Кожа (красный заметно больше синего) под гранями,
    у которых все вершины target, и на bleed пикселей вокруг (мип-уровни смешивают соседей) заливается цветом
    ближайших пикселей не кожи. Пиксели других граней не трогаются. a - пиксели RGBA строками снизу, size x size."""
    inside = np.zeros((size, size), bool)
    other = np.zeros((size, size), bool)
    uv = me.uv_layers.active.data
    me.calc_loop_triangles()
    yy, xx = np.mgrid[0:size, 0:size]
    for lt in me.loop_triangles:
        t = np.array([tuple(uv[k].uv) for k in lt.loops]) * size
        x0, y0 = np.floor(t.min(0)).astype(int)
        x1, y1 = np.ceil(t.max(0)).astype(int) + 1
        x0, y0, x1, y1 = max(x0, 0), max(y0, 0), min(x1, size), min(y1, size)
        px, py = xx[y0:y1, x0:x1] + 0.5, yy[y0:y1, x0:x1] + 0.5
        (ax, ay), (bx, by), (cx, cy) = t
        den = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy)
        if abs(den) < 1e-12:
            continue
        l1 = ((by - cy) * (px - cx) + (cx - bx) * (py - cy)) / den
        l2 = ((cy - ay) * (px - cx) + (ax - cx) * (py - cy)) / den
        hit = (l1 >= -0.02) & (l2 >= -0.02) & (1 - l1 - l2 >= -0.02)
        if all(target[v] for v in lt.vertices):
            inside[y0:y1, x0:x1] |= hit
        else:
            other[y0:y1, x0:x1] |= (l1 >= 0) & (l2 >= 0) & (1 - l1 - l2 >= 0)
    zone = inside.copy()
    for _ in range(bleed):
        zone[1:] |= zone[:-1].copy()
        zone[:-1] |= zone[1:].copy()
        zone[:, 1:] |= zone[:, :-1].copy()
        zone[:, :-1] |= zone[:, 1:].copy()
    zone &= ~other
    img = a.reshape(size, size, 4).copy()
    skin = zone & (img[..., 0] > img[..., 2] + 0.04)
    good = zone & ~skin
    print(f'{tag} {what}: {inside.sum()} px under the part, skin in it and around {skin.sum()} px')
    todo = skin.copy()
    for _ in range(400):
        if not todo.any():
            break
        acc = np.zeros_like(img)
        cnt = np.zeros((size, size), np.float32)
        for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            cnt += np.roll(np.roll(good, dy, 0), dx, 1)
            acc += np.roll(np.roll(img * good[..., None], dy, 0), dx, 1)
        fill = todo & (cnt > 0)
        img[fill] = acc[fill] / cnt[fill][:, None]
        good |= fill
        todo &= ~fill
    print(f'{tag} {what}: left unfilled {todo.sum()} px')
    return img.reshape(-1, 4)


# ----------------------------------------------------------------------------------------------
# Маска тела: какую кожу прятать под частью (WornGear прячет треугольник, только если все 3 вершины в маске)
# ----------------------------------------------------------------------------------------------
def sphere_dirs(n=160):
    dirs = []
    for k in range(n):
        zz = 1 - 2 * (k + 0.5) / n
        rr = math.sqrt(1 - zz * zz)
        a = k * math.pi * (3 - math.sqrt(5))
        dirs.append(Vector((rr * math.cos(a), rr * math.sin(a), zz)))
    return dirs


def seen_from_outside(p, n, dirs, bvhs, max_slope=0.5):
    """Кожа видна снаружи, если хоть один луч от неё уходит, ничего не задев. Лучи круче max_slope (sin угла) вниз
    не берутся - камера не смотрит снизу вверх под часть; и вверх - у плотной ткани луч вверх уходит только по зазору
    в 1-3 мм между тканью и телом до края (кожа в паху считалась видной и в приседе выходила сквозь шорты)."""
    p = Vector(p) + Vector(n) * 0.0005
    n = Vector(n)
    for dv in dirs:
        if dv.dot(n) <= 0.05 or abs(dv.z) > max_slope:
            continue
        if all(t.ray_cast(p, dv, 1.0)[0] is None for t in bvhs):
            return True
    return False


def covered_triangles(body, body_rest, body_normal, eligible, covered_at):
    """Треугольники тела (frozenset вершин), целиком закрытые частью: covered_at(точка, нормаль) верно в 10 точках
    треугольника (вершины, середины сторон, центр и между центром и вершинами). Нужно там, где треугольники тела
    крупнее частей и между частями просветы (ленты обмоток): треугольник со всеми вершинами под лентами закрывает
    и просвет, на его месте дыра."""
    body.data.calc_loop_triangles()
    out = set()
    for lt in body.data.loop_triangles:
        vs = tuple(lt.vertices)
        if not all(eligible(i) for i in vs):
            continue
        A, B, C = (Vector(body_rest[i]) for i in vs)
        n = sum((Vector(body_normal[i]) for i in vs), Vector()).normalized()
        g = (A + B + C) / 3
        samples = (A, B, C, (A + B) / 2, (B + C) / 2, (C + A) / 2, g, (g + A) / 2, (g + B) / 2, (g + C) / 2)
        if all(covered_at(s, n) for s in samples):
            out.add(frozenset(vs))
    return out


def add_covered(body, under, tris_ok):
    """К маске under добавляются вершины треугольников tris_ok так, чтобы не спрятался ни один треугольник, которого
    нет в tris_ok: вершина, из-за которой такой треугольник целиком попал бы в маску, остаётся видимой.
    Возвращает (маска, сколько добавлено, сколько осталось видимыми)."""
    extra = {i for t in tris_ok for i in t} - under
    loose = set()
    while True:
        final = under | (extra - loose)
        bad = {i for lt in body.data.loop_triangles if set(lt.vertices) <= final and
               frozenset(lt.vertices) not in tris_ok for i in lt.vertices if i in extra and i not in loose}
        if not bad:
            return final, len(extra - loose), len(loose)
        loose |= bad
