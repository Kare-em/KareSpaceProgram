# Крылатые системы (WingedRockets.cs): орбитер «Шаттла», внешний бак ET, ускоритель SRB, «Буран», блок Ц и блок А
# «Энергии», стойка шасси. Тем же стилем, что station_parts.py.
# Запуск (print виден только так, см. docs/pitfalls-tools.md):
#   "/c/Program Files/Blender Foundation/Blender 5.2/blender.exe" -b Tools/blender/parts.blend --python Tools/blender/winged_parts.py < /dev/null
# Ось +Z (в Unity +Y — нос), начало — днище секции, метры, натуральная величина: L/Ø — из WingedRockets.cs.
# Оси орбитера — как у WingMesh: брюхо к −X Blender (= +X Unity, к баку), верх и киль к +X Blender, размах по ±Y.
# Слоты 0..3 заняты у каждой модели все (иначе Unity схлопывает пустой и палитра VesselView.CraftPalette съезжает).
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
    before = set(bm.verts)
    fn()
    new = [v for v in bm.verts if v not in before]
    bmesh.ops.transform(bm, matrix=m, verts=new)


TO_Y = Matrix.Rotation(math.radians(-90), 4, 'X')     # +Z -> +Y


def new_faces(bm, fn):
    before = set(bm.faces)
    fn()
    return [f for f in bm.faces if f not in before]


def ogive(r0, z0, L, n=10, tip=0.0):
    """Касательная оживальная головная часть: от (r0, z0) до острия z0+L ([(r, z)] снизу вверх, без оси)."""
    rho = (r0 * r0 + L * L) / (2 * r0)
    out = []
    for i in range(n + 1):
        x = L * (1 - i / n)                       # расстояние от острия
        r = math.sqrt(max(rho * rho - (L - x) ** 2, 0)) + r0 - rho
        out.append((max(r, tip), z0 + L - x))
    return out


# ---------------------------------------------------------------- орбитер (общий конструктор «Шаттл»/«Буран»)

def sect(w, xb, xt, k=28):
    """Сечение фюзеляжа: плоское брюхо (суперэллипс n=6) снизу у x=xb, скруглённый верх (n=2.5) до xt, полуширина w.
    Самое широкое место — на 40 % высоты от брюха (боковые кромки-«чайны» орбитера)."""
    xm = xb + 0.4 * (xt - xb)
    pts = []
    for j in range(k):
        t = 2 * math.pi * j / k
        c, s = math.cos(t), math.sin(t)
        y = w * math.copysign(abs(s) ** (2 / 2.5 if c >= 0 else 2 / 6.0), s)
        x = xm + (xt - xm) * abs(c) ** (2 / 2.5) if c >= 0 else xm - (xm - xb) * abs(c) ** (2 / 6.0)
        pts.append((x, y))
    return pts


def fuselage(bm, stations, tip, base_mat=N, nose_z=1e9, win=(0, 0)):
    """Лофт по станциям [(z, w, xb, xt)] от днища к носу; tip=(x, z) — острие носа.
    Раскраска по нормали: вниз (−X) — чёрные плитки, иначе белые; нос (z > nose_z) — серый RCC; полоса win — остекление."""
    rings = [[bm.verts.new((x, y, z)) for x, y in sect(w, xb, xt)] for z, w, xb, xt in stations]
    k = len(rings[0])
    fs = []
    for a, b in zip(rings, rings[1:]):
        for j in range(k):
            j2 = (j + 1) % k
            fs.append(bm.faces.new((a[j], a[j2], b[j2], b[j])))
    vt = bm.verts.new((tip[0], 0.0, tip[1]))
    for j in range(k):
        fs.append(bm.faces.new((rings[-1][j], rings[-1][(j + 1) % k], vt)))
    cap = bm.faces.new(list(reversed(rings[0])))
    fs.append(cap)
    bmesh.ops.recalc_face_normals(bm, faces=fs)
    for f in fs:
        f.normal_update()
        n = f.normal
        zc = f.calc_center_median().z
        if f is cap:
            f.material_index = base_mat
        elif zc > nose_z:
            f.material_index = M
        elif win[0] < zc < win[1] and n.x > 0.25 and n.z > 0.15:
            f.material_index = B            # остекление кабины в чёрной окантовке
        elif n.x < -0.3:
            f.material_index = B
        else:
            f.material_index = H
        f.smooth = True
    # Острое ребро по донному срезу, иначе сглаживание тянет нормаль торца на борт.
    for e in cap.edges:
        e.smooth = False


