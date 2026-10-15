# «Титан IVB — Центавр» с «Кассини–Гюйгенсом» (PlanetaryRockets.TitanCentaurCassini, GDD §5.3): ядро I ступени
# «Титана IVB», РДТТ SRMU, орбитер «Кассини» и три секции «Гюйгенса» (лобовой экран, задняя крышка, спускаемый модуль).
# II ступень и «Центавр» — готовые Titan_Stage2 / Centaur.
# Запуск (parts.blend не сохраняется — модели строятся заново из кода):
#   "/c/Program Files/Blender Foundation/Blender 5.2/blender.exe" -b Tools/blender/parts.blend --python Tools/blender/cassini_parts.py < /dev/null
# Ось +Z (в Unity +Y — нос), начало — днище секции, метры, натуральная величина: L/Ø — из PlanetaryRockets.cs.
# Оси FBX → Unity: (x, y, z) → (−x, z, −y). SRMU стоят на RadialPhase = π/2 → узлы крепления на ядре — на азимутах
# 90°/270° Blender; у РДТТ сторона к ядру — +X Blender (как у Titan_SRB в voyager_parts.py).
# Слоты (hulls_lib.SLOTS: 0/1/2/3) — смысл у каждой модели в docstring, палитра — VesselView.CraftPalette.
# Все тела замкнуты (нормали наружу): сквозь открытые торцы тонкие детали «просвечивали» (voyager_parts.py).
import math, os
import bpy, bmesh

TOOLS = r"C:\CocosGames\KareSpaceProgram\Tools\blender"
exec(open(os.path.join(TOOLS, "hulls_lib.py"), encoding="utf-8").read())
# Из hulls_models.py нужны только помощники (paint, polar).
exec(open(os.path.join(TOOLS, "hulls_models.py"), encoding="utf-8").read())


def placed(bm, mtx, make):
    """Строит деталь у начала координат (make()) и переносит только её новые вершины матрицей mtx."""
    before = set(bm.verts)
    make()
    bmesh.ops.transform(bm, matrix=mtx, verts=[v for v in bm.verts if v not in before])


def rod(bm, p, q, rad, mat=M, segs=6):
    """Закрытый стержень: боковины и обе крышки (strut из hulls_lib открыт с торцов)."""
    p, q = Vector(p), Vector(q)
    d = q - p
    if d.length < 1e-6:
        return
    z = d.normalized()
    x = z.orthogonal().normalized()
    y = z.cross(x)
    a = [bm.verts.new(p + (x * math.cos(2 * math.pi * j / segs) + y * math.sin(2 * math.pi * j / segs)) * rad)
         for j in range(segs)]
    b = [bm.verts.new(v.co + d) for v in a]
    fs = [bm.faces.new((a[j], a[(j + 1) % segs], b[(j + 1) % segs], b[j])) for j in range(segs)]
    fs.append(bm.faces.new(list(reversed(a))))
    fs.append(bm.faces.new(b))
    for f in fs:
        f.material_index = mat
        f.smooth = segs > 4


# ================================================================== ядро «Титана IVB» (L 26,4, Ø 3,05)
RC = 1.525                       # радиус ядра; пара: PlanetaryRockets.Titan4SrmuOffset = 1,525 + 1,6 + 0,3
RS = 1.6                         # радиус SRMU (Ø 3,2)
SRMU_OFF = RC + RS + 0.3         # ось SRMU от оси ядра — Titan4SrmuOffset
CORE_L = 26.4


