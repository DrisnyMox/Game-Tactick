"""
Чистка текстуры части, вырезанной из модели генератора (Docs/Gear.md, «Что вышло с SunPants»). Текстура модели -
текстура целого персонажа: вокруг кусков части кожа, края кусков в развёртке её захватывают, на ткани шум, царапины
и неровный рисунок, у латуни размытые пятна. Чистка:
  1. каждый пиксель развёртки относится к грани части (texel_map) и получает класс по цвету (classify): ткань, серая
     ткань, латунь, кожа ремня, медь; кожа персонажа и размытые пятна - «плохие»;
  2. класс сглаживается по соседям (край детали ровный, без пилы), плохие пиксели заливаются соседними;
  3. пиксель перекрашивается в цвет класса с картинки генератора, яркость (складки, тени) остаётся - сглаженная,
     у ткани сильнее: царапины и пятна уходят;
  4. на ткань заново рисуется узор (suns): точки ровно по поверхности части в 3D, вокруг каждой - солнце в плоскости
     ткани. По 3D, а не по развёртке: на швах развёртки узор не рвётся.
Координаты - как в place_pants.py: лицом в -Y, Z вверх, рост тела 1,088. Пиксели - строки снизу, RGB 0..1 как
в Image.pixels.
"""
import math

import numpy as np
from mathutils import Vector
from mathutils.kdtree import KDTree

NAVY, GREY, BRASS, LEATHER, COPPER = range(5)
BAD = -1
DEBUG = {}  # классы пикселей последней чистки - для снимков
CLASS_NAMES = ('fabric', 'grey', 'brass', 'leather', 'copper')


def texel_map(me, size):
    """Какая грань (треугольник) в каждом пикселе развёртки и где пиксель в ней. Возвращает (lt - номер
    loop-треугольника или -1, w - барицентрические веса, tri - вершины треугольников, poly - грань треугольника)."""
    me.calc_loop_triangles()
    uv = np.array([tuple(d.uv) for d in me.uv_layers.active.data])
    lts = me.loop_triangles
    tri = np.array([tuple(t.vertices) for t in lts])
    poly = np.array([t.polygon_index for t in lts])
    lt_of = np.full((size, size), -1, np.int32)
    w = np.zeros((size, size, 3), np.float32)
    for k, t in enumerate(lts):
        p = uv[list(t.loops)] * size
        x0, y0 = np.maximum(np.floor(p.min(0)).astype(int), 0)
        x1, y1 = np.minimum(np.ceil(p.max(0)).astype(int) + 1, size)
        if x1 <= x0 or y1 <= y0:
            continue
        py, px = np.mgrid[y0:y1, x0:x1] + 0.5
        (ax, ay), (bx, by), (cx, cy) = p
        den = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy)
        if abs(den) < 1e-12:
            continue
        l1 = ((by - cy) * (px - cx) + (cx - bx) * (py - cy)) / den
        l2 = ((cy - ay) * (px - cx) + (ax - cx) * (py - cy)) / den
        l3 = 1 - l1 - l2
        hit = (l1 >= 0) & (l2 >= 0) & (l3 >= 0)
        lt_of[y0:y1, x0:x1][hit] = k
        w[y0:y1, x0:x1][hit] = np.stack([l1[hit], l2[hit], l3[hit]], 1)
    return lt_of, w, tri, poly


def blur(a, sigma):
    """Гаусс по двум осям (a - H x W или H x W x C)."""
    if sigma <= 0:
        return a
    r = int(math.ceil(sigma * 3))
    k = np.exp(-0.5 * (np.arange(-r, r + 1) / sigma) ** 2)
    k /= k.sum()
    out = a
    for axis in (0, 1):
        pad = [(0, 0)] * a.ndim
        pad[axis] = (r, r)
        p = np.pad(out, pad, mode='edge')
        acc = np.zeros_like(out)
        for i, kv in enumerate(k):
            acc += kv * np.take(p, np.arange(i, i + out.shape[axis]), axis=axis)
        out = acc
    return out


def masked_blur(a, m, sigma):
    """Сглаживание только по пикселям маски m (соседи других классов не смешиваются)."""
    mf = m.astype(np.float32)
    num = blur(a * (mf[..., None] if a.ndim == 3 else mf), sigma)
    den = blur(mf, sigma)
    den = den[..., None] if a.ndim == 3 else den
    return np.where(den > 1e-4, num / np.maximum(den, 1e-4), a)