def wing(bm, outline, x_root, dihedral, t_root, t_tip, y_root):
    """Двойная дельта одной плитой на каждую сторону: низ — продолжение брюха с поперечным V, толщина от t_root к t_tip.
    outline — контур [(y, z)] правой консоли. Передняя кромка (нормаль вперёд) — серый RCC, низ — чёрный, верх — белый."""
    ymax = max(p[0] for p in outline)
    tg = math.tan(math.radians(dihedral))
    for sgn in (1, -1):
        bot, top = [], []
        for y, z in outline:
            u = (y - y_root) / (ymax - y_root)
            xb = x_root + (y - y_root) * tg
            t = t_root + (t_tip - t_root) * u
            bot.append(bm.verts.new((xb, sgn * y, z)))
            top.append(bm.verts.new((xb + t, sgn * y, z)))
        n = len(outline)
        fs = [bm.faces.new(bot), bm.faces.new(list(reversed(top)))]
        for i in range(n):
            i2 = (i + 1) % n
            fs.append(bm.faces.new((bot[i], top[i], top[i2], bot[i2])))
        bmesh.ops.recalc_face_normals(bm, faces=fs)
        res = bmesh.ops.triangulate(bm, faces=fs[:2], quad_method='BEAUTY', ngon_method='BEAUTY')
        fs = fs[2:] + res['faces']
        for f in fs:
            f.normal_update()
            nx, nz = f.normal.x, f.normal.z
            f.material_index = B if nx < -0.5 else (H if nx > 0.5 else (M if nz > 0.3 else B))
            f.smooth = False


def bay_doors(bm, z0, z1, xt, w, panels=5):
    """Створки грузового отсека: осевой шов и петлевые линии по бортам + поперечные швы (серые полоски на белом)."""
    box(bm, (xt + 0.01, 0, (z0 + z1) / 2), (0.05, 0.08, z1 - z0), M)
    xm = -w + 0.4 * 2 * w
    yh = 0.885 * w
    xh = xm + (xt - xm) * 0.587          # точка сечения sect() на y = 0,885 w (n=2.5 сверху)
    for s in (1, -1):
        box(bm, (xh, s * yh, (z0 + z1) / 2), (0.06, 0.06, z1 - z0), M)
    for i in range(1, panels):
        z = z0 + (z1 - z0) * i / panels
        for s in (1, -1):
            # поперечный шов — двумя наклонными брусками по скруглению верха
            transformed(bm, lambda: box(bm, (0, 0, 0), (0.05, yh, 0.06), M),
                        Matrix.Translation(((xt + xh) / 2, s * yh / 2, z)) @
                        Matrix.Rotation(math.atan2(s * (xt - xh), yh), 4, 'Z'))


# Пятый слот орбитеров — остекление (VesselView.GlassColor: гладкое, тёмное, с отражениями). Только у орбитеров:
# у остальных моделей слотов 4, палитры CraftPalette на них рассчитаны.
G = 4
GLASS_SLOT = ("Glass", (0.02, 0.025, 0.03))


