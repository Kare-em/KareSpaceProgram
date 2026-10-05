# Корабли после «Аполлона» и станции (StationRockets.cs): блок И, шлюз «Волга», «Союз ТМ» (ПАО, СА, БО, обтекатель с САС),
# МКС 2000 и 2020, Falcon 9 (I и II ступени), Crew Dragon (багажник и капсула).
# Запуск без MCP-вывода (print виден только так, см. docs/pitfalls-tools.md):
#   "/c/Program Files/Blender Foundation/Blender 5.2/blender.exe" -b Tools/blender/parts.blend --python Tools/blender/station_parts.py < /dev/null
# Ось +Z (в Unity +Y — нос), начало — днище секции, метры, натуральная величина: L/Ø — из StationRockets.cs.
# Слоты 0..3 заняты у каждой модели все (иначе Unity схлопывает пустой и палитра VesselView.CraftPalette съезжает);
# что значит слот у конкретной модели — в комментарии к её функции и в CraftPalette.
import math, os, sys
import bpy, bmesh
from mathutils import Vector, Matrix

TOOLS = r"C:\CocosGames\KareSpaceProgram\Tools\blender"
if "lathe" not in globals():
    exec(open(os.path.join(TOOLS, "hulls_lib.py"), encoding="utf-8").read())


def polar(r, az):
    a = math.radians(az)
    return (r * math.cos(a), r * math.sin(a))


def transformed(bm, fn, m):
    """Строит деталь fn(bm) у начала координат вдоль +Z и переносит матрицей m (поворот оси, сдвиг)."""
    before = set(bm.verts)
    fn()
    new = [v for v in bm.verts if v not in before]
    bmesh.ops.transform(bm, matrix=m, verts=new)


# Поворот оси вращения +Z на нужную: модули МКС вбок, крылья, стыковочные узлы
TO_X = Matrix.Rotation(math.radians(90), 4, 'Y')      # +Z -> +X
TO_NX = Matrix.Rotation(math.radians(-90), 4, 'Y')    # +Z -> -X
TO_Y = Matrix.Rotation(math.radians(-90), 4, 'X')     # +Z -> +Y
TO_NY = Matrix.Rotation(math.radians(90), 4, 'X')     # +Z -> -Y


def cyl_side(bm, prof, at, rot, mat=H, segs=24):
    """Тело вращения (профиль [(r, z)]) с осью по rot, основание в точке at."""
    transformed(bm, lambda: lathe(bm, prof, segs, mat), Matrix.Translation(at) @ rot)


def bell_tilt(bm, az, pivot_r, pivot_z, h, re, tilt_deg, mat=N, up=False):
    """Скошенное сопло (как у САС «Аполлона»): камера в pivot, срез наружу на tilt°; up — срезом вверх."""
    def mk():
        bell(bm, (0.0, 0.0), 0.0, h, re, mat=mat, segs=12)
    m = (Matrix.Rotation(math.radians(az), 4, 'Z') @ Matrix.Translation((pivot_r, 0.0, pivot_z)) @
         Matrix.Rotation(-math.radians(tilt_deg), 4, 'Y') @ Matrix.Translation((0.0, 0.0, -h)))
    if up:
        m = (Matrix.Rotation(math.radians(az), 4, 'Z') @ Matrix.Translation((pivot_r, 0.0, pivot_z)) @
             Matrix.Rotation(math.radians(180 + tilt_deg), 4, 'Y') @ Matrix.Translation((0.0, 0.0, -h)))
    transformed(bm, mk, m)