def features(c):
    r, g, b = c[..., 0], c[..., 1], c[..., 2]
    mx, mn = c.max(-1), c.min(-1)
    lum = c @ np.array([0.299, 0.587, 0.114], np.float32)
    sat = (mx - mn) / np.maximum(mx, 1e-6)
    return r, g, b, lum, sat


def classify(c, grey_only, grey_ok, leather_ok):
    """Класс пикселя по цвету. grey_only - грани, помеченные пользователем под цвет качества (там только серая ткань),
    grey_ok - где серое может быть (на латуни серое - размытое пятно генератора), leather_ok - грани ремня."""
    r, g, b, lum, sat = features(c)
    skin = (r - g > 0.07) & (g - b > 0.02) & (g - b < 0.16) & (lum > 0.42) & (r - b < 0.42)
    grey = (sat < 0.14) & (lum > 0.40)
    brass = (g - b > 0.12) & (r - b > 0.2) & (lum > 0.26) & ~skin
    copper = (r - g > 0.16) & (r - b > 0.3) & (sat > 0.5) & ~brass
    leather = (r - b > 0.07) & (lum > 0.1) & (lum < 0.34) & ~brass & ~copper
    cls = np.full(c.shape[:2], NAVY, np.int8)
    cls[leather & leather_ok] = LEATHER
    cls[copper] = COPPER
    cls[brass] = BRASS
    cls[grey & grey_ok] = GREY
    cls[skin] = BAD
    # серая ткань под цвет качества: тёмно-синее и кожа там - захваченная ткань или кожа рядом с куском развёртки
    g_bad = (b > r + 0.01) & (lum < 0.32) | skin | (sat > 0.3)
    cls[grey_only] = np.where(g_bad[grey_only], BAD, GREY)
    return cls


def vote(cls, valid, n_classes, size=2, rounds=2):
    """Сглаживание класса: каждый пиксель берёт самый частый класс в окне (2*size+1)^2 среди пикселей valid.
    Край детали становится ровным, одиночные пиксели другого класса уходят."""
    for _ in range(rounds):
        counts = []
        for k in range(n_classes):
            m = ((cls == k) & valid).astype(np.float32)
            acc = np.zeros_like(m)
            p = np.pad(m, size)
            for dy in range(2 * size + 1):
                for dx in range(2 * size + 1):
                    acc += p[dy:dy + m.shape[0], dx:dx + m.shape[1]]
            counts.append(acc)
        counts = np.stack(counts, -1)
        best = counts.argmax(-1).astype(np.int8)
        has = counts.max(-1) > 0
        cls = np.where(valid & (cls >= 0) & has, best, cls)
    return cls


def inpaint(cls, lum, region, steps=400):
    """Плохие пиксели (cls == BAD) внутри region берут класс и яркость соседей того же класса: пятна кожи и размытые
    места заливаются окружающей деталью."""
    cls, lum = cls.copy(), lum.copy()
    todo = region & (cls == BAD)
    n0 = int(todo.sum())
    for _ in range(steps):
        if not todo.any():
            break
        good = region & (cls >= 0)
        best_cnt = np.zeros(cls.shape, np.float32)
        best_cls = np.full(cls.shape, BAD, np.int8)
        best_lum = np.zeros(cls.shape, np.float32)
        for k in range(len(CLASS_NAMES)):
            m = good & (cls == k)
            cnt = np.zeros(cls.shape, np.float32)
            acc = np.zeros(cls.shape, np.float32)
            for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                cnt += np.roll(np.roll(m, dy, 0), dx, 1)
                acc += np.roll(np.roll(lum * m, dy, 0), dx, 1)
            more = cnt > best_cnt
            best_cnt[more] = cnt[more]
            best_cls[more] = k
            best_lum[more] = acc[more] / cnt[more]
        fill = todo & (best_cnt > 0)
        cls[fill] = best_cls[fill]
        lum[fill] = best_lum[fill]
        todo &= ~fill
    return cls, lum, n0, int(todo.sum())