def surf_pt(st, z, t):
    """Точка поверхности фюзеляжа по станциям st на высоте z, параметр сечения t (рад, как в sect: 0 — верх по
    оси, ±π/2 — борта). Станции интерполируются линейно — так же лофтит fuselage, отличие только в прогибе хорды."""
    for a, b in zip(st, st[1:]):
        if a[0] <= z <= b[0]:
            f = (z - a[0]) / (b[0] - a[0])
            w, xb, xt = (a[k] + (b[k] - a[k]) * f for k in (1, 2, 3))
            break
    else:
        raise ValueError(z)
    xm = xb + 0.4 * (xt - xb)
    c, s = math.cos(t), math.sin(t)
    y = w * math.copysign(abs(s) ** (2 / 2.5 if c >= 0 else 2 / 6.0), s)
    x = xm + (xt - xm) * abs(c) ** (2 / 2.5) if c >= 0 else xm - (xm - xb) * abs(c) ** (2 / 6.0)
    return Vector((x, y, z)), Vector((xm, 0.0, z))


def patch(bm, st, z0, z1, t0, t1, off, mat, nz=3, nt=4):
    """Лоскут по поверхности фюзеляжа (z0..z1, t0..t1 в градусах), поднятый по нормали на off — рамы и стёкла
    окон. Подъём больше прогиба граней лофта (≈2 см при k=28), иначе лоскут тонет в обшивке."""
    e = 1e-3
    grid = []
    for i in range(nz + 1):
        z = z0 + (z1 - z0) * i / nz
        row = []
        for j in range(nt + 1):
            t = math.radians(t0 + (t1 - t0) * j / nt)
            p, axis = surf_pt(st, z, t)
            dz = surf_pt(st, min(z + e, st[-1][0]), t)[0] - surf_pt(st, max(z - e, st[0][0]), t)[0]
            dt = surf_pt(st, z, t + e)[0] - surf_pt(st, z, t - e)[0]
            n = dt.cross(dz).normalized()
            if n.dot(p - axis) < 0:
                n = -n
            row.append(bm.verts.new(p + n * off))
        grid.append(row)
    for i in range(nz):
        for j in range(nt):
            f = bm.faces.new((grid[i][j], grid[i][j + 1], grid[i + 1][j + 1], grid[i + 1][j]))
            f.normal_update()
            c = f.calc_center_median()
            if f.normal.dot(c - surf_pt(st, c.z, 0)[1]) < 0:      # наружу от оси сечения
                f.normal_flip()
            f.material_index = mat
            f.smooth = False


def windows(bm, st, front, top, side):
    """Остекление кабины орбитера: 6 лобовых, 2 верхних (над командиром и пилотом), по боковому на борт.
    front/top/side — (z0, z1, t0, t1) группы; рама — чёрная окантовка (слот B) на 6 см шире, стёкла — слот G,
    перемычки между стёклами — рама в зазорах."""
    FRAME, GAP = 2.0, 1.6          # поле рамы и перемычки, градусы сечения
    def group(z0, z1, t0, t1, panes):
        patch(bm, st, z0 - 0.08, z1 + 0.08, t0 - FRAME, t1 + FRAME, 0.03, B)
        w = (t1 - t0 - GAP * (panes - 1)) / panes
        for k in range(panes):
            a = t0 + k * (w + GAP)
            patch(bm, st, z0, z1, a, a + w, 0.045, G, nz=2, nt=2)
    group(*front, 6)
    z0, z1, t0, t1 = top
    for sgn in (1, -1):
        a, b = sorted((sgn * t0, sgn * t1))
        group(z0, z1, a, b, 1)
    z0, z1, t0, t1 = side
    for sgn in (1, -1):
        a, b = sorted((sgn * t0, sgn * t1))
        group(z0, z1, a, b, 1)


def cut_fin(bm, pts, t):
    """Киль с вырезом под руль-тормоз: контур невыпуклый — грани триангулируем здесь, иначе импорт FBX в Unity
    режет n-угольник веером и закрывает вырез."""
    fs = new_faces(bm, lambda: fin(bm, 0, pts, t, H))
    bmesh.ops.triangulate(bm, faces=[f for f in fs if len(f.verts) > 4], quad_method='BEAUTY', ngon_method='BEAUTY')


