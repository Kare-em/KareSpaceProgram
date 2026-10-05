# Starship IFT-5 (SpaceXRockets.cs): ускоритель Super Heavy B12 с кольцом горячего разделения и корабль Starship S30.
# Плюс пере-экспорт Falcon9_S1 из station_parts.py (без запечённых опор и рулей — их раскрывает VesselView.SpaceX).
# Запуск (parts.blend не сохраняется — его же пишет station_parts.py):
#   "/c/Program Files/Blender Foundation/Blender 5.2/blender.exe" -b Tools/blender/parts.blend --python Tools/blender/spacex_parts.py < /dev/null
# Ось +Z (в Unity +Y — нос), начало — срез самого нижнего сопла, метры, натуральная величина: L/Ø — из SpaceXRockets.cs.
# Брюхо корабля — к −X Blender (= +X Unity, оси секции: +X брюхо), как у орбитеров winged_parts.py.
# Слоты: 0 нержавейка (PolishedColor), 1 чёрное (плитки ТЗП, кольцо), 2 металл (рули, шарниры), 3 сопла.
import math, os
import bpy, bmesh

TOOLS = r"C:\CocosGames\KareSpaceProgram\Tools\blender"
os.environ.pop("STATION_RUN", None)
exec(open(os.path.join(TOOLS, "hulls_lib.py"), encoding="utf-8").read())
_main_name = __name__
__name__ = "station_parts"      # station_parts.py без своего main: только функции и MODELS
exec(open(os.path.join(TOOLS, "station_parts.py"), encoding="utf-8").read())
__name__ = _main_name

R = 4.5                          # Ø 9 м у обоих


def raptor(bm, c, z0, vac=False):
    """Raptor 2: срез Ø 1,3 м, высота ≈ 1,6; RVac — срез Ø 2,3, высота ≈ 2,8."""
    if vac:
        bell(bm, c, z0, 2.8, 1.15, segs=24)
    else:
        bell(bm, c, z0, 1.6, 0.65, segs=16)


# ================================================================== Super Heavy (L 72,8 = 71 + кольцо 1,8, Ø 9)
SH_L, SH_RING = 72.8, 1.8


def b_super_heavy(bm):
    """0 нержавейка, 1 донный экран и окна кольца горячего разделения, 2 решётчатые рули, вывод газа, 3 Raptor."""
    # 33 Raptor: 3 в центре, 10 во внутреннем кольце (качаются), 20 по краю (неподвижные, за юбкой).
    for k in range(3):
        raptor(bm, polar(0.95, 90 + 120 * k), 0.0)
    for k in range(10):
        raptor(bm, polar(2.45, 18 + 36 * k), 0.0)
    for k in range(20):
        raptor(bm, polar(3.75, 9 + 18 * k), 0.0)
    # Донный экран между соплами и юбка до среза внешнего кольца.
    lathe(bm, [(0.0, 1.55), (R - 0.05, 1.55)], 64, B)
    hot = SH_L - SH_RING
    lathe(bm, [(R, 0.15), (R, 1.55), (R, hot)], 96)
    # Кольцо горячего разделения: решётка окон (чёрные сектора) под крышкой-куполом.
    lathe(bm, [(R, hot), (R, SH_L - 0.25), (R - 0.15, SH_L), (0.0, SH_L)], 96,
          mif=lambda az, z: B if (z < SH_L - 0.3 and int(az / 7.5) % 2 == 0) else M)
    ring(bm, R + 0.03, hot, 0.08, 0.25, M, 96)
    # Шпангоуты-сварные пояса по баку и общая днищевая перегородка (CH₄ сверху, O₂ снизу).
    for z in (8.0, 26.0, 44.0, 62.0):
        ring(bm, R + 0.02, z, 0.05, 0.12, M, 96)
    # Четыре решётчатых руля под кольцом (у Super Heavy не складываются): решётка ≈ 3 × 2,7 м, плоскость — поперёк оси.
    for k in range(4):
        az = 45 + 90 * k
        x, y = polar(R + 1.45, az)
        box(bm, (x, y, hot - 2.4), (2.7, 3.0, 0.35), M, az=az)
        x, y = polar(R + 0.15, az)
        box(bm, (x, y, hot - 2.4), (0.4, 1.2, 0.9), M, az=az)   # привод
    # Магистраль газового наддува и кабельный короб — тёмная полоса вдоль бака.
    x, y = polar(R + 0.12, 90)
    box(bm, (x, y, hot * 0.5), (0.3, 0.6, hot - 4.0), B, az=90)
    # Цапфы ловли («ловильные штыри») под рулями.
    for az in (0, 180):
        x, y = polar(R + 0.35, az)
        box(bm, (x, y, hot - 6.0), (0.8, 0.9, 0.7), M, az=az)


# ================================================================== Starship (L 50,3, Ø 9)
SS_L, SS_CYL = 50.3, 31.5


def b_starship(bm):
    """0 нержавейка (подветренная сторона), 1 плитки ТЗП (брюхо, −X Blender), 2 узлы закрылков, 3 Raptor и RVac.
    Закрылки — не здесь: их строит VesselView.Controls по SpaceXRockets.StarshipFlaps (шарниры, отклонение)."""
    for k in range(3):
        raptor(bm, polar(1.05, 60 + 120 * k), 1.2)            # Raptor у Земли — срез выше RVac
    for k in range(3):
        raptor(bm, polar(2.95, 120 * k), 0.0, vac=True)        # RVac — ниже всех
    lathe(bm, [(0.0, 2.75), (R - 0.05, 2.75)], 64, B)          # донный экран
    # Плитки: брюхо ± 100° от −X; на носу тайлы заходят выше — сторона шире (как у S30: «шапка» нос-закрылки).
    def tiles(az, z):
        d = abs(((az - 180.0) + 180.0) % 360.0 - 180.0)
        return B if d < (100.0 if z < SS_CYL else 112.0) else H
    nose = [(R, SS_CYL), (4.42, 34.5), (4.18, 37.5), (3.75, 40.5), (3.12, 43.3), (2.32, 45.8), (1.45, 47.9), (0.65, 49.5), (0.0, SS_L)]
    lathe(bm, [(R, 0.9), (R, 2.75)] + nose, 120, mif=tiles)
    for z in (6.0, 13.0, 20.0, 27.0):
        ring(bm, R + 0.02, z, 0.04, 0.1, M, 96)
    # Обтекатели шарниров закрылков: у кормовых — по всей длине шарнира, у носовых — коротко (пара: StarshipFlaps).
    for side in (-1, 1):
        y = side * (R * 0.97 + 0.15)
        box(bm, (-0.8, y, 0.145 * SS_L), (0.7, 0.45, 0.23 * SS_L), M)
        box(bm, (-0.7, side * 3.15, 0.84 * SS_L), (0.5, 0.4, 0.12 * SS_L), M)


MODELS_SPACEX = [("Super_Heavy", b_super_heavy), ("Starship", b_starship), ("Falcon9_S1", b_f9_s1)]

for name, fn in MODELS_SPACEX:
    print("SPACEX", build(name, fn))
    export(name)