def b_titan4_core(bm):
    """0 корпус (металл), 1 юбки и днища, 2 узлы крепления SRMU и короб кабелей, 3 сопла LR87-AJ-11.
    Как Titan3_Core, но на 4,2 м длиннее (бак Titan IV) и с узлами под SRMU Ø 3,2 на ±Y Blender."""
    for s in (1, -1):
        bell(bm, (s * 0.75, 0), 0.0, 2.9, 0.57)
    # Корпус одним замкнутым телом: днище хвостового отсека → обечайка → вогнутый верхний купол к оси.
    lathe(bm, [(0, 2.6), (RC - 0.03, 2.6), (RC - 0.03, 1.2), (RC, 1.2), (RC, 3.4), (RC, 24.0), (RC, 24.6),
               (RC, 26.0), (RC, CORE_L), (RC - 0.03, CORE_L), (0, CORE_L - 0.6)], 48,
          mif=paint(bands=[(1.2, 3.41, B), (CORE_L - 0.01, CORE_L + 1, B)], roll=[(24.6, 26.0, 16, 0)]))
    box(bm, (RC + 0.04, 0, 13.8), (0.12, 0.3, 20.0), M)       # кабельный короб — к +X Blender, мимо SRMU
    for z in (3.4, 13.7, 24.0):
        ring(bm, RC, z, 0.04, 0.1)
    # Узлы крепления SRMU: передний (воспринимает тягу) у верха бака окислителя и задний над юбкой. Пара: SRMU_OFF.
    gap = SRMU_OFF - RC - RS
    for az in (90, 270):
        x, y = polar(RC + gap * 0.5, az)
        box(bm, (x, y, 22.8), (gap + 0.1, 0.9, 1.2), M, az=az)
        box(bm, (x, y, 4.4), (gap + 0.1, 0.5, 0.5), M, az=az)
        for dz in (-0.6, 0.6):
            box(bm, (x, y, 4.4 + dz), (gap + 0.1, 0.2, 0.2), M, az=az)


# ================================================================== РДТТ SRMU (L 34,3, Ø 3,2)
SRMU_L = 34.3


def b_titan4_srmu(bm):
    """0 сегменты (белые), 1 юбка, носовой конус и обтекатель тракта, 2 стыки и узлы, 3 сопло.
    Три сегмента в графито-эпоксидном корпусе (у Titan IVA их было семь, стальных), сопло качается — без бака впрыска."""
    bell(bm, (0, 0), 0.0, 3.4, 1.15, segs=32)
    # Кормовая юбка — раструбом к срезу, стенкой 5 см (замкнутое кольцо, а не оболочка).
    lathe(bm, [(1.3, 0.6), (RS + 0.08, 2.6), (RS, 3.4), (RS - 0.05, 3.4), (RS + 0.03, 2.6), (1.25, 0.6)], 48, B,
          closed=True)
    seg0, nose0 = 3.4, 28.6
    nose = [(RS, nose0), (1.5, 29.8), (1.25, 31.2), (0.9, 32.4), (0.5, 33.5), (0.0, SRMU_L)]
    # Днище под юбкой закрывает тело снизу (иначе сквозь кольцо юбки видна изнанка сегментов).
    lathe(bm, [(0.0, seg0), (RS, seg0), (RS, nose0)] + nose, 48, mif=lambda az, z: B if z > nose0 + 0.01 else H)
    step = (nose0 - seg0) / 3
    for k in range(4):
        ring(bm, RS + 0.01, seg0 + step * k, 0.05, 0.25)
    # Обтекатель кабельного тракта вдоль корпуса — наружу (−X Blender = +X Unity у радиальной копии).
    box(bm, (-(RS + 0.06), 0, 16.0), (0.14, 0.35, 24.0), B)
    # Ответные ложементы узлов ядра — со стороны ядра (+X Blender).
    for z, h in ((22.8, 1.2), (4.4, 1.3)):
        box(bm, (RS + 0.03, 0, z), (0.1, 0.7, h), M)


# ================================================================== орбитер «Кассини» (L 6,8; HGA Ø 4,0)
CAS_L = 6.8
MAG_L = 11.0             # штанга магнитометра 11 м от борта (у настоящего — 11 м)


def dodecagon(bm, r, z0, z1, mif):
    """Двенадцатигранная шина электроники: призма с плоскими гранями и крышками."""
    lathe(bm, [(0.0, z0), (r, z0), (r, z1), (0.0, z1)], 12, mif=mif, smooth=False, az0=15.0)