def b_shuttle(bm):
    """Орбитер «Колумбия» 37,24 × 23,79 м, Ø фюзеляжа 5,2 (секция Orbiter в WingedRockets, r = 2,6 — брюхо на x = −2,6).
    Слоты: 0 белые плитки, 1 чёрные плитки/остекление, 2 RCC (нос, передние кромки) и швы, 3 сопла SSME/OMS."""
    st = [(2.6, 2.75, -2.6, 2.3), (6.0, 2.7, -2.6, 2.6), (10.0, 2.6, -2.6, 2.6), (27.0, 2.6, -2.6, 2.6),
          (28.6, 2.6, -2.6, 3.05), (30.8, 2.5, -2.55, 3.25), (32.4, 2.3, -2.42, 2.65), (33.9, 1.95, -2.2, 1.65),
          (35.3, 1.45, -1.9, 0.75), (36.5, 0.8, -1.55, -0.1)]
    fuselage(bm, st, (-1.2, 37.24), nose_z=36.6)
    windows(bm, st, front=(31.9, 32.95, -66, 66), top=(30.45, 31.25, 4, 18), side=(31.05, 31.6, 70, 84))
    # Двойная дельта: наплыв 81° от корня к излому на y 4,6, крыло 45° к законцовке на полуразмахе 11,9 (§ GDD крылья).
    # Задняя кромка вырезана на хорду элевонов 1,8 м до y 11,3 — элевоны отдельно, процедурные (WingedRockets.
    # ShuttleSurfaces, шарниры по этой линии): правишь контур — правь шарниры там.
    wing(bm, [(2.0, 25.0), (4.6, 15.2), (11.9, 7.7), (11.9, 4.4), (11.3, 4.246), (11.3, 6.046), (8.0, 5.2), (2.0, 4.4)],
         x_root=-2.55, dihedral=3.5, t_root=1.1, t_tip=0.25, y_root=2.0)
    # Киль 8 м над фюзеляжем, стреловидность 45°; руль-тормоз вырезан (u 2,9…10,4) и рисуется створками в Unity.
    cut_fin(bm, [(2.2, 2.4), (2.4, 12.4), (10.6, 4.4), (10.6, 1.2), (10.4, 1.229), (10.4, 2.529), (2.9, 4.7),
                 (2.9, 2.3)], 0.45)
    bay_doors(bm, 10.0, 27.0, 2.6, 2.6)
    # Гондолы OMS по бокам киля.
    for s in (1, -1):
        lathe(bm, [(0.0, 2.4), (0.95, 2.4), (1.1, 3.6), (1.1, 7.6), (0.7, 10.2), (0.0, 11.0)], 20, H, c=(1.6, s * 1.9))
        bell(bm, (1.6, s * 1.9), 1.7, 1.2, 0.4, segs=16)
    # Три SSME: верхний по оси симметрии, два нижних (срез 2,3 м; на z = 0 — низ секции).
    for c in ((1.0, 0.0), (-0.95, 1.35), (-0.95, -1.35)):
        bell(bm, c, 0.0, 3.2, 1.12, segs=24)
    # Балансировочный щиток под двигателями и элевоны — процедурные створки на шарнирах (VesselView.Controls).
    for s in (1, -1):                                                 # двери ниш шасси (чёрный чуть темнее не нужен — шов)
        box(bm, (-2.62, s * 3.4, 13.0), (0.04, 0.9, 3.4), M)
    box(bm, (-2.45, 0, 31.3), (0.04, 0.6, 1.6), M)