def arc_lathe(bm, prof, a0=90.0, a1=270.0, segs=48, mat=H, mif0=None):
    """Замкнутый профиль (r, z) против часовой, развёрнутый на дугу a0..a1 + плоские торцы по разрезу
    (копия apollo_fairings.arc_lathe: тот файл при exec сразу экспортирует свои модели)."""
    angs = [math.radians(a0 + (a1 - a0) * j / segs) for j in range(segs + 1)]
    rings = [[bm.verts.new((r * math.cos(a), r * math.sin(a), z)) for a in angs] for r, z in prof]
    n = len(prof)
    for k in range(n):
        A, Bv = rings[k], rings[(k + 1) % n]
        zm = 0.5 * (prof[k][1] + prof[(k + 1) % n][1])
        for j in range(segs):
            f = bm.faces.new((A[j], A[j + 1], Bv[j + 1], Bv[j]))
            f.material_index = mif0(a0 + (a1 - a0) * (j + 0.5) / segs, zm) if (mif0 and k == 0) else mat
            f.smooth = True
    for j, sgn in ((0, -1.0), (segs, 1.0)):
        a = angs[j]
        want = Vector((-math.sin(a), math.cos(a), 0.0)) * sgn
        f = bm.faces.new([rings[k][j] for k in range(n)])
        f.normal_update()
        if f.normal.dot(want) < 0:
            f.normal_flip()
        f.material_index = mat
        f.smooth = False
        # Торец невыпуклый (конус + цилиндр + башня) — режем здесь, Unity триангулирует n-угольник наивно.
        bmesh.ops.triangulate(bm, faces=[f], quad_method='BEAUTY', ngon_method='BEAUTY')
    for k in range(n):
        A, Bv = rings[k], rings[(k + 1) % n]
        for j in range(segs):
            e = bm.edges.get((A[j], A[j + 1]))
            if e:
                e.smooth = False
        for j in (0, segs):
            e = bm.edges.get((A[j], Bv[j]))
            if e:
                e.smooth = False


def panel(bm, center, size, mat):
    box(bm, center, size, mat)


# ================================================================== Р-7: блок И (L 6,7, Ø2,66)
BI_R, BI_L, BI_TRUSS = 1.33, 6.7, 1.35   # ферма горячего разделения внизу блока; низ фермы — на верхе блока А (r 1,475)


def b_block_i(bm):
    """0 корпус (серый), 1 тёмные пояса/днище, 2 ферма и рама, 3 сопла РД-0110."""
    truss(bm, 1.42, 0.06, BI_R - 0.02, BI_TRUSS, 10, 0.045, M)
    # РД-0110: четыре камеры + четыре рулевых, ТНА над ними
    for k in range(4):
        bell(bm, polar(0.42, 45 + 90 * k), 0.0, 1.05, 0.24, segs=16)
        bell(bm, polar(0.98, 90 * k), 0.35, 0.5, 0.09, segs=12)
    box(bm, (0, 0, 1.1), (0.55, 0.55, 0.5), M)
    # днище бака (купол вниз) и корпус
    lathe(bm, [(0.0, 1.05), (BI_R - 0.02, BI_TRUSS + 0.02), (BI_R, BI_TRUSS)], 48, B)
    lathe(bm, [(BI_R, BI_TRUSS), (BI_R, BI_TRUSS + 0.3), (BI_R, 6.45), (BI_R - 0.03, BI_L), (0.0, BI_L)], 48,
          mif=lambda az, z: B if z < BI_TRUSS + 0.3 else (M if z > 6.45 else H))
    for z in (BI_TRUSS + 0.3, 3.9, 6.45):
        ring(bm, BI_R + 0.01, z, 0.03, 0.08, M)
    for az in (0, 180):  # кабельные короба вдоль бака
        x, y = polar(BI_R + 0.05, az)
        box(bm, (x, y, 3.95), (0.1, 0.18, 4.9), H, az=az)


# ================================================================== «Восход-2»: шлюз «Волга» (L 2,5, Ø1,2)
def b_volga(bm):
    """0 ткань (белая), 1 иллюминаторы, 2 металл колец и люка, 3 баллоны наддува."""
    R = 0.6
    lathe(bm, [(0.0, 0.0), (0.5, 0.0), (0.52, 0.05), (0.52, 0.22)], 32, M)          # нижнее кольцо у люка СА
    # надувная оболочка с рёбрами-баллонами: 36 штук в реальности, здесь 18 выпуклостей
    prof = [(0.52, 0.22)]
    for i in range(1, 12):
        z = 0.22 + (2.2 - 0.22) * i / 12
        prof.append((R - 0.03 + 0.015 * math.sin(math.pi * i), z))
    prof.append((0.52, 2.2))
    lathe(bm, prof, 36, H)
    for k in range(18):
        x, y = polar(R - 0.02, 10 + 20 * k)
        box(bm, (x, y, 1.21), (0.06, 0.05, 1.9), H, az=10 + 20 * k)
    lathe(bm, [(0.52, 2.2), (0.55, 2.25), (0.55, 2.38), (0.4, 2.45), (0.3, 2.5), (0.0, 2.5)], 32, M)  # верхний люк
    for az in (0, 120, 240):
        x, y = polar(0.6, az)
        box(bm, (x, y, 1.6), (0.04, 0.16, 0.16), B, az=az)
    for az in (60, 180, 300):  # баллоны наддува у основания
        sphere(bm, 0.14, 0.35, N, segs=12, k=8, c=polar(0.7, az))


