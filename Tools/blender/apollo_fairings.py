# Apollo_LES (система аварийного спасения + защитный колпак КМ) и Apollo_SLA_Half (половина переходника КК/ЛМ).
# Запуск в Blender (MCP): exec(open(r"C:\CocosGames\KareSpaceProgram\Tools\blender\apollo_fairings.py", encoding="utf-8").read())
# Требует hulls_lib.py (lathe, bell, ring, strut, fin, box, sphere, build, export, isolate) — подгружается ниже, если не загружена.
# Ось +Z (в Unity станет +Y), метры, натуральная величина. Слоты: 0 Hull, 1 Black, 2 Metal, 3 Nozzle (заняты все 4 — иначе FBX сдвигает субмеши).
import math, os
import bpy, bmesh
from mathutils import Vector, Matrix

if "lathe" not in globals():
    exec(open(r"C:\CocosGames\KareSpaceProgram\Tools\blender\hulls_lib.py", encoding="utf-8").read())

CM_R_BASE = 1.96   # КМ: радиус днища (пара: BPC_R_BASE, SLA_R_TOP — всё стыкуется по 1.96)
CM_H = 3.2         # КМ: высота; колпак выше КМ — верх колпака на z=3.45


# ------------------------------------------------------------------ Apollo_LES
BPC_R_BASE, BPC_R_TOP, BPC_H = 1.98, 0.45, 3.45       # колпак: чуть больше КМ (1.96 / ~0.4 на z=3.2), пара: CM_R_BASE
TOWER_Z0, TOWER_Z1 = 3.45, 6.35                        # ферма-башня (~3 м)
TOWER_R0, TOWER_R1 = 0.42, 0.28                        # радиусы ног снизу/сверху (внизу — на верхушке колпака r=0.45)
MOTOR_Z0, MOTOR_L, MOTOR_R = 6.45, 4.5, 0.33
NOSE_Z = MOTOR_Z0 + MOTOR_L                            # 10.95; выше — ожива до 12.95, шар Q на 13.03 (R 0.07) => zmax 13.10


def polar(r, az):
    a = math.radians(az)
    return (r * math.cos(a), r * math.sin(a))


def bell_tilt(bm, az, pivot_r, pivot_z, h, re, tilt_deg):
    """Скошенное сопло: камера (верх) в точке pivot, срез уходит наружу-вниз на tilt°. bell() строит вдоль +Z срезом на z0."""
    before = set(bm.verts)
    bell(bm, (0.0, 0.0), 0.0, h, re, segs=12)
    new = [v for v in bm.verts if v not in before]
    m = (Matrix.Rotation(math.radians(az), 4, 'Z') @ Matrix.Translation((pivot_r, 0.0, pivot_z)) @
         Matrix.Rotation(-math.radians(tilt_deg), 4, 'Y') @ Matrix.Translation((0.0, 0.0, -h)))
    bmesh.ops.transform(bm, matrix=m, verts=new)


def b_apollo_les(bm):
    # --- колпак (оболочка со стенкой 2 см, открыт снизу): наружный конус -> верхний диск -> внутренний конус
    lathe(bm, [(BPC_R_BASE, 0.0), (BPC_R_TOP, BPC_H), (0.0, BPC_H), (BPC_R_TOP - 0.02, BPC_H - 0.02), (BPC_R_BASE - 0.02, 0.0)],
          36, H, closed=True, sharp=30)
    ring(bm, BPC_R_BASE - 0.015, 0.06, 0.03, 0.12, segs=36)           # нижний бандаж колпака (внешний r ровно 1.98)
    # --- переходник/юбка колпак -> башня
    lathe(bm, [(0.46, BPC_H - 0.02), (0.46, BPC_H + 0.14), (TOWER_R0 + 0.03, BPC_H + 0.14), (TOWER_R0 + 0.03, BPC_H + 0.18)], 24, M, sharp=30)
    ring(bm, TOWER_R0, TOWER_Z0 + 0.1, 0.05, 0.08, segs=16)
    # --- ферма-башня: 4 ноги + X-раскосы в 3 пролётах
    def rr(z):
        return TOWER_R0 + (TOWER_R1 - TOWER_R0) * (z - TOWER_Z0) / (TOWER_Z1 - TOWER_Z0)
    zs = [TOWER_Z0 + 0.1 + (TOWER_Z1 - TOWER_Z0 - 0.1) * i / 3 for i in range(4)]
    azs = [45 + 90 * k for k in range(4)]
    for az in azs:
        strut(bm, (*polar(rr(zs[0]), az), zs[0]), (*polar(rr(zs[-1]), az), zs[-1]), 0.035, M)
    for i in range(3):
        for k in range(4):
            a, b = azs[k], azs[(k + 1) % 4]
            pa0, pa1 = (*polar(rr(zs[i]), a), zs[i]), (*polar(rr(zs[i + 1]), a), zs[i + 1])
            pb0, pb1 = (*polar(rr(zs[i]), b), zs[i]), (*polar(rr(zs[i + 1]), b), zs[i + 1])
            strut(bm, pa0, pb1, 0.02, M, segs=5)
            strut(bm, pb0, pa1, 0.02, M, segs=5)
    for z in zs[1:3]:
        ring(bm, rr(z), z, 0.03, 0.05, segs=16)
    # --- юбка двигателя (Metal): закрытое днище, расширение к цилиндру
    lathe(bm, [(0.0, 6.30), (0.30, 6.30), (0.36, 6.55), (0.36, 6.80)], 24, M, sharp=30)
    ring(bm, 0.37, 6.80, 0.03, 0.06, segs=24)
    # --- 4 скошенных сопла у низа двигателя (между ногами фермы)
    for k in range(4):
        bell_tilt(bm, 90 * k, 0.29, 7.05, 0.75, 0.10, 22.0)
    # --- корпус РДТТ + ожива, чёрная полоса в середине
    def paint(az, zm):
        return B if 9.35 <= zm < 9.85 else H
    nose = [(0.325, NOSE_Z + 0.15), (0.29, NOSE_Z + 0.65), (0.22, NOSE_Z + 1.15), (0.13, NOSE_Z + 1.6), (0.05, NOSE_Z + 1.9),
            (0.025, NOSE_Z + 2.0), (0.0, NOSE_Z + 2.0)]
    lathe(bm, [(MOTOR_R, MOTOR_Z0), (MOTOR_R, NOSE_Z)] + nose, 24, mif=paint, sharp=30)
    ring(bm, MOTOR_R + 0.005, NOSE_Z, 0.02, 0.1, segs=24)
    ring(bm, MOTOR_R + 0.005, 8.0, 0.02, 0.07, segs=24)
    # --- канарды (2 шт., противоположные) и Q-шар
    for az in (0, 180):
        fin(bm, az, [(0.15, 11.85), (0.52, 11.95), (0.52, 12.15), (0.15, 12.35)], 0.03, H)
    sphere(bm, 0.07, 13.03, B, segs=12, k=6)