def poisson_points(V, tris, spacing, seed=7):
    """Точки на поверхности треугольников tris (вершины V) не ближе spacing друг к другу: ровная россыпь узора."""
    A, B, C = V[tris[:, 0]], V[tris[:, 1]], V[tris[:, 2]]
    area = 0.5 * np.linalg.norm(np.cross(B - A, C - A), axis=1)
    rng = np.random.default_rng(seed)
    n = int(area.sum() / (spacing * spacing) * 12)
    pick = rng.choice(len(tris), n, p=area / area.sum())
    u, v = rng.random(n), rng.random(n)
    flip = u + v > 1
    u[flip], v[flip] = 1 - u[flip], 1 - v[flip]
    P = A[pick] + (B[pick] - A[pick]) * u[:, None] + (C[pick] - A[pick]) * v[:, None]
    N = np.cross(B - A, C - A)[pick]
    N /= np.maximum(np.linalg.norm(N, axis=1, keepdims=True), 1e-12)
    cell = spacing / math.sqrt(3)
    grid = {}
    keep = []
    for i, p in enumerate(P):
        key = tuple((p // cell).astype(int))
        ok = True
        for dx in (-2, -1, 0, 1, 2):
            for dy in (-2, -1, 0, 1, 2):
                for dz in (-2, -1, 0, 1, 2):
                    j = grid.get((key[0] + dx, key[1] + dy, key[2] + dz))
                    if j is not None and np.linalg.norm(P[j] - p) < spacing:
                        ok = False
                        break
                if not ok:
                    break
            if not ok:
                break
        if ok:
            grid[key] = i
            keep.append(i)
    return P[keep], N[keep]


def sun_coverage(x, y, radius, aa):
    """Солнце в плоскости ткани (x, y от центра): кольцо, точка в середине и 12 треугольных лучей. 0..1."""
    r = np.hypot(x, y) / radius
    a = np.arctan2(y, x)
    soft = aa / radius
    step = lambda e, v: np.clip((v - e) / soft + 0.5, 0, 1)
    ring = step(0.36, r) * (1 - step(0.48, r))
    dot = 1 - step(0.16, r)
    ray_a = np.abs(((a + math.pi / 12) % (math.pi / 6)) - math.pi / 12)  # угол до оси ближайшего луча
    t = np.clip((r - 0.58) / 0.42, 0, 1)
    half = 0.17 * (1 - t)  # полуширина луча в радианах, к концу сходит на нет
    ray = step(0.58, r) * (1 - step(1.0, r)) * np.clip((half - ray_a) * r * radius / aa + 0.5, 0, 1)
    return np.clip(ring + dot + ray, 0, 1)


def draw_suns(img, where, pos, nrm, centers, cnormals, radius, color, alpha, texel):
    """Узор на пикселях where: ближайшее солнце по 3D, координаты в плоскости ткани у его центра (ось x - вбок,
    y - вверх по телу). Солнце не рисуется на дальней стороне (другая нога): нормаль пикселя должна смотреть
    туда же, и пиксель - лежать в плоскости солнца."""
    kd = KDTree(len(centers))
    for i, c in enumerate(centers):
        kd.insert(c, i)
    kd.balance()
    ys, xs = np.nonzero(where)
    P, Nn = pos[ys, xs], nrm[ys, xs]
    near = np.empty(len(P), np.int64)
    dist = np.empty(len(P))
    for k, p in enumerate(P):
        _, j, d = kd.find(Vector(p))
        near[k], dist[k] = j, d
    ok = dist < radius * 1.2
    C, CN = centers[near], cnormals[near]
    up = np.array([0.0, 0.0, 1.0])
    e1 = np.cross(up, CN)
    e1 /= np.maximum(np.linalg.norm(e1, axis=1, keepdims=True), 1e-9)
    e2 = np.cross(CN, e1)
    d = P - C
    x, y, h = (d * e1).sum(1), (d * e2).sum(1), (d * CN).sum(1)
    ok &= (np.abs(h) < radius * 0.6) & ((Nn * CN).sum(1) > 0.4)
    cov = np.where(ok, sun_coverage(x, y, radius, texel), 0.0)
    sub = img[ys, xs]
    img[ys, xs] = sub + (np.array(color) - sub) * (cov * alpha)[:, None]
    return int((cov > 0.5).sum())


def bleed(img, good, steps):
    """Цвет кусков продлевается наружу на steps пикселей (мип-уровни смешивают соседей)."""
    good = good.copy()
    for _ in range(steps):
        acc = np.zeros_like(img)
        cnt = np.zeros(good.shape, np.float32)
        for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            cnt += np.roll(np.roll(good, dy, 0), dx, 1)
            acc += np.roll(np.roll(img * good[..., None], dy, 0), dx, 1)
        fill = ~good & (cnt > 0)
        img[fill] = acc[fill] / cnt[fill][:, None]
        good |= fill
    return img


def opening(m, n):
    """Сжатие маски на n пикселей и расширение обратно: остаются только пятна толще 2n."""
    def shift_all(a, op):
        out = a.copy()
        for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            out = op(out, np.roll(np.roll(a, dy, 0), dx, 1))
        return out
    for _ in range(n):
        m = shift_all(m, np.logical_and)
    for _ in range(n + 1):
        m = shift_all(m, np.logical_or)
    return m


def enclosed(me, wall, max_area):
    """Грани, отрезанные от остальной сетки гранями wall, - связные куски площадью меньше max_area (окошко в рамке
    наколенника). Соседи - по общему ребру."""
    faces_of = {}
    for p in me.polygons:
        for ek in p.edge_keys:
            faces_of.setdefault(ek, []).append(p.index)
    nb = [[] for _ in me.polygons]
    for fs in faces_of.values():
        for a in fs:
            nb[a] += [b_ for b_ in fs if b_ != a]
    area = np.array([p.area for p in me.polygons])
    out = np.zeros(len(me.polygons), bool)
    seen = wall.copy()
    for s in range(len(me.polygons)):
        if seen[s]:
            continue
        comp, stack, touches = [], [s], False
        seen[s] = True
        while stack:
            a = stack.pop()
            comp.append(a)
            for b_ in nb[a]:
                if wall[b_]:
                    touches = True
                elif not seen[b_]:
                    seen[b_] = True
                    stack.append(b_)
        if touches and area[comp].sum() < max_area:
            out[comp] = True
    return out


def behind(me, co, cover, pos, nrm, where, reach):
    """Пиксели where ближе reach к граням cover. Панель на бедре - открытая коробка: сквозь окна видна ткань бедра
    под разными углами, и генератор нарисовал на ней продолжение решётки."""
    from mathutils.bvhtree import BVHTree
    polys = [tuple(p.vertices) for p in me.polygons]
    cov = np.flatnonzero(cover)
    tree = BVHTree.FromPolygons([Vector(p) for p in co], [polys[i] for i in cov])
    vs = co[np.unique(np.concatenate([np.array(polys[i]) for i in cov]))]
    lo, hi = vs.min(0) - reach, vs.max(0) + reach
    ys, xs = np.nonzero(where & np.all((pos > lo) & (pos < hi), -1))
    out = np.zeros(where.shape, bool)
    for y, x in zip(ys, xs):
        p, n = Vector(pos[y, x]), Vector(nrm[y, x])
        out[y, x] = tree.find_nearest(p, reach)[0] is not None
    return out


def bleed_into(img, good, todo, steps=300):
    """Пиксели todo берут средний цвет соседей good, слой за слоем от края пятна."""
    img, good, todo = img.copy(), good.copy(), todo.copy()
    for _ in range(steps):
        if not todo.any():
            break
        acc = np.zeros_like(img)
        cnt = np.zeros(good.shape, np.float32)
        for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            cnt += np.roll(np.roll(good, dy, 0), dx, 1)
            acc += np.roll(np.roll(img * good[..., None], dy, 0), dx, 1)
        fill = todo & (cnt > 0)
        img[fill] = acc[fill] / cnt[fill][:, None]
        good |= fill
        todo &= ~fill
    return img


def clean(me, co, poly_piece, quality_poly, base, normal, rough, size, cfg, tag='[Tex]'):
    """Чистка текстур части по правилам cfg (place_pants.PARTS[...]['texture']). me - сетка с развёрткой и
    материалами, co - вершины (рисунок узора - по этой форме), poly_piece - имя куска модели у каждой грани,
    quality_poly - грани, помеченные пользователем под цвет качества (там только серая ткань), base/normal/rough -
    пиксели size x size x 4. Возвращает новые (base, normal, rough)."""
    lt_of, w, tri, poly = texel_map(me, size)
    inside = lt_of >= 0
    li = np.maximum(lt_of, 0)
    pid = poly[li]
    npoly = len(me.polygons)
    quality = quality_poly[pid] & inside
    rgb = base[..., :3].copy()
    r, g, b, lum, sat = features(rgb)

    def share(px):
        """Доля пикселей px у каждой грани."""
        tot = np.bincount(pid[inside], minlength=npoly)
        return np.bincount(pid[inside & px], minlength=npoly) / np.maximum(tot, 1)

    # Мелкие детали (латунь, ячейки панелей, провода, решётка наколенников) - тонкие линии в 1-3 пикселя: класс
    # по соседям их стирает. Их пиксели остаются своими, только цвет подтягивается к картинке, а кожа и размытые
    # серые пятна заливаются соседями. Это куски hard и грани с латунью (наколенники)
    brass_px = (g - b > 0.12) & (r - b > 0.2) & (lum > 0.2)
    # окошки наколенников (тёмные ячейки и тонкая светлая решётка) - куски граней, со всех сторон окружённые латунью
    brassy = share(brass_px) > cfg['brass_share']
    hard_poly = np.isin(poly_piece, cfg['hard']) | brassy | enclosed(me, brassy, cfg['window_area'])
    # ткань под панелью на бедре: у панели в сетке два окна, и сквозь них видна ткань, на которую генератор
    # нарисовал продолжение решётки. Чистая ткань там выглядела дырой в панели
    # точка и нормаль ткани в каждом пикселе (по форме co)
    V = np.asarray(co)
    pos = (V[tri[li]] * w[..., None]).sum(2)
    tn = np.cross(V[tri[:, 1]] - V[tri[:, 0]], V[tri[:, 2]] - V[tri[:, 0]])
    area = 0.5 * np.linalg.norm(tn, axis=1).sum()
    tn /= np.maximum(np.linalg.norm(tn, axis=1, keepdims=True), 1e-12)
    nrm = tn[li]
    hard = hard_poly[pid] & inside & ~quality
    near_panel = behind(me, V, np.isin(poly_piece, cfg['panel']), pos, nrm, inside & ~hard & ~quality, cfg['behind_reach'])
    hard |= near_panel
    # Ремень под коленом - грани, где коричневого больше половины: тёмно-рыжий ореол вокруг латуни на ткани - не ремень
    leather_px = (r - b > 0.07) & (lum > 0.1) & (lum < 0.34) & ~brass_px
    leather_ok = (share(leather_px) > 0.5)[pid] & inside
    cls = classify(rgb, quality, inside & ~hard & ~quality, leather_ok)
    cls[~inside] = BAD
    soft = inside & ~hard
    # серое на ткани - только полосы толще 2 пикселей: светлые царапины на ткани уходят, а полосы по шву и решётка
    # окошек наколенников остаются (сглаживание класса по соседям стирало тонкую решётку)
    thin = soft & ~quality & (cls == GREY) & ~opening(soft & (cls == GREY), 1)
    cls[thin] = NAVY
    # тёмно-синие пятна на ремне (генератор закрасил ремень тканью) - заливка ремнём
    cls[leather_ok & soft & (b > r)] = BAD
    cls, lum_f, n_bad, n_left = inpaint(cls, lum, soft)
    print(f'{tag} texture: {int(inside.sum())} px under the part (details {int(hard.sum())}), '
          f'repainted spots on cloth {n_bad} px, left {n_left}')

    out = np.zeros_like(rgb)
    for k, name in enumerate(CLASS_NAMES):
        m = soft & (cls == k)
        if not m.any():
            continue
        ref = np.array(cfg['palette'][name], np.float32)
        sigma, lo, hi = cfg['shade'][name]
        lm = lum_f.copy()
        med = float(np.median(lm[m]))
        lm = np.clip(lm, med * lo, med * hi)  # царапины и пятна ярче и темнее предела срезаются
        sh = masked_blur(lm, m, sigma) / med
        out[m] = ref * sh[m][:, None]
        print(f'{tag}   {name}: {int(m.sum())} px ({m.sum() / inside.sum() * 100:.0f} %), model median luma {med:.3f}')

    # детали: кожа и серые размытые пятна (у панелей генератор закрасил ячейки серым) - заливка соседями
    skin = (r - g > 0.07) & (g - b > 0.02) & (g - b < 0.16) & (lum > 0.42) & (r - b < 0.42)
    # серое пятно - крупное: тонкие светлые линии решётки панелей и наколенников остаются (открытие на 2 пикселя)
    # (у панели на ткани серое - полоса по шву, она остаётся)
    blob = opening((sat < 0.12) & (lum > 0.38) & hard & ~near_panel, 2)
    hard_bad = hard & (skin | blob)
    hc = rgb.copy()
    for k, name in ((BRASS, 'brass'), (NAVY, 'fabric'), (COPPER, 'copper')):
        m = hard & ~hard_bad & (cls == k) if k != NAVY else hard & ~hard_bad & ~brass_px & (cls != COPPER)
        if m.any():
            # наполовину к цвету картинки: полный сдвиг белил латунь (у модели она темнее и рыжее)
            gain = np.sqrt(np.array(cfg['palette'][name], np.float32) / np.median(rgb[m], 0))
            hc[m] = rgb[m] * gain
    good = hard & ~hard_bad
    hc = bleed_into(hc, good, hard_bad)
    out[hard] = hc[hard]
    print(f'{tag}   details: {int(hard.sum())} px, repainted spots {int(hard_bad.sum())} px')

    # узор солнц на ткани (не на латуни и проводах)
    fabric_tris = np.isin(poly_piece[poly], cfg['fabric_pieces']) & ~quality_poly[poly]
    centers, cn = poisson_points(V, tri[fabric_tris], cfg['sun_spacing'])
    texel = math.sqrt(area / max(inside.sum(), 1))  # размер пикселя на поверхности
    # солнц нет у латуни: окошки наколенников - тёмные ячейки, это не ткань. Грани ближе SUN_CLEAR к граням,
    # где латуни больше четверти
    pc = np.array([tuple(p.center) for p in me.polygons])
    brassy = np.flatnonzero(brassy)
    kd = KDTree(len(brassy))
    for k_, i_ in enumerate(brassy):
        kd.insert(pc[i_], k_)
    kd.balance()
    near_brass = np.array([kd.find(Vector(p))[2] < cfg['sun_clear'] if len(brassy) else False for p in pc])
    where = soft & (cls == NAVY) & ~near_brass[pid]
    n_sun = draw_suns(out, where, pos, nrm, centers, cn, cfg['sun_radius'], cfg['palette']['sun'], cfg['sun_alpha'],
                      texel * 0.75)
    print(f'{tag}   suns: {len(centers)} on the fabric, {n_sun} px, texel {texel * 1000:.2f} mm')

    out = np.where(soft[..., None], masked_blur(out, soft, 0.6), out)  # край классов ткани без ступенек
    res = base.copy()
    res[..., :3] = np.clip(bleed(out, inside, 12), 0, 1)

    # нормали и шероховатость ткани: царапины и старый рисунок узора убираются сглаживанием
    nm = normal.copy()
    fab = soft & (cls == NAVY)
    nm[..., :3] = np.where(fab[..., None], masked_blur(normal[..., :3], fab, 2.5), normal[..., :3])
    ro = rough.copy()
    ro[..., :3] = np.where(fab[..., None], masked_blur(rough[..., :3], fab, 4.0), rough[..., :3])
    DEBUG.update(cls=cls, hard=hard, hard_bad=hard_bad, inside=inside, quality=quality)
    return res, nm, ro


def face_mask(me, faces, size, gutter=8):
    """Маска цвета качества по граням faces (0..1, size x size строками снизу). Край - по краю граней: у штанов
    Культа Солнца грани под цвет качества - сами повязки и обмотки. Маска продлена в пустоту развёртки на gutter
    пикселей (мип-уровни смешивают соседей, иначе край полосы темнел бы)."""
    lt_of, _, _, poly = texel_map(me, size)
    inside = lt_of >= 0
    m = np.where(inside, faces[poly[np.maximum(lt_of, 0)]], False).astype(np.float32)
    good = inside.copy()
    for _ in range(gutter):
        acc = np.zeros_like(m)
        cnt = np.zeros_like(m)
        for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            cnt += np.roll(np.roll(good, dy, 0), dx, 1)
            acc += np.roll(np.roll(m * good, dy, 0), dx, 1)
        fill = ~good & (cnt > 0)
        m[fill] = acc[fill] / cnt[fill]
        good |= fill
    print(f'[Tex] quality mask: {int((m > 0.5).sum())} px ({(m > 0.5).mean() * 100:.1f} % of texture)')
    return m