def rtg(bm):
    """GPHS-РИТЭГ у начала: цилиндр Ø 0,42 × 1,14 по оси Z с восемью рёбрами-радиаторами."""
    lathe(bm, [(0.0, -0.57), (0.21, -0.57), (0.21, 0.57), (0.0, 0.57)], 12, B, smooth=False)
    for k in range(4):
        box(bm, (0, 0, 0), (0.64, 0.03, 1.0), B, az=45 * k)


def b_cassini(bm):
    """0 тарелка HGA (белая), 1 одеяла ЭВТИ и РИТЭГи (тёмное), 2 металл (сопла, штанги, кольца), 3 фольга.
    Снизу вверх: два ЖРД 445 Н, нижний приборный отсек с тремя РИТЭГами, двигательный модуль (баки в золотой фольге),
    12-гранная шина, верхний отсек, HGA Ø 4 м. «Гюйгенс» в игре стоит секциями выше (стопкой, не сбоку, как у
    настоящего) — над контррефлектором. Штанга магнитометра 11 м — к +Y Blender."""
    for s in (1, -1):
        bell(bm, (s * 0.3, 0), 0.0, 0.9, 0.2, mat=M, chamber=M)
    lathe(bm, [(0.0, 0.8), (0.75, 0.8), (0.75, 1.35), (0.0, 1.35)], 48, B, closed=False)
    # Три РИТЭГа на кронштейнах: наружу и вниз на 30°, азимуты 60/180/300 (мимо штанги магнитометра на 90°).
    for az in (60, 180, 300):
        x, y = polar(1.3, az)
        tilt = Matrix.Rotation(math.radians(az), 4, 'Z') @ Matrix.Rotation(math.radians(120), 4, 'Y')
        placed(bm, Matrix.Translation((x, y, 0.85)) @ tilt, lambda: rtg(bm))
        x0, y0 = polar(0.72, az)
        rod(bm, (x0, y0, 1.05), (x * 0.8, y * 0.8, 0.95), 0.04, M)
    lathe(bm, [(0.0, 1.35), (0.8, 1.35), (0.8, 4.3), (0.0, 4.3)], 48, N)
    for z in (1.35, 2.8, 4.3):
        ring(bm, 0.8, z, 0.04, 0.08)
    dodecagon(bm, 1.05, 4.3, 5.0, mif=lambda az, z: N if int(az / 30.0) % 3 == 0 else B)
    lathe(bm, [(0.0, 5.0), (0.65, 5.0), (0.65, 5.55), (0.0, 5.55)], 32, B)
    # HGA: параболоид, вершина на 5,55, кромка Ø 4,0 на 6,25; контррефлектор на четырёх подкосах до 6,8.
    dish_z, rim_r, depth = 5.55, 2.0, 0.7
    n = 8
    top = [(rim_r * i / n, dish_z + depth * (i / n) ** 2) for i in range(n + 1)]
    back = [(r, z - 0.05) for r, z in top]
    lathe(bm, back + list(reversed(top)), 48, H, sharp=60)
    for k in range(4):
        x, y = polar(1.3, 45 + 90 * k)
        zr = dish_z + depth * (1.3 / rim_r) ** 2
        rod(bm, (x, y, zr), (0.18 * x / 1.3, 0.18 * y / 1.3, 6.68), 0.025, M)
    lathe(bm, [(0.0, 6.62), (0.35, 6.66), (0.35, 6.72), (0.0, CAS_L)], 32, H)
    # Штанга магнитометра: контейнер у верхнего отсека и ферма 11 м с датчиками посередине и на конце.
    zm = 5.25
    placed(bm, Matrix.Translation((0.0, 0.65, zm)) @ Matrix.Rotation(math.radians(-90), 4, 'X'),
           lambda: lathe(bm, [(0.0, 0.0), (0.15, 0.0), (0.15, 0.6), (0.0, 0.6)], 12, M, smooth=False))
    yb, ye = 1.25, 1.25 + MAG_L
    R3 = 0.2 / math.sqrt(3.0)
    cor = [(R3 * math.cos(math.radians(90 + 120 * k)), R3 * math.sin(math.radians(90 + 120 * k))) for k in range(3)]
    for cx, cz in cor:
        rod(bm, (cx, yb, zm + cz), (cx, ye, zm + cz), 0.012, M, 4)
    bays = 18
    for i in range(bays + 1):
        y = yb + (ye - yb) * i / bays
        for k in range(3):
            (ax, az_), (bx, bz) = cor[k], cor[(k + 1) % 3]
            rod(bm, (ax, y, zm + az_), (bx, y, zm + bz), 0.008, M, 4)
    for y, s in ((yb + MAG_L * 0.5, 0.12), (ye + 0.1, 0.18)):
        box(bm, (0.0, y, zm + 0.15 + s * 0.5), (s, s, s), N)


