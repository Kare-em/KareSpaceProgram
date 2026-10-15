# «Титан IIIE — Центавр» с «Вояджером» (PlanetaryRockets.cs, GDD §5.3): ядро I ступени «Титана III», пятисегментный
# РДТТ UA1205, разгонный модуль TE-M-364-4 и сам зонд. «Центавр» и II ступень — готовые Centaur / Titan_Stage2.
# Запуск (parts.blend не сохраняется — его пишет station_parts.py, модели строятся заново из кода):
#   "/c/Program Files/Blender Foundation/Blender 5.2/blender.exe" -b Tools/blender/parts.blend --python Tools/blender/voyager_parts.py < /dev/null
# Ось +Z (в Unity +Y — нос), начало — днище секции, метры, натуральная величина: L/Ø — из PlanetaryRockets.cs.
# Оси FBX → Unity: (x, y, z) → (−x, z, −y). РДТТ стоят на RadialPhase = π/2 → Unity ±Z = Blender ∓Y, поэтому узлы
# крепления на ядре — на азимутах 90°/270°; у РДТТ +X Unity (наружу, VesselView: part.Yaw) = −X Blender.
# Слоты (hulls_lib.SLOTS: Hull/Black/Metal/Nozzle) — смысл у каждой модели в docstring, палитра — VesselView.CraftPalette.
import math, os
import bpy, bmesh

TOOLS = r"C:\CocosGames\KareSpaceProgram\Tools\blender"
exec(open(os.path.join(TOOLS, "hulls_lib.py"), encoding="utf-8").read())
# Из hulls_models.py нужны только помощники (paint, polar, top_bowl): его HULLS строит лишь вызывающий код.
exec(open(os.path.join(TOOLS, "hulls_models.py"), encoding="utf-8").read())

def placed(bm, mtx, make):
    """Строит деталь у начала координат (make()) и переносит только её новые вершины матрицей mtx."""
    before = set(bm.verts)
    make()
    bmesh.ops.transform(bm, matrix=mtx, verts=[v for v in bm.verts if v not in before])


RC = 1.525                       # радиус ядра и РДТТ (Ø 3,05); пара: TitanSrbOffset = 1,525 + 1,525 + 0,3
SRB_OFF = 3.35                   # ось РДТТ от оси ядра — PlanetaryRockets.TitanSrbOffset


# ================================================================== ядро «Титана IIIE» (L 22,2, Ø 3,05)
CORE_L = 22.2


def b_titan3_core(bm):
    """0 корпус (металл), 1 юбки и днища, 2 узлы крепления РДТТ и короб кабелей, 3 сопла LR87.
    Как Titan_Stage1, но на 1,2 м длиннее и с узлами крепления РДТТ на ±Y Blender (= ∓Z Unity, RadialPhase π/2)."""
    for s in (1, -1):
        bell(bm, (s * 0.75, 0), 0.0, 2.9, 0.57)
    lathe(bm, [(0, 2.6), (RC - 0.03, 2.6), (RC - 0.03, 1.2), (RC, 1.2)], 48, B)
    lathe(bm, [(RC, 1.2), (RC, 3.4), (RC, 19.8), (RC, 20.4), (RC, 21.8), (RC, CORE_L)], 48,
          mif=paint(bands=[(1.2, 3.41, B)], roll=[(20.4, 21.8, 16, 0)]))
    top_bowl(bm, RC, CORE_L, 20.1, 19.5, mat=B)
    box(bm, (RC + 0.04, 0, 11.6), (0.12, 0.3, 15.6), M)       # кабельный короб — к +X Blender, мимо РДТТ
    for z in (3.4, 11.5, 19.8):
        ring(bm, RC, z, 0.04, 0.1)
    # Узлы крепления РДТТ: передний (воспринимает тягу) у верха бака окислителя и задний над юбкой. Перекрывают
    # зазор 0,3 м до обечайки РДТТ — пакет читается связанным. Пара: SRB_OFF, RC.
    gap = SRB_OFF - 2 * RC
    for az in (90, 270):
        x, y = polar(RC + gap * 0.5, az)
        box(bm, (x, y, 18.6), (gap + 0.1, 0.9, 1.1), M, az=az)          # передний узел — балка с шаровой опорой
        box(bm, (x, y, 4.2), (gap + 0.1, 0.5, 0.5), M, az=az)           # задний — два подкоса
        for dz in (-0.6, 0.6):
            box(bm, (x, y, 4.2 + dz), (gap + 0.1, 0.2, 0.2), M, az=az)


# ================================================================== РДТТ UA1205 (L 25,9, Ø 3,05)
SRB_L = 25.9


