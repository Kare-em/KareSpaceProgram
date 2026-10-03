# Стартовые комплексы исторических ракет (GDD §7). Blender 5.2, метры, натуральная величина.
# Запуск в Blender (MCP): exec(open(r"C:\CocosGames\KareSpaceProgram\Tools\blender\launch_pads.py", encoding="utf-8").read())
# Требует hulls_lib.py (lathe, box, strut, build, export, isolate) — подгружается ниже, если не загружена.
#
# Система координат: начало — точка грунта под осью ракеты, X — восток, Y — север, Z — вверх (в Unity: +Z вверх -> +Y).
# Палуба стола (опора ракеты) на PAD_H = 6 м — пара: LaunchSite.PadHeight (Core/Bodies/Terrain.cs) и LaunchPadView.
# Низ ракеты (срез сопел первой секции) стоит ровно на палубе; контакты (ферма, захваты) считаны от неё.
# Слоты материалов (порядок 0..3 обязателен — Unity нумерует субмеши по слотам, поэтому build() сортирует грани):
#   0 Steel (сталь комплекса), 1 Dark (газоотвод, оборудование, захваты), 2 Red (красная окраска), 3 White (площадки, стрелы).
# Реальные цвета задаёт LaunchPadView.Palette — здесь только заглушки для вьюпорта.
import math
import bpy, bmesh
from mathutils import Vector, Matrix

if "lathe" not in globals():
    exec(open(r"C:\CocosGames\KareSpaceProgram\Tools\blender\hulls_lib.py", encoding="utf-8").read())

PAD_H = 6.0
PAD_SLOTS = [("Steel", (0.50, 0.50, 0.48)), ("Dark", (0.10, 0.10, 0.10)), ("Red", (0.55, 0.13, 0.08)), ("White", (0.80, 0.80, 0.78))]
S, D, RD, WH = 0, 1, 2, 3

# ---- пары с LaunchPadView.cs (Plan*): меняешь здесь — меняй там
MAST_REACH = 8.45      # Pad_Mast: от оси шарнира до упора вылета (тип «наклонная мачта»)
MAST_H = 24.0          # Pad_Mast: высота
ARM_LIGHT_LEN = 10.0   # Pad_Arm_Light: длина от шарнира до упора
ARM_HEAVY_LEN = 14.0   # Pad_Arm_Heavy: то же
PROTON_TOWER = (-17.5, 0.0, 4.2, 62.0)       # кабельная мачта «Протона»: x, y центра, сторона, высота над столом
PROTON_TRUSS = (44.0, 0.0, 11.0, 12.0, 66.0)  # обслуживающая ферма в парковке: x, y, ширина X, глубина Y, высота
ATLAS_TOWER = (-12.0, 0.0, 3.0, 36.0)
TITAN_TOWER = (0.0, 12.5, 3.0, 38.0)
SATURN_TOWER = (-24.0, 0.0, 12.2, 120.0)     # LUT: x, y центра, сторона, высота над палубой