def b_buran(bm):
    """«Буран» 36,37 × 23,92 м, Ø 5,6 (r = 2,8). Без маршевых, только 2 сопла ОДУ у основания киля; тормозной парашют.
    Крыло — тоже двойная дельта, но наплыв уже и длиннее (78°), консоль 45°. Слоты как у b_shuttle."""
    st = [(1.2, 2.8, -2.8, 2.5), (8.0, 2.8, -2.8, 2.8), (26.0, 2.8, -2.8, 2.8), (28.4, 2.8, -2.8, 3.3),
          (30.4, 2.7, -2.72, 3.45), (32.0, 2.45, -2.55, 2.75), (33.6, 2.05, -2.3, 1.75), (35.0, 1.45, -1.95, 0.75),
          (35.9, 0.85, -1.65, -0.05)]
    fuselage(bm, st, (-1.05, 36.37), nose_z=35.7)
    windows(bm, st, front=(31.5, 32.55, -66, 66), top=(30.05, 30.85, 4, 18), side=(30.65, 31.2, 70, 84))
    # Вырез задней кромки под элевоны (хорда 1,8 до y 11,4) — пара с WingedRockets.BuranSurfaces.
    wing(bm, [(2.2, 24.5), (5.0, 14.2), (11.96, 7.24), (11.96, 4.3), (11.4, 4.074), (11.4, 5.874), (8.0, 4.5),
              (2.2, 3.3)], x_root=-2.75, dihedral=5.0, t_root=1.15, t_tip=0.25, y_root=2.2)
    # Руль-тормоз вырезан выше контейнера парашютов (u 3,6…10,6).
    cut_fin(bm, [(2.4, 1.0), (2.6, 12.0), (10.8, 3.8), (10.8, 0.6), (10.6, 0.6095), (10.6, 1.9095), (3.6, 3.293),
                 (3.6, 0.943)], 0.45)
    bay_doors(bm, 9.0, 26.5, 2.8, 2.8)
    # Контейнер тормозного парашюта в основании киля (отличие от «Шаттла») и 2 ОДУ под ним.
    lathe(bm, [(0.0, 0.9), (0.55, 0.9), (0.6, 1.3), (0.6, 3.6), (0.0, 4.4)], 16, M, c=(2.85, 0))
    for s in (1, -1):
        bell(bm, (1.2, s * 0.95), 0.0, 1.4, 0.42, segs=16)     # срез на z = 0 — низ секции
    # Блоки двигателей ориентации по бокам кормы (вместо гондол OMS).
    for s in (1, -1):
        box(bm, (1.6, s * 2.55, 2.3), (1.4, 0.7, 2.2), H)
    # Щиток и элевоны — створками в Unity (WingedRockets.BuranSurfaces).
    for s in (1, -1):
        box(bm, (-2.82, s * 3.6, 12.5), (0.04, 0.9, 3.4), M)
    box(bm, (-2.6, 0, 30.4), (0.04, 0.6, 1.6), M)


# ---------------------------------------------------------------- носители

def b_et(bm):
    """Внешний бак ET 47 × Ø8,4. Слоты: 0 оранжевая пена, 1 тёмная пена межбакового отсека и магистралей,
    2 металл (узлы крепления), 3 тёмные плиты разъёмов. Орбитер — на +X Blender (−X Unity, Beside), SRB — на ±Y."""
    r = 4.2
    zi0, zi1 = 29.5, 36.4
    prof = [(0.0, 0.0), (1.6, 0.12), (2.8, 0.45), (3.6, 0.95), (4.05, 1.6), (r, 2.4), (r, zi0), (r + 0.02, zi0),
            (r + 0.02, zi1), (r, zi1)] + ogive(r, zi1, 46.3 - zi1, 12, tip=0.35)[1:] + [(0.2, 46.6), (0.05, 47.0), (0.0, 47.0)]
    lathe(bm, prof, 48, mif=lambda az, z: B if zi0 < z < zi1 else H, sharp=30)
    for z in (zi0 + 1.2, zi0 + 3.4, zi1 - 1.2):                     # гофры межбакового
        ring(bm, r + 0.04, z, 0.08, 0.25, B)
    # Магистраль LO2 по борту бака со стороны орбитера, кабельный короб и линия наддува.
    strut(bm, (r + 0.3, 1.3, 2.0), (r + 0.3, 1.3, zi0 + 0.5), 0.22, B, 10)
    strut(bm, (r + 0.15, -0.9, 3.0), (r + 0.15, -0.9, 44.0), 0.1, B, 6)
    strut(bm, (r + 0.25, -1.6, 2.5), (r + 0.25, -1.6, zi0), 0.12, B, 6)
    # Узлы орбитера: передний «бипод» и задние плиты разъёмов.
    for s in (1, -1):
        strut(bm, (r - 0.1, s * 0.9, zi0 + 1.5), (r + 0.55, s * 0.25, zi0 + 2.8), 0.12, M, 8)
        box(bm, (r + 0.15, s * 1.3, 3.6), (0.35, 1.1, 1.6), N)
        strut(bm, (r - 0.1, s * 1.3, 4.6), (r + 0.5, s * 1.3, 5.6), 0.14, M, 8)
    # Узлы ускорителей SRB (±Y): балка в межбаковом и задний пояс.
    for s in (1, -1):
        box(bm, (0, s * (r + 0.2), zi0 + 3.0), (1.2, 0.5, 0.9), M)
        box(bm, (0, s * (r + 0.15), 4.2), (0.9, 0.4, 0.7), M)
    box(bm, (r + 0.25, 0, zi1 - 0.3), (0.4, 0.6, 0.5), N)