# ================================================================== «Союз ТМ»: ПАО (L 2,26, Ø2,72)
PAO_R, PAO_L = 1.36, 2.26
SA_R = 1.085   # Ø2,17 СА; пара: верх ПАО = низ СА


def b_soyuz_pao(bm):
    """0 ЭВТИ зелёная, 1 солнечные батареи, 2 металл (радиатор, рамы), 3 сопло СКД."""
    bell(bm, (0, 0), 0.0, 0.62, 0.2, segs=20)
    lathe(bm, [(0.0, 0.5), (PAO_R - 0.04, 0.32), (PAO_R - 0.04, 0.0), (PAO_R, 0.0)], 48, B)  # кормовое днище
    lathe(bm, [(PAO_R, 0.0), (PAO_R, 1.05), (1.08, 1.3)], 48, M)                             # агрегатный отсек-радиатор
    lathe(bm, [(1.08, 1.3), (1.08, 1.85), (SA_R - 0.04, 1.95), (SA_R - 0.04, PAO_L - 0.04), (SA_R - 0.08, PAO_L), (0.0, PAO_L)],
          48, mif=lambda az, z: M if z > 1.9 else H)
    for z in (0.5, 1.05):
        ring(bm, PAO_R + 0.01, z, 0.03, 0.06, M)
    for az in (45, 135, 225, 315):  # блоки ДПО на юбке
        x, y = polar(PAO_R + 0.05, az)
        box(bm, (x, y, 0.2), (0.12, 0.2, 0.2), N, az=az)
    # две панели СБ по ±X (в Unity — по ±X тоже: ось вращения не трогается), 4 створки по 0,95 × 1,25
    for side in (1, -1):
        strut(bm, (side * PAO_R, 0, 0.7), (side * 1.55, 0, 0.7), 0.04, M)
        for k in range(4):
            u = 1.6 + 1.0 * k + 0.475
            box(bm, (side * u, 0, 0.7), (0.95, 0.03, 1.25), B)
        strut(bm, (side * 1.55, 0, 0.7), (side * 5.6, 0, 0.7), 0.02, M)
    strut(bm, (0, PAO_R, 1.6), (0, PAO_R + 0.8, 1.9), 0.015, M)   # антенна


# ================================================================== «Союз ТМ»: СА «фара» (L 2,24, Ø2,17)
def b_soyuz_sa(bm):
    """0 ЭВТИ, 1 иллюминаторы и перископ, 2 металл люка и шпангоутов, 3 лобовой теплозащитный экран."""
    lathe(bm, [(0.0, 0.0), (0.6, 0.04), (0.95, 0.14), (SA_R, 0.3)], 48, N)
    lathe(bm, [(SA_R, 0.3), (SA_R - 0.01, 0.55), (1.0, 1.0), (0.86, 1.45), (0.66, 1.8), (0.45, 2.06), (0.42, 2.14)], 48, H, sharp=60)
    lathe(bm, [(0.42, 2.14), (0.42, 2.2), (0.36, 2.24), (0.0, 2.24)], 32, M)
    ring(bm, SA_R + 0.005, 0.32, 0.03, 0.06, M)
    for az in (60, 300):
        r = 0.95
        x, y = polar(r, az)
        box(bm, (x, y, 1.15), (0.08, 0.2, 0.2), B, az=az)
    x, y = polar(0.93, 0)
    box(bm, (x, y, 1.25), (0.14, 0.12, 0.3), B)                     # перископ ВСК
    x, y = polar(0.9, 180)
    box(bm, (x, y, 1.3), (0.05, 0.6, 0.55), M, az=180)              # крышка парашютного контейнера


# ================================================================== «Союз ТМ»: БО (L 2,98, Ø2,26) со «штырём»
BO_R, BO_L = 1.13, 2.98


def b_soyuz_bo(bm):
    """0 ЭВТИ, 1 иллюминатор, 2 металл стыковочного агрегата, 3 тарелки и антенны «Курса»."""
    lathe(bm, [(0.0, 0.0), (0.42, 0.0), (0.46, 0.08), (0.46, 0.38)], 32, M)
    zc = 1.42
    prof = [(BO_R * math.cos(math.radians(t)), zc + BO_R * math.sin(math.radians(t))) for t in range(-66, 68, 8)]
    lathe(bm, [(0.46, 0.38)] + prof, 40, H)
    rt, zt = prof[-1]
    lathe(bm, [(rt, zt), (0.55, zt + 0.06), (0.55, 2.62), (0.3, 2.66)], 32, M)        # стыковочное кольцо
    lathe(bm, [(0.3, 2.66), (0.12, 2.78), (0.04, BO_L - 0.03), (0.0, BO_L)], 16, M)    # штырь
    x, y = polar(BO_R - 0.02, 90)
    box(bm, (x, y, 1.6), (0.06, 0.22, 0.22), B, az=90)
    for az in (30, 150, 270):  # антенны «Курса»
        p = (*polar(0.85, az), 2.0)
        q = (*polar(1.55, az), 2.5)
        strut(bm, p, q, 0.015, M)
        lathe(bm, [(0.0, 0.0), (0.18, 0.06)], 12, N, c=polar(1.6, az))