def b_titan_srb(bm):
    """0 сегменты (белые), 1 юбка и носовой конус, 2 стыки сегментов, бак и трубы впрыска, 3 сопло.
    Бак впрыска N₂O₄ для управления вектором тяги — снаружи пакета (−X Blender = +X Unity у радиальной копии)."""
    bell(bm, (0, 0), 0.0, 3.0, 0.95, segs=32)
    # Кормовая юбка с обтекателем сопла: раструб шире сегментов (как у UA1205 — расширение к срезу).
    lathe(bm, [(1.2, 0.5), (RC + 0.06, 2.4), (RC, 3.2)], 48, B)
    seg0, nose0 = 3.2, 21.4                       # пять сегментов между юбкой и носовым конусом
    nose = [(RC, nose0), (1.45, 22.4), (1.2, 23.6), (0.85, 24.6), (0.45, 25.4), (0.0, SRB_L)]
    lathe(bm, [(RC, seg0), (RC, nose0)] + nose, 48, mif=lambda az, z: B if z > nose0 + 0.01 else H)
    step = (nose0 - seg0) / 5
    for k in range(6):
        ring(bm, RC + 0.01, seg0 + step * k, 0.05, 0.22)
    # Бак системы впрыска — цилиндр вдоль 2-го…4-го сегментов, на −X Blender, с трубами к соплу.
    tank_r = 0.32
    cx = -(RC + tank_r + 0.02)
    lathe(bm, [(0.0, 7.0), (tank_r * 0.7, 7.05), (tank_r, 7.3), (tank_r, 15.5), (tank_r * 0.7, 15.75), (0.0, 15.8)],
          16, M, c=(cx, 0.0))
    box(bm, (-(RC + 0.08), 0, 4.7), (0.12, 0.12, 4.6), M)             # магистраль к сопловому блоку
    for z in (8.0, 14.5):
        box(bm, (-(RC + 0.02), 0, z), (0.12, 0.5, 0.25), M)           # бугели бака
    # Ответные ложементы узлов ядра — со стороны ядра (+X Blender = −X Unity, к оси пакета).
    for z, h in ((18.6, 1.1), (4.2, 1.3)):
        box(bm, (RC + 0.03, 0, z), (0.1, 0.7, h), M)


# ================================================================== разгонный модуль TE-M-364-4 (L 2,0, Ø 1,8)
PM_L = 2.0


def b_voyager_pm(bm):
    """0 корпус РДТТ (сфера в ЭВТИ), 1 кольцо-адаптер, 2 ферма и блоки ЖРД, 3 сопло.
    Сфера Ø 0,93 м, сопло вниз до днища секции; ферма — от кольца стыка с «Центавром» до кольца под зондом."""
    sc, sr = 1.12, 0.47                           # центр и радиус сферы корпуса
    bell(bm, (0, 0), 0.0, 0.75, 0.3, segs=24)
    sphere(bm, sr, sc, H, segs=24, k=12)
    ring(bm, sr + 0.02, sc, 0.05, 0.08)           # экваториальный шпангоут — к нему сходится ферма
    lathe(bm, [(0.82, 0.0), (0.9, 0.0), (0.9, 0.12), (0.82, 0.12)], 48, B, closed=True)   # нижнее кольцо стыка
    lathe(bm, [(0.8, 1.86), (0.88, 1.86), (0.88, PM_L), (0.8, PM_L)], 48, B, closed=True)  # верхнее — под зонд
    truss(bm, 0.86, 0.12, sr + 0.04, sc, 8, 0.025, rings=False)
    truss(bm, sr + 0.04, sc, 0.84, 1.86, 8, 0.025, rings=False)
    # Четыре блока гидразиновых ЖРД ориентации модуля на верхнем кольце.
    for k in range(4):
        x, y = polar(0.86, 45 + 90 * k)
        box(bm, (x, y, 1.78), (0.16, 0.12, 0.16), M, az=45 + 90 * k)


# ================================================================== «Вояджер» (L 3,4; HGA Ø 3,66)
VG_L = 3.4


def decagon(bm, r, z0, z1, mif):
    """Десятигранная шина: призма с плоскими гранями (lathe по 10 сегментам), крышки сверху и снизу."""
    lathe(bm, [(0.0, z0), (r, z0), (r, z1), (0.0, z1)], 10, mif=mif, smooth=False, az0=18.0)


