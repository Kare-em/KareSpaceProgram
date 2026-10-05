import bpy, sys, math
sys.path.insert(0, r"C:\CocosGames\KareSpaceProgram\Tools\blender")
import hulls_lib, station_parts
# Раскладное «станционных» кораблей (§6.12): откидной носок Crew Dragon и створки СБ ПАО «Союза» — отдельными FBX.
# Повторяемо: модели сначала пересобираются из station_parts.py, потом острова-детали уносятся в свои объекты.
# Функции разрезки — из split_deploy.py (там же разовый прогон опор, его не запускаем: берём только определения).
src = open(r"C:\CocosGames\KareSpaceProgram\Tools\blender\split_deploy.py", encoding="utf-8").read()
exec(src[:src.index("r = lambda v")])

station_parts.main(do_export=False, only=["Crew_Dragon", "Soyuz_PAO"])
# Носок: всё выше палубы узла (3,06 м); шарнир (низ 3,025) остаётся на корпусе. Пара: VesselView.NoseHinge.
nose = split("Crew_Dragon", "Crew_Dragon_Nose", lambda p: min(v.z for v in p) > 3.07, (0,))
# Створки СБ и рама: острова дальше 1,5 м от оси по X (корешок PAO_R → 1,55 и блоки ДПО на 1,41 — на корпусе).
# Пара: VesselView.DeployHinge(SoyuzPAO) — шарнир на 1,55 м, высота 0,7.
pan = split("Soyuz_PAO", "Soyuz_PAO_Panel", lambda p: min(abs(v.x) for v in p) > 1.5 and max(abs(v.y) for v in p) < 0.1,
            (0, 180))
for n in ("Crew_Dragon", "Soyuz_PAO"):
    hulls_lib.export(n)
export_many("Crew_Dragon_Nose", nose)
export_many("Soyuz_PAO_Panels", pan)
print("SD exported")