def b_soyuz_bo_fix(bm):
    b_soyuz_bo(bm)
    # тарелки «Курса» (слот N, у z 0..0,06) поднять к концам антенн
    for v in bm.verts:
        if v.co.z < 0.07 and math.hypot(v.co.x, v.co.y) > 1.3:
            v.co.z += 2.5


# ================================================================== «Союз»: головной обтекатель с САС, половина −X (L 15, Ø3,0)
SH_R, SH_L, SH_W = 1.5, 15.0, 0.03


def b_soyuz_shroud_half(bm):
    """0 обшивка (белая), 1 швы разъёма и окно, 2 башня САС и решётчатые стабилизаторы, 3 сопла РДТТ."""
    outer = [(1.36, 0.0), (SH_R, 0.5), (SH_R, 7.8), (1.05, 9.6), (0.62, 10.5), (0.42, 10.7)]
    inner = [(r - SH_W, z) for r, z in reversed(outer)]
    seam = lambda az, zm: B if (abs(az - 91.9) < 2 or abs(az - 268.1) < 2) else H
    arc_lathe(bm, outer + inner, mat=H, mif0=seam)
    # башня САС: переходник, РДТТ, блок сопел, головной РДТТ с носком (тоже половина)
    arc_lathe(bm, [(0.02, 10.6), (0.44, 10.6), (0.3, 11.3), (0.3, 13.1), (0.38, 13.2), (0.38, 13.55), (0.3, 13.65),
                   (0.3, 14.3), (0.12, 14.85), (0.02, SH_L)], mat=M, segs=16)
    for az in (135, 225):
        bell_tilt(bm, az, 0.32, 13.4, 0.5, 0.11, 30, mat=N)            # основные сопла РДТТ САС, наружу-вниз
        x, y = polar(SH_R + 0.04, az)
        box(bm, (x, y, 6.3), (0.08, 0.9, 1.6), M, az=az)              # решётчатые стабилизаторы (сложены)
    x, y = polar(SH_R + 0.005, 180)
    box(bm, (x, y, 5.0), (0.02, 0.35, 0.35), B, az=180)               # окно напротив иллюминатора СА


# ================================================================== МКС: общие модули
def node_ball(bm, z, r=1.1, mat=H):
    sphere(bm, r, z, mat, segs=24, k=12)


def wing(bm, y0, y1, z, width, mat, x=0.0, gap=0.0, n=1):
    """Крыло СБ вдоль ±Y: n створок между y0 и y1 (знак задаёт сторону), плоскость Y-Z (смотрит по X)."""
    s = 1 if y1 > y0 else -1
    L = abs(y1 - y0)
    seg = (L - gap * (n - 1)) / n
    for k in range(n):
        c = y0 + s * (seg * 0.5 + k * (seg + gap))
        box(bm, (x, c, z), (0.04, seg, width), mat)
    strut(bm, (x, y0, z), (x, y1, z), 0.04, M)


def progress(bm, z0, mat=H):
    """«Прогресс М1» стыковочным узлом вниз на z0: ГО-«шар», отсек дозаправки, ПАО, ~7,2 м."""
    lathe(bm, [(0.0, z0), (0.3, z0 + 0.25), (0.55, z0 + 0.45)], 16, M)
    zc = z0 + 1.6
    lathe(bm, [(0.55, z0 + 0.45)] + [(1.1 * math.cos(math.radians(t)), zc + 1.1 * math.sin(math.radians(t)))
                                     for t in range(-60, 61, 12)] + [(0.95, z0 + 3.2)], 24, mat)
    lathe(bm, [(0.95, z0 + 3.2), (1.0, z0 + 4.4), (1.36, z0 + 4.6), (1.36, z0 + 7.0), (0.0, z0 + 7.2)], 24, mat)
    wing(bm, 1.36, 5.3, z0 + 6.0, 1.2, B, gap=0.05, n=4)
    wing(bm, -1.36, -5.3, z0 + 6.0, 1.2, B, gap=0.05, n=4)