# ------------------------------------------------------------------ помощники
def bx(bm, x0, y0, z0, x1, y1, z1, mat=S):
    box(bm, ((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2), (x1 - x0, y1 - y0, z1 - z0), mat)


def sq(bm, p, q, rad, mat=S):
    """Прямая стойка с сечением в 4 грани (8 треугольников)."""
    strut(bm, p, q, rad, mat, segs=4)


def lattice(bm, cx, cy, w, d, z0, z1, n, rl=0.2, rb=0.1, mat=S, taper=1.0):
    """Решётчатая башня: 4 ноги, пояса и X-раскосы на n панелях. taper — доля сечения наверху."""
    def pts(z):
        k = 1 + (taper - 1) * (z - z0) / (z1 - z0)
        return [(cx + sx * w / 2 * k, cy + sy * d / 2 * k, z) for sx, sy in ((-1, -1), (1, -1), (1, 1), (-1, 1))]
    lv = [pts(z0 + (z1 - z0) * i / n) for i in range(n + 1)]
    for j in range(4):
        sq(bm, lv[0][j], lv[-1][j], rl, mat)
    for i in range(n + 1):
        for j in range(4):
            sq(bm, lv[i][j], lv[i][(j + 1) % 4], rb, mat)
    for i in range(n):
        for j in range(4):
            a, b = j, (j + 1) % 4
            sq(bm, lv[i][a], lv[i + 1][b], rb, mat)
            sq(bm, lv[i][b], lv[i + 1][a], rb, mat)


def floors(bm, cx, cy, w, d, zs, mat=WH, t=0.18):
    for z in zs:
        bx(bm, cx - w / 2, cy - d / 2, z - t, cx + w / 2, cy + d / 2, z, mat)


def girder_y(bm, y0, y1, w, h, n, z0=0.0, cx=0.0, rc=0.09, rb=0.05, mat=S):
    """Решётчатая балка вдоль Y: сечение w (X) × h (Z), низ на z0."""
    ys = [y0 + (y1 - y0) * i / n for i in range(n + 1)]
    cs = [(-w / 2, z0), (w / 2, z0), (w / 2, z0 + h), (-w / 2, z0 + h)]
    for x, z in cs:
        sq(bm, (cx + x, y0, z), (cx + x, y1, z), rc, mat)
    for i in range(n + 1):
        for j in range(4):
            a, b = cs[j], cs[(j + 1) % 4]
            sq(bm, (cx + a[0], ys[i], a[1]), (cx + b[0], ys[i], b[1]), rb, mat)
    for i in range(n):
        for j in range(4):
            a, b = cs[j], cs[(j + 1) % 4]
            if (i + j) % 2 == 0:
                sq(bm, (cx + a[0], ys[i], a[1]), (cx + b[0], ys[i + 1], b[1]), rb, mat)
            else:
                sq(bm, (cx + b[0], ys[i], b[1]), (cx + a[0], ys[i + 1], a[1]), rb, mat)


def gable(bm, cx, cy, across, along, z0, h, mat=D, axis='y'):
    """Двускатный дефлектор: гребень вдоль axis, ширина across, длина along, высота h (газоотвод)."""
    def P(u, v, z):
        return (cx + u, cy + v, z) if axis == 'y' else (cx + v, cy + u, z)
    a, l = across / 2, along / 2
    b = [bm.verts.new(P(u, v, z0)) for u, v in ((-a, -l), (a, -l), (a, l), (-a, l))]
    r0 = bm.verts.new(P(0, -l, z0 + h))
    r1 = bm.verts.new(P(0, l, z0 + h))
    fs = [bm.faces.new(f) for f in ((b[0], b[1], b[2], b[3]), (b[0], b[1], r0), (b[2], b[3], r1),
                                      (b[3], b[0], r0, r1), (b[1], b[2], r1, r0))]
    bmesh.ops.recalc_face_normals(bm, faces=fs)
    for f in fs:
        f.material_index = mat
        f.smooth = False


def placed(bm, fn, matrix):
    """Строит fn() и применяет матрицу только к новым вершинам."""
    before = set(bm.verts)
    fn()
    bmesh.ops.transform(bm, matrix=matrix, verts=[v for v in bm.verts if v not in before])


def sbuild(name, fn):
    """build() из hulls_lib со слотами комплекса; все 4 слота занимаем (иначе FBX сдвинет субмеши)."""
    global SLOTS
    old = SLOTS
    SLOTS = PAD_SLOTS

    def f(bm):
        fn(bm)
        used = {fc.material_index for fc in bm.faces}
        for s in range(4):
            if s not in used:
                bx(bm, 0.2 * s - 0.01, -0.01, -0.32, 0.2 * s + 0.01, 0.01, -0.3, s)   # крошечный куб под грунтом (свой на слот: иначе remove_doubles склеит)
        for fc in bm.faces:
            fc.smooth = False
    try:
        res = build(name, f)
    finally:
        SLOTS = old
    ob = bpy.data.objects[name]
    bb = [Vector(c) for c in ob.bound_box]
    res["x"] = (round(min(v.x for v in bb), 2), round(max(v.x for v in bb), 2))
    res["y"] = (round(min(v.y for v in bb), 2), round(max(v.y for v in bb), 2))
    return res


# ------------------------------------------------------------------ Р-7: Гагаринский старт
def b_pad_r7(bm):
    # Дефлектор-«крыша» в котловане: расщепляет струю на два рукава (газоотводный лоток уходит на юг).
    gable(bm, 0, -1.0, 7.0, 9.6, 0.0, 2.8, D)
    # Мост через лоток под шарниром южной фермы (ширина лотка 6 м — пара: LaunchPadView.PlanR7).
    bx(bm, -3.4, -6.6, 5.0, 3.4, -4.4, PAD_H + 0.1, S)
    # Низкие бункеры-кабельные вводы у кромки площадки.
    bx(bm, 13.0, -17.0, PAD_H, 19.0, -11.0, PAD_H + 2.6, D)
    bx(bm, -19.0, 11.0, PAD_H, -13.0, 17.0, PAD_H + 2.6, D)


def b_pad_mast(bm):
    # Кабель-заправочная мачта: начало — шарнир у основания, мачта вверх (+Z), вылет к ракете в −Y.
    bx(bm, -0.9, -0.9, 0.0, 0.9, 0.9, 0.35, D)
    lattice(bm, 0, 0, 1.6, 1.6, 0.35, MAST_H, 8, rl=0.09, rb=0.05, taper=0.55)
    girder_y(bm, -0.6, -(MAST_REACH - 0.45), 0.7, 0.8, 6, z0=MAST_H - 4.6, rc=0.06, rb=0.035)
    bx(bm, -0.5, -MAST_REACH, MAST_H - 5.0, 0.5, -(MAST_REACH - 0.45), MAST_H - 3.6, RD)


# ------------------------------------------------------------------ «Протон»: площадка 81/200
def b_pad_proton(bm):
    # Стол-кольцо (восьмиугольник) на палубе; 6 опор по краю котлована (HoleHalf 9.5 — пара: PlanProton).
    lathe(bm, [(4.4, 4.7), (8.2, 4.7), (8.2, PAD_H), (4.4, PAD_H)], 8, S, closed=True, az0=22.5)
    for k in range(6):
        a = math.radians(60 * k)
        px, py = 7.2 * math.cos(a), 7.2 * math.sin(a)
        bx(bm, px - 0.7, py - 0.7, 0.0, px + 0.7, py + 0.7, 4.7, S)
        # Кронштейны удержания — под днища боковых баков (центры баков на az 30+60k, r 2.85).
        b = math.radians(30 + 60 * k)
        c, s = math.cos(b), math.sin(b)
        sq(bm, (5.3 * c, 5.3 * s, PAD_H), (3.95 * c, 3.95 * s, PAD_H + 2.0), 0.25, S)
        bx(bm, 3.65 * c - 0.3, 3.65 * s - 0.3, PAD_H + 1.75, 3.65 * c + 0.3, 3.65 * s + 0.3, PAD_H + 2.15, D)
    # Конический отражатель по оси; вершина на 1,7 м ниже срезов сопел.
    lathe(bm, [(5.0, 0.05), (2.8, 2.6), (0.0, 4.3)], 24, D)
    # Высокая кабель-заправочная мачта.
    tx, ty, tw, th = PROTON_TOWER
    lattice(bm, tx, ty, tw, tw, PAD_H - 0.1, PAD_H + th, 15, rl=0.2, rb=0.09, mat=S)
    floors(bm, tx, ty, tw + 0.6, tw + 0.6, [PAD_H + 8 * k for k in range(1, 8)], WH)
    bx(bm, tx - 0.5, ty - 0.5, PAD_H + th, tx + 0.5, ty + 0.5, PAD_H + th + 7.0, RD)
    # Обслуживающая ферма откачена на рельсы в сторону.
    ux, uy, uw, ud, uh = PROTON_TRUSS
    lattice(bm, ux, uy, uw, ud, 0.9, uh, 15, rl=0.3, rb=0.14, mat=S)
    floors(bm, ux, uy, uw, ud, [12.0 * k for k in range(1, 6)], WH)
    bx(bm, ux - uw / 2 - 1.2, uy - 5.6, 0.4, ux + uw / 2 + 1.2, uy - 4.8, 0.9, D)
    bx(bm, ux - uw / 2 - 1.2, uy + 4.8, 0.4, ux + uw / 2 + 1.2, uy + 5.6, 0.9, D)
    bx(bm, ux - 3.0, uy - 3.0, uh, ux + 3.0, uy + 3.0, uh + 3.0, RD)


# ------------------------------------------------------------------ «Редстоун» / «Юнона»: кольцевой стол
def b_pad_redstone(bm):
    # Кольцо стола (внутр. 0,95 / внеш. 2,5), палуба = его верх. Стабилизаторы ракеты (r до 1,79) лежат на кольце.
    lathe(bm, [(0.95, PAD_H - 0.6), (2.5, PAD_H - 0.6), (2.5, PAD_H), (0.95, PAD_H)], 16, S, closed=True)
    legs = [(sx * 1.6, sy * 1.6) for sx in (-1, 1) for sy in (-1, 1)]
    for x, y in legs:
        bx(bm, x - 0.18, y - 0.18, 0.0, x + 0.18, y + 0.18, PAD_H - 0.6, S)
    pairs = [(0, 1), (1, 3), (3, 2), (2, 0)]
    for a, b in pairs:
        (xa, ya), (xb, yb) = legs[a], legs[b]
        sq(bm, (xa, ya, 0.4), (xb, yb, 4.8), 0.07, S)
        sq(bm, (xa, ya, 4.8), (xb, yb, 0.4), 0.07, S)
    for k in range(4):
        a = math.radians(90 * k)
        bx(bm, 1.15 * math.cos(a) - 0.17, 1.15 * math.sin(a) - 0.17, PAD_H, 1.15 * math.cos(a) + 0.17, 1.15 * math.sin(a) + 0.17, PAD_H + 0.28, D)
    lathe(bm, [(2.4, 0.05), (1.3, 1.3), (0.0, 2.5)], 16, D)
    bx(bm, 8.0, -10.0, 0.0, 11.0, -7.0, 2.2, D)   # на грунте (площадка Редстоуна без палубы), не на отметке стола


# ------------------------------------------------------------------ «Атлас»: LC-14 / LC-12 / LC-36
def b_pad_atlas(bm):
    # Две силовые балки захвата бустерного фланца (r 1,62, низ на z=0,9 над палубой): консоль с опорой на кромку проёма.
    for s in (1, -1):
        y0, y1 = sorted((s * 1.15, s * 3.9))
        bx(bm, -0.45, y0, PAD_H, 0.45, y1, PAD_H + 0.88, S)
        j0, j1 = sorted((s * 1.1, s * 1.65))
        bx(bm, -0.65, j0, PAD_H + 0.3, 0.65, j1, PAD_H + 0.88, D)
    gable(bm, 0, 0, 5.6, 6.0, 0.0, 2.6, D, axis='x')
    # Кабельная мачта (umbilical tower) с площадками.
    tx, ty, tw, th = ATLAS_TOWER
    lattice(bm, tx, ty, tw, tw, PAD_H - 0.1, PAD_H + th, 10, rl=0.15, rb=0.07)
    floors(bm, tx, ty, tw + 0.5, tw + 0.5, [PAD_H + 6 * k for k in range(1, 7)], WH)
    bx(bm, tx - 0.4, ty - 0.4, PAD_H + th, tx + 0.4, ty + 0.4, PAD_H + th + 5.0, RD)
    bx(bm, 7.0, 8.0, PAD_H, 11.0, 12.0, PAD_H + 2.4, D)


# ------------------------------------------------------------------ «Титан II»: LC-19
def b_pad_titan(bm):
    # Четыре захвата юбки (кромка r 1,525 на z=1,2 над палубой) на консолях от кромки проёма (±3,5).
    for sx in (-1, 1):
        for sy in (-1, 1):
            y0, y1 = sorted((sy * 1.0, sy * 3.9))
            bx(bm, sx * 1.1 - 0.25, y0, PAD_H, sx * 1.1 + 0.25, y1, PAD_H + 0.9, S)
            j0, j1 = sorted((sy * 1.0, sy * 1.35))
            bx(bm, sx * 1.1 - 0.2, j0, PAD_H + 0.9, sx * 1.1 + 0.2, j1, PAD_H + 1.2, D)
    gable(bm, 0, 0, 6.0, 6.4, 0.0, 2.8, D, axis='x')
    # Кабельная мачта с поворотными стрелами.
    tx, ty, tw, th = TITAN_TOWER
    lattice(bm, tx, ty, tw, tw, PAD_H - 0.1, PAD_H + th, 11, rl=0.14, rb=0.07)
    floors(bm, tx, ty, tw + 0.5, tw + 0.5, [PAD_H + 7 * k for k in range(1, 6)], WH)
    bx(bm, tx - 0.4, ty - 0.4, PAD_H + th, tx + 0.4, ty + 0.4, PAD_H + th + 5.0, RD)
    # Эректор откинут: лежит на площадке вдоль X, шарнир (тёмный) на востоке.
    placed(bm, lambda: girder_y(bm, 0.0, 30.0, 3.0, 1.6, 12, z0=PAD_H + 0.35, rc=0.11, rb=0.06, mat=WH),
           Matrix.Translation((-17.0, -9.0, 0.0)) @ Matrix.Rotation(-math.pi / 2, 4, 'Z'))
    for x in (-15.0, -5.0, 5.0, 12.0):
        bx(bm, x - 0.3, -10.2, PAD_H, x + 0.3, -7.8, PAD_H + 0.35, D)
    bx(bm, 11.6, -11.0, PAD_H, 14.2, -7.0, PAD_H + 2.4, D)
    bx(bm, 8.0, 8.0, PAD_H, 12.0, 12.0, PAD_H + 2.4, D)


# ------------------------------------------------------------------ «Сатурн V»: LC-39A (ML + LUT)
ML_X = (-33.0, 16.0)      # подвижная платформа 49 м по X (башня на западе, ракета ближе к востоку)
ML_Y = (-20.5, 20.5)      # 41 м по Y
ML_HOLE = 6.85            # проём 13,7 × 13,7 под F-1 (пара: PlanSaturn, бетон и котлован)
ML_BASE = 2.0             # низ ML над грунтом; верх — палуба PAD_H (пара: толщина бетона в PlanSaturn)


def holdown(bm):
    # Хват-стойка «hold-down» вдоль +X от кромки проёма: колонна + раскос к кромке юбки S-IC (r 5,05, низ на z=5 над палубой).
    bx(bm, ML_HOLE + 0.05, -0.8, PAD_H, ML_HOLE + 1.45, 0.8, PAD_H + 2.6, S)
    sq(bm, (ML_HOLE + 0.75, 0, PAD_H + 2.6), (5.35, 0, PAD_H + 4.9), 0.35, S)
    bx(bm, 4.85, -0.7, PAD_H + 4.3, 5.7, 0.7, PAD_H + 4.9, D)


def b_pad_saturn_ml(bm):
    h = ML_HOLE
    # (x0, x1, y0, y1): западный и восточный блоки на всю ширину; северный/южный — между ними.
    blocks = [(ML_X[0], -h, ML_Y[0], ML_Y[1]), (h, ML_X[1], ML_Y[0], ML_Y[1]), (-h, h, h, ML_Y[1]), (-h, h, ML_Y[0], -h)]
    for x0, x1, y0, y1 in blocks:
        bx(bm, x0, y0, ML_BASE, x1, y1, PAD_H - 0.8, D)
        bx(bm, x0, y0, PAD_H - 0.8, x1, y1, PAD_H, S)
    # Рёбра жёсткости по бортам (Steel поверх тёмного корпуса).
    for x in range(-30, 16, 6):
        bx(bm, x, ML_Y[0] - 0.15, ML_BASE, x + 0.6, ML_Y[0], PAD_H - 0.8, S)
        bx(bm, x, ML_Y[1], ML_BASE, x + 0.6, ML_Y[1] + 0.15, PAD_H - 0.8, S)
    for k in range(4):
        placed(bm, lambda: holdown(bm), Matrix.Rotation(math.radians(90 * k), 4, 'Z'))
    # Хвостовые мачты обслуживания у кромки проёма.
    bx(bm, 2.4, 8.0, PAD_H, 4.2, 9.8, PAD_H + 10.0, S)
    bx(bm, -4.2, -9.8, PAD_H, -2.4, -8.0, PAD_H + 10.0, S)
    bx(bm, 2.0, 7.9, PAD_H + 9.6, 4.6, 10.2, PAD_H + 10.4, D)
    bx(bm, -4.6, -10.2, PAD_H + 9.6, -2.0, -7.9, PAD_H + 10.4, D)
    # Отражатель: гребень поперёк котлована, струя уходит на север и юг.
    gable(bm, 0, 0, 13.4, 13.4, 0.0, 4.6, D, axis='x')


def b_pad_saturn_lut(bm):
    tx, ty, tw, th = SATURN_TOWER
    z0 = PAD_H - 0.1
    lattice(bm, tx, ty, tw, tw, z0, PAD_H + th, 20, rl=0.45, rb=0.2, mat=RD)
    bx(bm, tx - 1.4, ty - 1.4, z0, tx + 1.4, ty + 1.4, PAD_H + th - 2.0, S)           # шахта лифтов
    floors(bm, tx, ty, tw + 0.6, tw + 0.6, [PAD_H + 6.0 * k for k in range(1, 20)], WH, 0.3)
    top = PAD_H + th
    bx(bm, tx - 3.5, ty - 3.5, top, tx + 3.5, ty + 3.5, top + 4.0, S)                   # машинная надстройка
    sq(bm, (tx, ty, top + 4.0), (tx, ty, top + 12.0), 0.3, D)                           # молниеотвод
    bx(bm, tx - 9.0, ty - 0.6, top + 4.0, tx + 16.0, ty + 0.6, top + 5.4, RD)            # стрела крана
    bx(bm, tx - 11.0, ty - 1.2, top + 4.0, tx - 9.0, ty + 1.2, top + 6.0, D)             # противовес
    sq(bm, (tx + 14.0, ty, top + 4.4), (tx + 14.0, ty, top + 1.0), 0.08, D)             # трос крюка


# ------------------------------------------------------------------ Поворотные стрелы (шарнир — начало, длина вдоль +Y)
def b_arm_light(bm):
    L = ARM_LIGHT_LEN
    girder_y(bm, 0.0, L - 0.5, 0.9, 1.1, 7, z0=-0.55, rc=0.07, rb=0.04, mat=WH)
    bx(bm, -0.55, L - 0.5, -0.45, 0.55, L, 0.45, D)
    bx(bm, -0.3, -0.3, -0.7, 0.3, 0.3, 0.7, S)


def b_arm_heavy(bm):
    L = ARM_HEAVY_LEN
    girder_y(bm, 0.0, L - 0.7, 2.6, 3.0, 7, z0=-1.5, rc=0.13, rb=0.07, mat=WH)
    bx(bm, -1.2, 0.0, -1.6, 1.2, L - 0.7, -1.5, S)
    bx(bm, -1.4, L - 0.7, -1.5, 1.4, L, 1.5, RD)
    bx(bm, -0.5, -0.5, -1.7, 0.5, 0.5, 1.7, S)


PADS = [("Pad_R7", b_pad_r7), ("Pad_Mast", b_pad_mast), ("Pad_Proton", b_pad_proton), ("Pad_Redstone", b_pad_redstone),
        ("Pad_Atlas", b_pad_atlas), ("Pad_Titan", b_pad_titan), ("Pad_Saturn_ML", b_pad_saturn_ml),
        ("Pad_Saturn_LUT", b_pad_saturn_lut), ("Pad_Arm_Light", b_arm_light), ("Pad_Arm_Heavy", b_arm_heavy)]


def build_pads(export_fbx=False):
    out = []
    for name, fn in PADS:
        out.append(sbuild(name, fn))
        if export_fbx:
            export(name)
    return out