def b_voyager(bm):
    """0 тарелка HGA (белая), 1 шина и одеяла ЭВТИ (тёмное), 2 штанги, фермы, РИТЭГи, 3 фольга (адаптер, бак, платформа).
    Под обтекателем Ø 4,27 всё в r ≤ 2,0: штанги укорочены (у настоящего сложены), магнитометр — в контейнере."""
    # Адаптер к модулю — конус фольги; шина 0,47 м высотой, ширина ≈ 1,78 по вершинам.
    lathe(bm, [(0.84, 0.0), (0.84, 0.08), (0.6, 0.42), (0.0, 0.42)], 32, N)
    z0, z1 = 0.42, 0.89
    decagon(bm, 0.89, z0, z1, mif=lambda az, z: N if int(az / 36.0) % 5 == 0 else B)
    # Ферма под тарелкой и сама тарелка: параболоид, вершина на 1,05, кромка Ø 3,66 на 1,55 (глубина ≈ f/D 0,33).
    truss(bm, 0.6, z1, 0.45, 1.06, 6, 0.02, rings=False)
    dish_z, rim_r, depth = 1.05, 1.83, 0.5
    n = 8
    top = [(rim_r * i / n, dish_z + depth * (i / n) ** 2) for i in range(n + 1)]
    back = [(r, z - 0.04) for r, z in top]
    lathe(bm, back + list(reversed(top)), 48, H, sharp=60)   # тыл от оси к кромке, лицо обратно — нормали наружу
    # Контррефлектор на четырёх подкосах и рупор облучателя; над ним малонаправленная антенна LGA.
    for k in range(4):
        x, y = polar(1.15, 45 + 90 * k)
        zr = dish_z + depth * (1.15 / rim_r) ** 2
        strut(bm, (x, y, zr), (0.22 * x / 1.15, 0.22 * y / 1.15, 2.55), 0.02)
    lathe(bm, [(0.0, 1.05), (0.12, 1.05), (0.12, 1.5), (0.08, 1.9), (0.0, 1.9)], 16, M)
    lathe(bm, [(0.0, 2.52), (0.3, 2.56), (0.3, 2.62), (0.0, 2.66)], 32, H)
    lathe(bm, [(0.0, 2.66), (0.06, 2.66), (0.06, 2.9), (0.2, 3.25), (0.18, 3.32), (0.0, VG_L)], 16, H)
    # Штанга РИТЭГов — к +X Blender: три цилиндра с рёбрами-радиаторами друг за другом под тарелкой.
    zb = 0.62
    strut(bm, (0.85, 0, zb), (1.95, 0, zb + 0.25), 0.035)
    for k in range(3):
        cx = 1.05 + 0.33 * k
        cz = zb + 0.05 + 0.23 * (cx - 0.85) / 1.1
        placed(bm, Matrix.Translation((cx, 0, cz)) @ Matrix.Rotation(math.radians(90), 4, 'Y'),
               lambda: lathe(bm, [(0.0, -0.14), (0.2, -0.14), (0.2, 0.14), (0.0, 0.14)], 12, M, smooth=False))
        box(bm, (cx, 0, cz), (0.24, 0.62, 0.03), B)                  # рёбра-радиаторы крестом
        box(bm, (cx, 0, cz), (0.24, 0.03, 0.62), B)
    # Научная штанга — к −X Blender: платформа сканирования (камеры ISS, спектрометры) на конце.
    strut(bm, (-0.85, 0, zb), (-1.55, 0, zb + 0.15), 0.04)
    box(bm, (-1.65, 0, zb + 0.2), (0.4, 0.45, 0.35), N)
    for dy in (-0.12, 0.12):                                         # камеры ISS узкого и широкого поля
        placed(bm, Matrix.Translation((-1.72, dy, zb + 0.37)),
               lambda: lathe(bm, [(0.0, 0.0), (0.07, 0.0), (0.07, 0.3), (0.0, 0.3)], 12, B))
    # Магнитометр: штанга-«астромаст» сложена в контейнер, стоит у шины между штангами (к +Y Blender).
    placed(bm, Matrix.Translation((0.0, 1.05, 0.3)),
           lambda: lathe(bm, [(0.0, 0.0), (0.13, 0.0), (0.13, 0.75), (0.0, 0.75)], 12, M))
    strut(bm, (0.0, 0.85, 0.65), (0.0, 1.0, 0.65), 0.04)
    # Антенны радиоастрономии PRA — две сложенные «усы» вверх-наружу на −Y.
    for dx in (-0.12, 0.12):
        strut(bm, (dx, -0.85, z1), (dx * 2, -1.35, 1.25), 0.015)


MODELS_VOYAGER = [("Titan3_Core", b_titan3_core), ("Titan_SRB", b_titan_srb),
                  ("Voyager_PM", b_voyager_pm), ("Voyager", b_voyager)]


def main(only=None, do_export=True):
    for name, fn in MODELS_VOYAGER:
        if only and name not in only:
            continue
        print("VOYAGER", build(name, fn))
        if do_export:
            export(name)


if __name__ == "__main__":
    main()