def zvezda_up(bm, z0, r=2.075):
    """СМ «Звезда» кормой вниз (кормовой узел на z0): ПрК, большой цилиндр, конус, шар ПХО — 13,1 м."""
    lathe(bm, [(0.0, z0), (0.3, z0 + 0.1), (0.5, z0 + 0.3), (0.95, z0 + 0.45), (0.95, z0 + 1.2), (r, z0 + 1.5),
               (r, z0 + 7.3), (1.45, z0 + 8.6), (1.45, z0 + 11.0), (0.9, z0 + 11.4)], 32, H)
    node_ball(bm, z0 + 12.0)
    for z in (z0 + 1.5, z0 + 7.3):
        ring(bm, r + 0.01, z, 0.04, 0.1, M, segs=32)
    for s in (1, -1):  # две панели СБ по 14,9 м размаха
        wing(bm, s * r, s * 14.9, z0 + 5.5, 3.3, B, gap=0.1, n=6)


def zarya_up(bm, z0, r=2.05):
    """ФГБ «Заря» большим концом вниз (стык с «Звездой» на z0), шар со стыковочными узлами вверху — 12,6 м."""
    lathe(bm, [(0.0, z0), (0.9, z0), (r, z0 + 0.2), (r, z0 + 6.0), (1.45, z0 + 6.8), (1.45, z0 + 11.2), (0.9, z0 + 11.5)], 32, H)
    node_ball(bm, z0 + 11.8)
    ring(bm, r + 0.01, z0 + 3.0, 0.04, 0.1, M, segs=32)


def zarya_down(bm, z0, r=2.05):
    """«Заря» шаром вниз (стык с PMA-1 на z0), большой конец вверх — к «Звезде»."""
    node_ball(bm, z0 + 0.8)
    lathe(bm, [(0.9, z0 + 1.1), (1.45, z0 + 1.4), (1.45, z0 + 5.8), (r, z0 + 6.6), (r, z0 + 12.4), (0.9, z0 + 12.6), (0.0, z0 + 12.6)], 32, H)
    ring(bm, r + 0.01, z0 + 9.6, 0.04, 0.1, M, segs=32)


def pma(bm, z0, h=1.9, r0=0.8, r1=1.0):
    lathe(bm, [(0.0, z0), (r0, z0), (r0, z0 + 0.15), (r1, z0 + h), (0.0, z0 + h)], 24, H)
    ring(bm, r0 + 0.02, z0 + 0.05, 0.06, 0.1, M, segs=24)


def node(bm, z0, L, r):
    lathe(bm, [(0.0, z0), (r - 0.15, z0), (r, z0 + 0.3), (r, z0 + L - 0.3), (r - 0.15, z0 + L), (0.0, z0 + L)], 32, H)


# ================================================================== МКС ноября 2000 (L 43, Ø4,15)
def b_iss2000(bm):
    """0 модули (белые), 1 СБ российского сегмента, 2 фермы и кольца, 3 антенны/иллюминаторы.
    Снизу вверх: кормовой узел «Звезды» (к нему — «Союз ТМ-31»), «Звезда», «Заря», PMA-1, «Юнити» с Z1, PMA-2,
    «Прогресс М1-3» сверху — так ядро задаёт ~43 м по оси (StationRockets.SoyuzTM31)."""
    zvezda_up(bm, 0.0)                       # 0 .. 12,6 (+ шар до 13,1)
    zarya_up(bm, 13.1)                       # 13,1 .. 25,7
    for s in (1, -1):
        wing(bm, s * 2.05, s * 12.2, 15.5, 3.35, B, gap=0.1, n=4)
    pma(bm, 25.7)                            # 25,7 .. 27,6
    node(bm, 27.6, 5.5, 2.07)                # «Юнити» 27,6 .. 33,1
    # Z1 на зенитном (+X) порту «Юнити»: ферма-короб, антенна Ku, гиродины
    box(bm, (2.07 + 1.3, 0.0, 30.35), (2.6, 3.0, 4.2), M)
    strut(bm, (4.6, 0.0, 31.5), (6.2, 0.0, 32.6), 0.06, M)
    lathe(bm, [(0.0, 0.0), (0.9, 0.25)], 16, N, c=(6.3, 0.0))
    pma(bm, 33.1, r0=1.0, r1=0.8)            # PMA-2 33,1 .. 35,0
    progress(bm, 35.6)                       # 35,6 .. 42,8
    lathe(bm, [(0.0, 35.0), (0.55, 35.0), (0.55, 35.6)], 16, M)
    for az in (90, 270):
        x, y = polar(2.08, az)
        box(bm, (x, y, 4.0), (0.05, 0.25, 0.25), N, az=az)