# ================================================================== «Гюйгенс»: экран, крышка, модуль
def b_huygens_shield(bm):
    """0 плитки AQ60 (цвет экрана), 1 тыльная сторона, 2 кромка. Затупленный конус 60° Ø 2,7, носом вниз (−Z):
    «Гюйгенс» входит днищем вперёд."""
    R = 1.35
    k = math.tan(math.radians(30))               # полуугол 60°: подъём профиля на единицу радиуса
    prof = [(0.0, 0.0), (0.25, 0.02), (0.45, 0.07), (0.6, 0.12)]
    prof += [(1.28, 0.12 + 0.68 * k), (1.35, 0.12 + 0.68 * k + 0.05), (R, 0.58), (1.25, 0.6), (0.0, 0.6)]
    lathe(bm, prof, 64, mif=lambda az, z: H if z < 0.5 else (M if z < 0.59 else B), sharp=30)


def b_huygens_cover(bm):
    """0 фольга ЭВТИ, 2 кромка и узлы, 1 люк купола (слоты подряд с 0: субмеш FBX = индекс палитры). Задняя крышка — усечённый конус Ø 2,6 → 1,4."""
    lathe(bm, [(0.0, 0.0), (1.3, 0.0), (1.3, 0.06), (0.72, 0.46), (0.7, 0.5), (0.0, 0.5)], 64,
          mif=lambda az, z: M if z < 0.07 else H, sharp=30)
    for k in range(3):                            # узлы разделения с орбитером (SED) — по краю
        x, y = polar(1.18, 30 + 120 * k)
        box(bm, (x, y, 0.13), (0.16, 0.16, 0.14), M, az=30 + 120 * k)
    box(bm, (0.35, 0, 0.5), (0.3, 0.3, 0.04), B)


def b_huygens(bm):
    """0 фольга, 2 платформы и антенны, 1 контейнер купола. Спускаемый модуль Ø 1,3: днище-купол, корпус,
    верхняя платформа с контейнером парашютов."""
    lathe(bm, [(0.0, 0.0), (0.45, 0.03), (0.62, 0.12), (0.65, 0.2), (0.65, 0.48), (0.58, 0.6), (0.3, 0.74), (0.0, 0.76)],
          48, H, sharp=30)
    ring(bm, 0.65, 0.48, 0.04, 0.06)
    lathe(bm, [(0.0, 0.72), (0.22, 0.72), (0.22, 0.9), (0.0, 0.9)], 24, B)
    for k in range(2):                            # антенны радиоканала с орбитером
        x, y = polar(0.42, 90 + 180 * k)
        rod(bm, (x, y, 0.62), (x, y, 0.82), 0.04, M, 8)


MODELS_CASSINI = [("Titan4_Core", b_titan4_core), ("Titan4_SRMU", b_titan4_srmu), ("Cassini", b_cassini),
                  ("Huygens_Shield", b_huygens_shield), ("Huygens_Cover", b_huygens_cover), ("Huygens", b_huygens)]


def main(only=None, do_export=True):
    for name, fn in MODELS_CASSINI:
        if only and name not in only:
            continue
        print("CASSINI", build(name, fn))
        if do_export:
            export(name)


if __name__ == "__main__":
    main()