def b_srb(bm):
    """Твердотопливный ускоритель SRB 45,5 × Ø3,7. Слоты: 0 белый, 1 чёрный (РДТТ отделения), 2 металл (стыки,
    узлы к ET), 3 сопло и донный экран. Ускоритель радиальный: его +X Unity — наружу, значит к баку — +X Blender.
    Юбку держим в r ≤ 2,3 (настоящая Ø5,2), иначе пересекается с ET при RadialOffset 6,05."""
    r = 1.85
    prof = [(2.25, 1.0), (2.3, 1.0), (2.3, 1.6), (r, 4.4), (r, 39.5), (r + 0.02, 39.5), (r + 0.02, 41.5),
            (r, 41.5), (1.25, 43.6), (0.75, 44.8), (0.35, 45.35), (0.0, 45.5)]
    lathe(bm, prof, 40, H, sharp=25)
    lathe(bm, [(0.0, 2.4), (2.25, 1.0)], 40, N)                      # донный экран внутри юбки
    bell(bm, (0, 0), 0.0, 3.4, 1.45, segs=28)
    for z in (4.4, 12.0, 20.4, 28.8, 37.0, 39.5):                    # заводские и монтажные стыки сегментов
        ring(bm, r + 0.03, z, 0.07, 0.3, M, 40)
    ring(bm, r + 0.05, 5.4, 0.12, 0.5, M, 40)                        # задний пояс крепления к ET
    for k in range(4):                                               # РДТТ отделения: на носу и на юбке
        x, y = polar(1.15, 45 + 90 * k)
        transformed(bm, lambda: box(bm, (0, 0, 0), (0.25, 0.35, 1.0), B),
                    Matrix.Translation((x, y, 43.1)) @ Matrix.Rotation(math.radians(45 + 90 * k), 4, 'Z') @
                    Matrix.Rotation(math.radians(-28), 4, 'Y'))
        x, y = polar(2.05, 45 + 90 * k)
        box(bm, (x, y, 2.6), (0.3, 0.3, 1.0), B, az=45 + 90 * k)
    box(bm, (r + 0.15, 0, 5.4), (0.4, 0.7, 0.8), M)                  # задний узел к баку
    box(bm, (r + 0.2, 0, 40.4), (0.45, 0.8, 1.2), M)                 # передний шар-шарнир
    box(bm, (-(r + 0.08), 0, 22.0), (0.16, 0.35, 34.0), H)            # туннель систем снаружи