def b_iss2000_fix(bm):
    b_iss2000(bm)
    for v in bm.verts:   # тарелка Ku (lathe у z 0..0,25 на x 6,3) — к концу штанги Z1
        if v.co.z < 0.3 and v.co.x > 5.0:
            v.co.z += 32.6


# ================================================================== МКС 2020 (L 51, Ø4,4)
def b_iss2020(bm):
    """0 модули и радиаторы (белые), 1 СБ российского сегмента, 2 ферма ITS и кольца, 3 СБ американского (медные).
    Снизу вверх: IDA-2 на PMA-2 (к нему — Crew Dragon), «Гармония» с «Кибо» (−Y) и «Коламбусом» (+Y), «Дестини»
    с фермой ITS на зените (+X), «Юнити» с «Транквилити» (−X), PMA-1, «Заря», «Звезда» с «Пирсом» и «Поиском»."""
    R = 2.2
    lathe(bm, [(0.0, 0.0), (0.62, 0.0), (0.66, 0.08), (0.66, 0.35), (0.8, 0.4)], 24, M)   # IDA-2
    pma(bm, 0.4, h=1.5)                     # PMA-2 0,4 .. 1,9
    node(bm, 1.9, 7.2, R)                    # «Гармония» 1,9 .. 9,1
    cyl_side(bm, [(R, 0.0), (R, 13.4), (0.0, 13.6)], (0.0, 0.0, 5.5), TO_NY)   # «Кибо»
    box(bm, (0.0, -R - 4.0, 5.5 + R + 1.0), (3.2, 4.0, 2.0), H)              # ELM-PS на «Кибо»
    cyl_side(bm, [(R, 0.0), (R, 8.2), (0.0, 9.1)], (0.0, 0.0, 5.5), TO_Y)       # «Коламбус»
    node(bm, 9.1, 8.5, R)                    # «Дестини» 9,1 .. 17,6
    node(bm, 17.6, 5.5, R)                   # «Юнити» 17,6 .. 23,1
    cyl_side(bm, [(R, 0.0), (R, 8.9), (0.0, 8.9)], (0.0, 0.0, 20.3), TO_NX)    # «Транквилити»
    cyl_side(bm, [(1.0, 0.0), (1.0, 1.0), (0.4, 1.5), (0.0, 1.5)], (-6.0, R - 0.3, 20.3), TO_Y)  # «Купол»
    pma(bm, 23.1)                            # PMA-1 23,1 .. 25,0
    zarya_down(bm, 25.0)                     # 25,0 .. 37,6
    # «Звезда» шаром вниз: шар ПХО, конус, большой цилиндр, ПрК, кормовой узел у 51 м
    node_ball(bm, 38.6)
    lathe(bm, [(0.9, 39.5), (1.45, 39.9), (1.45, 42.4), (2.075, 43.7), (2.075, 49.5), (0.95, 49.8),
               (0.95, 50.55), (0.5, 50.7), (0.3, 50.9), (0.0, 51.0)], 32, H)
    for s in (1, -1):
        wing(bm, s * 2.075, s * 14.9, 46.0, 3.3, B, gap=0.1, n=6)
    cyl_side(bm, [(1.1, 0.0), (1.1, 3.6), (0.5, 4.0), (0.0, 4.0)], (1.1, 0.0, 38.6), TO_X)    # «Поиск»
    cyl_side(bm, [(1.1, 0.0), (1.1, 3.6), (0.5, 4.0), (0.0, 4.0)], (-1.1, 0.0, 38.6), TO_NX)  # «Пирс»
    # ферма ITS на зените «Дестини»: 109 м вдоль Y, сверху на стойке
    X_T, Z_T = R + 1.6, 13.4
    box(bm, (R + 0.7, 0.0, Z_T), (1.4, 1.2, 1.2), M)
    box(bm, (X_T + 0.4, 0.0, Z_T), (2.0, 109.0, 2.0), M)
    # радиаторы S1/P1 — к надиру (−X) и вбок по ферме
    for s in (1, -1):
        box(bm, (X_T + 0.4 - 7.0, s * 12.0, Z_T - 3.0), (12.0, 3.2, 0.05), H)
    # 4 пары крыльев SAW (P4, P6, S4, S6): каждое крыло — 2 полотна 4,6 × 34 м по обе стороны мачты, вдоль ±X
    for y in (-50.0, -37.0, 37.0, 50.0):
        for sx in (1, -1):
            x0 = X_T + 0.4 + sx * 1.5
            for dy in (-2.9, 2.9):
                box(bm, (x0 + sx * 17.0, y + dy, Z_T), (34.0, 4.6, 0.05), N)
            strut(bm, (x0, y, Z_T), (x0 + sx * 34.0, y, Z_T), 0.12, M)
        box(bm, (X_T + 0.4, y, Z_T), (3.0, 2.5, 2.5), M)  # шарнир «бета»