# ------------------------------------------------------------------ Apollo_SLA_Half
SLA_R0, SLA_R1, SLA_H, SLA_W = 3.30, 1.96, 8.5, 0.03  # низ/верх/высота/стенка. Верх SLA = низ КМ (1.96 = CM_R_BASE)


def sla_r(z):
    return SLA_R0 + (SLA_R1 - SLA_R0) * z / SLA_H


def arc_lathe(bm, prof, a0=90.0, a1=270.0, segs=48, mat=H, mif0=None):
    """Замкнутый профиль (r, z), против часовой, развёрнутый на дугу a0..a1 (не полный оборот) + плоские торцы по разрезу.
    mif0(az, zmid) задаёт слот только для профильного сегмента 0 (наружная стенка) — швы."""
    angs = [math.radians(a0 + (a1 - a0) * j / segs) for j in range(segs + 1)]
    rings = [[bm.verts.new((r * math.cos(a), r * math.sin(a), z)) for a in angs] for r, z in prof]
    n = len(prof)
    for k in range(n):
        A, Bv = rings[k], rings[(k + 1) % n]
        zm = 0.5 * (prof[k][1] + prof[(k + 1) % n][1])
        for j in range(segs):
            f = bm.faces.new((A[j], A[j + 1], Bv[j + 1], Bv[j]))
            if mif0 and k == 0:
                f.material_index = mif0(a0 + (a1 - a0) * (j + 0.5) / segs, zm)
            else:
                f.material_index = mat
            f.smooth = True
    # торцы: нормаль наружу от тела (начало — против касательной, конец — по касательной)
    for j, sgn in ((0, -1.0), (segs, 1.0)):
        a = angs[j]
        want = Vector((-math.sin(a), math.cos(a), 0.0)) * sgn
        f = bm.faces.new([rings[k][j] for k in range(n)])
        f.normal_update()
        if f.normal.dot(want) < 0:
            f.normal_flip()
        f.material_index = mat
        f.smooth = False
    # острые рёбра: все дуговые рёбра (углы профиля 90°) и границы торцов
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


def b_sla_half(bm):
    zb, zt = 0.3, 8.3                                              # стенка между шпангоутами; сами пояса закрывают концы
    seam = lambda az, zm: B if (abs(az - 133.125) < 1 or abs(az - 226.875) < 1) else H
    arc_lathe(bm, [(sla_r(zb), zb), (sla_r(zt), zt), (sla_r(zt) - SLA_W, zt), (sla_r(zb) - SLA_W, zb)], mat=H, mif0=seam)
    # пояса: низ (R 3.30 ровно на z=0) и верх (чуть шире 1.96 — фланец стыка с КМ)
    arc_lathe(bm, [(SLA_R0, 0.0), (SLA_R0, zb), (SLA_R0 - 0.10, zb), (SLA_R0 - 0.10, 0.0)], mat=M)
    arc_lathe(bm, [(1.985, zt), (1.985, SLA_H), (1.86, SLA_H), (1.86, zt)], mat=M)
    # два промежуточных шпангоута изнутри
    for z in (2.9, 5.6):
        ro = sla_r(z) - SLA_W
        arc_lathe(bm, [(ro, z - 0.05), (ro, z + 0.05), (ro - 0.12, z + 0.05), (ro - 0.12, z - 0.05)], mat=M)
    # шарнирные узлы створки у основания (тёмные блоки, слот Nozzle)
    for az in (120.0, 240.0):
        x, y = polar(SLA_R0 - 0.15, az)
        box(bm, (x, y, 0.35), (0.3, 0.3, 0.3), mat=N, az=az)


# ------------------------------------------------------------------ запуск
def frame(names, loc, dist, pitch, yaw):
    """Камера вьюпорта на объект: isolate + поворот (для скриншота)."""
    isolate(names)
    q = __import__("mathutils").Euler((math.radians(pitch), 0.0, math.radians(yaw)), 'XYZ').to_quaternion()
    for win in bpy.context.window_manager.windows:
        for area in win.screen.areas:
            if area.type == 'VIEW_3D':
                r3 = area.spaces.active.region_3d
                r3.view_perspective = 'PERSP'
                r3.view_rotation = q
                r3.view_location = loc
                r3.view_distance = dist


def main(do_export=True):
    out = []
    for name, fn in (("Apollo_LES", b_apollo_les), ("Apollo_SLA_Half", b_sla_half)):
        out.append(build(name, fn))
        if do_export:
            export(name)
    return out


res = main()
print(res)