def b_energia_core(bm):
    """Блок Ц «Энергии» 58,8 × Ø7,75, 4 РД-0120. Слоты: 0 оранжевая теплоизоляция, 1 тёмные пояса, 2 металл, 3 сопла.
    «Буран» — на +X Blender (Beside), блоки А — по диагоналям (фаза π/4)."""
    r = 3.875
    zi0, zi1 = 37.5, 41.5
    prof = [(3.5, 3.2), (3.7, 3.2), (r, 4.4), (r, zi0), (r + 0.02, zi0), (r + 0.02, zi1), (r, zi1),
            (r, 50.0)] + ogive(r, 50.0, 8.8, 12, tip=0.3)[1:] + [(0.0, 58.8)]
    lathe(bm, prof, 48, mif=lambda az, z: B if zi0 < z < zi1 or z < 4.4 else H, sharp=25)
    lathe(bm, [(0.0, 3.6), (3.5, 3.2)], 48, N)                       # донный экран
    for k in range(4):
        bell(bm, polar(2.0, 45 + 90 * k), 0.0, 4.5, 1.2, segs=24)
    for z in (6.5, 20.0, 33.0, 49.5):
        ring(bm, r + 0.03, z, 0.07, 0.25, M)
    strut(bm, (r + 0.25, 1.0, 5.0), (r + 0.25, 1.0, zi0), 0.25, B, 10)   # магистраль/кабельный короб к «Бурану»
    for s in (1, -1):                                                 # узлы «Бурана»
        strut(bm, (r - 0.1, s * 0.9, 44.0), (r + 0.6, s * 0.3, 45.5), 0.13, M, 8)
        box(bm, (r + 0.15, s * 1.3, 6.0), (0.35, 1.1, 1.6), N)
    for k in range(4):                                                # узлы блоков А
        for z in (6.0, 40.0):
            x, y = polar(r + 0.15, 45 + 90 * k)
            box(bm, (x, y, z), (0.4, 0.8, 0.8), M, az=45 + 90 * k)


def b_energia_a(bm):
    """Боковой блок А 38,3 × Ø3,92, РД-170 (4 камеры). Слоты: 0 белый, 1 тёмные пояса, 2 металл, 3 сопла.
    Радиальный: к блоку Ц — +X Blender."""
    r = 1.96
    prof = [(1.6, 1.8), (1.75, 1.8), (r, 2.8), (r, 21.5), (r + 0.02, 21.5), (r + 0.02, 24.0), (r, 24.0),
            (r, 33.4), (1.4, 35.9), (0.9, 37.4), (0.45, 38.1), (0.0, 38.3)]
    lathe(bm, prof, 36, mif=lambda az, z: B if 21.5 < z < 24.0 or 33.4 < z < 35.0 else H, sharp=25)
    lathe(bm, [(0.0, 2.2), (1.6, 1.8)], 36, N)                       # донный экран
    for k in range(4):
        bell(bm, polar(0.98, 45 + 90 * k), 0.0, 2.3, 0.68, segs=20)
    for z in (2.8, 12.0, 30.5):
        ring(bm, r + 0.03, z, 0.06, 0.22, M, 36)
    for k in range(4):                                                # аэродинамические щитки у хвоста
        x, y = polar(r + 0.15, 90 * k)
        box(bm, (x, y, 3.4), (0.3, 0.6, 2.2), B, az=90 * k)
    box(bm, (r + 0.15, 0, 4.5), (0.4, 0.8, 0.8), M)
    box(bm, (r + 0.15, 0, 34.0), (0.4, 0.8, 0.8), M)
    strut(bm, (-(r + 0.12), 0.0, 3.5), (-(r + 0.12), 0.0, 33.0), 0.12, M, 6)