# ================================================================== Falcon 9: I ступень (L 41,2, Ø3,66)
F9_R = 1.83
# Опоры и рули в корпусе (старый вид). False — их рисует VesselView.SpaceX на шарнирах. Пара: константы F9* в VesselView.SpaceX.cs.
F9_BAKED_DEPLOY = False


def b_f9_s1(bm):
    """0 белый бак, 1 межступенчатый отсек, теплозащита «октавеба», 2 рама, 3 Merlin 1D. Опоры и решётчатые рули — не
    здесь: их строит и раскрывает VesselView.SpaceX (шарниры, §6.12), запечённые сложенными они торчали бы сквозь раскрытые."""
    bell(bm, (0, 0), 0.0, 1.25, 0.42, segs=16)
    for k in range(8):
        bell(bm, polar(1.18, 22.5 + 45 * k), 0.0, 1.25, 0.42, segs=16)
    lathe(bm, [(0.0, 1.1), (F9_R - 0.05, 1.0), (F9_R, 0.9)], 48, B)
    IS0, L = 34.5, 41.2
    lathe(bm, [(F9_R, 0.9), (F9_R, IS0), (F9_R, L), (F9_R - 0.03, L), (F9_R - 0.03, 35.2), (0.0, 35.6)], 48,
          mif=lambda az, z: B if z > IS0 else H)
    for k in range(4 if F9_BAKED_DEPLOY else 0):  # сложенные опоры, углепластик
        az = 45 + 90 * k
        x, y = polar(F9_R + 0.08, az)
        box(bm, (x, y, 5.6), (0.16, 0.7, 9.4), B, az=az)
        x, y = polar(F9_R + 0.12, az)
        box(bm, (x, y, 1.0), (0.2, 0.8, 0.3), M, az=az)      # стопа
    for k in range(4 if F9_BAKED_DEPLOY else 0):  # решётчатые рули — сложены вдоль межступенчатого
        az = 90 * k
        x, y = polar(F9_R + 0.08, az)
        box(bm, (x, y, 39.9), (0.12, 1.2, 1.5), M, az=az)
    x, y = polar(F9_R + 0.06, 135)
    box(bm, (x, y, 17.5), (0.12, 0.35, 33.0), H, az=135)      # кабельный короб
    for az in (90, 270):  # блоки газовых двигателей ориентации
        x, y = polar(F9_R + 0.05, az)
        box(bm, (x, y, 40.6), (0.1, 0.5, 0.4), B, az=az)


# ================================================================== Falcon 9: II ступень (L 13,8, Ø3,66)
def b_f9_s2(bm):
    """0 белый бак, 1 чёрная кромка, 2 днище и адаптер, 3 насадок MVac. Насадок (выход Ø3,2) свисает на 3,6 м ниже
    z = 0 в открытый межступенчатый отсек I ступени (внутр. r 1,80, глубина 6,0) — VesselView: ownBottom."""
    bell(bm, (0, 0), -3.6, 4.4, 1.6, segs=40)
    lathe(bm, [(0.0, 1.0), (F9_R - 0.03, 0.25), (F9_R - 0.03, 0.0), (F9_R, 0.0)], 48, M)
    lathe(bm, [(F9_R, 0.0), (F9_R, 0.15), (F9_R, 13.3), (1.65, 13.8), (0.0, 13.8)], 48,
          mif=lambda az, z: B if z < 0.15 else (M if z > 13.3 else H))
    for az in (0, 180):
        x, y = polar(F9_R + 0.04, az)
        box(bm, (x, y, 7.0), (0.1, 0.25, 12.0), H, az=az)


# ================================================================== Crew Dragon: багажник (L 3,7, Ø3,7)
DR_TR = 1.85


def b_dragon_trunk(bm):
    """0 белая обшивка, 1 солнечные элементы (половина окружности), 2 радиаторы и торцы, 3 чёрные кромки."""
    lathe(bm, [(0.0, 0.05), (DR_TR - 0.03, 0.05), (DR_TR - 0.03, 0.0), (DR_TR, 0.0)], 48, B)
    lathe(bm, [(DR_TR, 0.0), (DR_TR, 0.15), (DR_TR, 3.55), (DR_TR, 3.7), (0.0, 3.7)], 48,
          mif=lambda az, z: N if (z < 0.15 or z > 3.55) else (B if 90 < az < 270 else (M if 300 < az or az < 60 else H)))
    for k in range(4):  # стабилизаторы у низа
        fin(bm, 45 + 90 * k, [(DR_TR - 0.02, 0.1), (DR_TR + 0.5, 0.1), (DR_TR + 0.5, 0.5), (DR_TR - 0.02, 1.5)], 0.08, H)


# ================================================================== Crew Dragon (L 4,4, Ø4,0)
def b_crew_dragon(bm):
    """0 белая обшивка, 1 иллюминаторы и ниши SuperDraco, 2 шарнир носка, 3 лобовой экран PICA-X."""
    lathe(bm, [(0.0, 0.0), (1.2, 0.05), (1.85, 0.16), (2.0, 0.3)], 48, N)
    side = [(2.0, 0.3), (1.97, 0.55), (1.32, 3.0)]
    lathe(bm, side, 48, H, sharp=20)
    # Тёмный шов и палуба со стыковочным узлом под носком (§6.12: носок откидывается перед стыковкой).
    lathe(bm, [(1.32, 3.0), (1.30, 3.06), (0.78, 3.06), (0.74, 3.28), (0.0, 3.28)], 48,
          mif=lambda az, z: B if z < 3.07 else M)
    # Носок отдельным островом (низ 3,08 выше палубы 3,06 — не сливается): split_station.py уносит его в Crew_Dragon_Nose.
    lathe(bm, [(0.0, 3.08), (1.27, 3.08), (1.29, 3.1), (1.22, 3.5), (0.98, 3.95), (0.55, 4.3), (0.0, 4.4)], 48, H)
    tilt = math.degrees(math.atan2(2.0 - 1.32, 3.0 - 0.3))  # ≈14° — уклон боковой стенки
    for k in range(4):  # спаренные SuperDraco
        az = 45 + 90 * k
        z = 2.0
        r = 1.97 + (1.32 - 1.97) * (z - 0.55) / (3.0 - 0.55)
        transformed(bm, lambda: box(bm, (0, 0, 0), (0.08, 0.7, 0.8), B),
                    Matrix.Rotation(math.radians(az), 4, 'Z') @ Matrix.Translation((r, 0, z)) @
                    Matrix.Rotation(math.radians(-tilt), 4, 'Y'))
    for az in (-25, 0, 25, 180):  # иллюминаторы
        z = 2.35
        r = 1.97 + (1.32 - 1.97) * (z - 0.55) / (3.0 - 0.55)
        transformed(bm, lambda: box(bm, (0, 0, 0), (0.04, 0.3, 0.3), B),
                    Matrix.Rotation(math.radians(az), 4, 'Z') @ Matrix.Translation((r, 0, z)) @
                    Matrix.Rotation(math.radians(-tilt), 4, 'Y'))
    x, y = polar(1.3, 0)
    box(bm, (x, y, 3.1), (0.12, 0.4, 0.15), M)              # шарнир носового обтекателя


MODELS = [
    ("R7_BlockI", b_block_i), ("Voskhod_Airlock", b_volga),
    ("Soyuz_PAO", b_soyuz_pao), ("Soyuz_SA", b_soyuz_sa), ("Soyuz_BO", b_soyuz_bo_fix), ("Soyuz_Shroud_Half", b_soyuz_shroud_half),
    ("ISS_2000", b_iss2000_fix), ("ISS_2020", b_iss2020),
    ("Falcon9_S1", b_f9_s1), ("Falcon9_S2", b_f9_s2), ("Dragon_Trunk", b_dragon_trunk), ("Crew_Dragon", b_crew_dragon),
]


def main(do_export=True, only=None):
    out = []
    for name, fn in MODELS:
        if only and name not in only:
            continue
        out.append(build(name, fn))
        if do_export:
            export(name)
    return out


if __name__ == "__main__" or "STATION_RUN" in os.environ:
    for r in main():
        print("STATION", r)
    bpy.context.preferences.filepaths.save_version = 0
    bpy.ops.wm.save_mainfile()