def b_gear(bm):
    """Стойка шасси, универсальная: начало — шарнир, стойка вдоль −X Blender (= +X Unity, наружу из брюха), ось колёс
    по Y (размах). От шарнира до низа колёс 2,4 м = 1,8 (GearHeight) + 0,6 заглубления шарнира — пара с
    VesselView.GearModelReach/GearInset. Слоты: 0 корпус стойки, 1 шины, 2 шток амортизатора и ось, 3 диски."""
    wr = 0.56
    ax = -(2.4 - wr)
    strut(bm, (0.05, 0, 0), (-1.05, 0, 0), 0.16, H, 12)
    strut(bm, (-1.0, 0, 0), (ax, 0, 0), 0.1, M, 10)
    strut(bm, (ax, -0.62, 0), (ax, 0.62, 0), 0.07, M, 8)
    strut(bm, (-0.95, 0, 0.0), (-0.1, 0, 1.0), 0.07, H, 8)            # подкос к носу — после складывания уходит внутрь
    box(bm, (0, 0, 0), (0.3, 0.5, 0.3), H)                            # проушина шарнира
    box(bm, (-1.05, 0, 0.12), (0.12, 0.08, 0.3), M)                    # шлиц-шарнир
    for s in (1, -1):
        tire = [(0.3, -0.2), (0.48, -0.2), (wr, -0.1), (wr, 0.1), (0.48, 0.2), (0.3, 0.2)]
        transformed(bm, lambda: lathe(bm, tire, 20, B, closed=True, sharp=30),
                    Matrix.Translation((ax, s * 0.36, 0)) @ TO_Y)
        transformed(bm, lambda: lathe(bm, [(0.0, -0.17), (0.31, -0.17), (0.31, 0.17), (0.0, 0.17)], 16, N, sharp=30),
                    Matrix.Translation((ax, s * 0.36, 0)) @ TO_Y)


MODELS = [
    ("Shuttle_Orbiter", b_shuttle), ("Shuttle_ET", b_et), ("Shuttle_SRB", b_srb), ("Buran", b_buran),
    ("Energia_BlockTs", b_energia_core), ("Energia_BlockA", b_energia_a), ("Landing_Gear", b_gear),
]


def main(do_export=True, only=None):
    out = []
    for name, fn in MODELS:
        if only and name not in only:
            continue
        # Орбитерам — пятый слот «стекло» (build берёт SLOTS из общих глобалов hulls_lib).
        global SLOTS
        base = SLOTS
        if name in ("Shuttle_Orbiter", "Buran"):
            SLOTS = base + [GLASS_SLOT]
        try:
            out.append(build(name, fn))
        finally:
            SLOTS = base
        if do_export:
            export(name)
    return out


def sheet(path, names):
    """Лист деталей: Workbench, цвета материалов, детали в ряд по X, вид сбоку-сверху. 960×540 (Full HD / 2)."""
    sc = bpy.context.scene
    keep = [bpy.data.objects[n] for n in names]
    for o in sc.objects:
        o.hide_render = o not in keep
    x = 0.0
    for o in keep:
        w = max(o.dimensions.x, o.dimensions.y)
        o.location = (x + w / 2, 0, 0)
        o.rotation_euler = (0, 0, math.radians(-55))   # 3/4 сверху: видно план крыла, киль и брюхо сбоку
        x += w + 4.0
    cam = bpy.data.objects.get("SheetCam")
    if cam is None:
        cam = bpy.data.objects.new("SheetCam", bpy.data.cameras.new("SheetCam"))
        sc.collection.objects.link(cam)
    cam.data.type = 'ORTHO'
    cam.data.ortho_scale = max(x, 60.0 * 16 / 9) * 1.02
    cam.location = (x / 2, -150, 30 + 150 * math.tan(math.radians(10)))
    cam.rotation_euler = (math.radians(80), 0, 0)
    sc.camera = cam
    try:
        sc.render.engine = 'BLENDER_WORKBENCH'
    except TypeError:
        pass
    sh = sc.display.shading
    sh.light = 'STUDIO'
    sh.color_type = 'MATERIAL'
    sh.show_shadows = False
    sh.show_cavity = True
    sc.render.resolution_x, sc.render.resolution_y = 960, 540
    sc.render.resolution_percentage = 100
    sc.render.filepath = path
    bpy.ops.render.render(write_still=True)
    for o in keep:
        o.location = (0, 0, 0)
        o.rotation_euler = (0, 0, 0)
    for o in sc.objects:
        o.hide_render = False
    bpy.data.objects.remove(cam, do_unlink=True)


if __name__ == "__main__" or "WINGED_RUN" in os.environ:
    for r in main():
        print("WINGED", r)
    if os.environ.get("WINGED_SHEET"):
        sheet(os.environ["WINGED_SHEET"], [n for n, _ in MODELS])
    bpy.context.preferences.filepaths.save_version = 0
    bpy.ops.wm.save_mainfile()
