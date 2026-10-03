# Корпуса ступеней исторических ракет. Требует hulls_lib.py (lathe, bell, ... build/export).
# Начало — днище секции (z=0), натуральная величина = L/D из HistoricRockets.cs.
# Сопла верхних ступеней свисают ниже z=0 в открытую юбку нижней ступени (VesselView: ownBottom).
import math


def paint(bands=(), roll=(), base=H):
    def f(az, z):
        for z0, z1, s, o in roll:
            if z0 <= z < z1:
                return stripes(s, o)(az)
        for z0, z1, m in bands:
            if z0 <= z < z1:
                return m
        return base
    return f


def polar(r, az):
    a = math.radians(az)
    return (r * math.cos(a), r * math.sin(a))


def tail_bowl(bm, R, zd, zr, zs=0.0, w=0.03, segs=48, mat=B):
    """Открытая снизу юбка: купол бака (0,zd)->(R-w,zr), стенка вниз до zs, кромка."""
    lathe(bm, [(0, zd), (R - w, zr), (R - w, zs), (R, zs)], segs, mat)


def top_bowl(bm, R, ztop, zw, zd, w=0.03, segs=48, mat=B):
    """Открытая сверху юбка (переходник): кромка, стенка вниз до zw, купол бака вверх к оси до zd."""
    lathe(bm, [(R, ztop), (R - w, ztop), (R - w, zw), (0, zd)], segs, mat)


# ---------------- Redstone / Juno I ----------------
def redstone_tail(bm, R):
    tail_bowl(bm, R, 1.2, 1.0)
    for k in range(4):
        fin(bm, 45 + 90 * k, [(R - 0.02, 0.0), (R + 0.9, 0.0), (R + 0.9, 0.6), (R - 0.02, 2.4)], 0.08)
    bell(bm, (0, 0), 0.05, 1.5, 0.4)
    ring(bm, R + 0.005, 3.0, 0.03, 0.08)


def b_redstone(bm):
    R, L = 0.89, 17.7
    redstone_tail(bm, R)
    lathe(bm, [(R, 0), (R, 3.0), (R, 12.0), (R, L - 0.3), (R - 0.05, L), (0, L)], 48,
          mif=paint(roll=[(0, 3.0, 4, 0)]))
    ring(bm, R + 0.005, 12.0, 0.03, 0.08)


def b_juno1(bm):
    R, L = 0.89, 21.0
    redstone_tail(bm, R)
    lathe(bm, [(R, 0), (R, 3.0), (R, 19.6), (0.5, 20.6), (0.5, L), (0, L)], 48,
          mif=paint(roll=[(0, 3.0, 4, 0)], bands=[(20.6, L + 0.01, M)]))
    ring(bm, R + 0.005, 19.6, 0.03, 0.08)
    ring(bm, 0.5, 20.92, 0.06, 0.1)


def cluster(bm, n, rc, L, rtub, rt=0.076):
    """Связка «Сарджентов» во вращающейся «ванне»."""
    for k in range(n):
        c = polar(rc, 360.0 * k / n)
        lathe(bm, [(0.045, 0.12), (rt, 0.12), (rt, L - 0.04), (rt - 0.02, L), (0, L)], 12, H, c=c)
        bell(bm, c, 0.0, 0.22, 0.055, segs=12)
    lathe(bm, [(rtub, L - 0.1), (0.0, L - 0.1)], 24, B)  # все 4 слота должны быть заняты, иначе FBX сдвинет субмеши
    ring(bm, rtub - 0.015, 0.2, 0.03, 0.08)
    ring(bm, rtub - 0.015, L - 0.2, 0.03, 0.08)


def b_cluster11(bm):
    cluster(bm, 11, 0.3, 1.3, 0.38)


def b_cluster3(bm):
    cluster(bm, 3, 0.12, 1.0, 0.21)


# ---------------- Atlas ----------------
def b_atlas_booster(bm):
    R = 1.525
    lathe(bm, [(R - 0.03, 4.0), (R - 0.03, 2.4), (1.59, 1.4), (1.59, 0.9), (1.62, 0.9), (1.62, 1.4), (R, 2.4), (R, 4.0)],
          48, H, closed=True)
    lathe(bm, [(0.85, 1.0), (1.59, 1.0)], 48, B)
    for s in (1, -1):
        c = (s * 1.45, 0.0)
        lathe(bm, [(0, 1.1), (0.65, 0.9), (0.65, 2.6), (0.3, 3.5), (0, 3.6)], 32, c=c, mif=paint(bands=[(0, 1.05, B)]))
        bell(bm, c, 0.0, 2.2, 0.55)
    ring(bm, R, 3.95, 0.05, 0.1)


def b_atlas_sus(bm, top):
    R, L = 1.525, 20.0
    lathe(bm, [(0, 0.5), (0.8, 0.35), (R, 0.0)], 48, B)
    bell(bm, (0, 0), -2.6, 3.0, 0.5)
    for s in (1, -1):
        bell(bm, (0, s * (R + 0.14)), 0.0, 0.6, 0.12, segs=16)
        box(bm, (0, s * (R + 0.12), 0.75), (0.34, 0.26, 0.7), M)
    box(bm, (R + 0.04, 0, 9.0), (0.12, 0.3, 16.0), M)
    ring(bm, R, 0.1, 0.04, 0.12)
    if top == "mercury":
        lathe(bm, [(R, 0), (R, 16.5), (0.95, 19.6), (0.95, L), (0, L)], 48, mif=paint(bands=[(19.6, L + 0.01, M)]))
    elif top == "agena":
        lathe(bm, [(R, 0), (R, 17.0), (0.8, 19.2), (0.8, L), (0.77, L)], 48, mif=paint(bands=[(19.2, L + 0.01, M)]))
        lathe(bm, [(0.77, L), (0.77, 18.0), (0, 17.8)], 48, B)
    else:  # centaur
        lathe(bm, [(R, 0), (R, L)], 48, H)
        top_bowl(bm, R, L, 18.2, 17.8, mat=B)
        ring(bm, R, L - 0.06, 0.05, 0.12)


def b_atlas_mercury(bm):
    b_atlas_sus(bm, "mercury")


def b_atlas_agena(bm):
    b_atlas_sus(bm, "agena")


def b_atlas_centaur(bm):
    b_atlas_sus(bm, "centaur")


def b_agena(bm):
    R, L = 0.76, 7.6
    bell(bm, (0, 0), -1.6, 2.0, 0.42)
    lathe(bm, [(0, 0.25), (R, 0.0)], 32, B)
    lathe(bm, [(R, 0), (R, 0.9), (R, 5.9), (R, L - 0.25), (R - 0.15, L), (0, L)], 48,
          mif=paint(bands=[(0, 0.91, B), (5.9, L + 0.01, M)]))
    for s in (1, -1):
        box(bm, (0, s * (R + 0.07), 0.55), (0.16, 0.14, 0.7), M)
    ring(bm, R, 5.9, 0.03, 0.08)


def b_centaur(bm):
    R, L = 1.525, 9.1
    for s in (1, -1):
        bell(bm, (s * 0.6, 0), -1.4, 1.8, 0.45)
    lathe(bm, [(0, 0.0), (0.9, 0.06), (1.3, 0.25), (R, 0.6)], 48, B)
    lathe(bm, [(R, 0.6), (R, 8.3), (R - 0.25, 8.8), (R - 0.25, L), (0, L)], 48, mif=paint(bands=[(8.3, L + 0.01, M)]))
    for z in (0.6, 4.4, 8.3):
        ring(bm, R, z, 0.03, 0.08)


# ---------------- Titan II ----------------
def b_titan1(bm):
    R, L = 1.525, 21.0
    for s in (1, -1):
        bell(bm, (s * 0.75, 0), 0.0, 2.9, 0.57)
    lathe(bm, [(0, 2.6), (R - 0.03, 2.6), (R - 0.03, 1.2), (R, 1.2)], 48, B)
    lathe(bm, [(R, 1.2), (R, 3.4), (R, 18.6), (R, 19.2), (R, 20.6), (R, L)], 48,
          mif=paint(bands=[(1.2, 3.41, B)], roll=[(19.2, 20.6, 16, 0)]))
    top_bowl(bm, R, L, 18.9, 18.3, mat=B)
    box(bm, (R + 0.04, 0, 11.0), (0.12, 0.3, 15.0), M)
    for z in (3.4, 18.6):
        ring(bm, R, z, 0.04, 0.1)


def b_titan2(bm):
    R, L = 1.525, 8.5
    bell(bm, (0, 0), -1.8, 2.8, 0.7)
    lathe(bm, [(0, 0.3), (R, 0.0)], 48, B)
    lathe(bm, [(R, 0), (R, 0.4), (R, 7.9), (R, L), (0, L)], 48, mif=paint(bands=[(0, 0.41, B)]))
    box(bm, (R + 0.04, 0, 4.2), (0.12, 0.3, 7.0), M)
    for z in (0.4, 7.9):
        ring(bm, R, z, 0.04, 0.1)


# ---------------- Saturn V ----------------
def b_sic(bm):
    R, L = 5.05, 42.0
    outb = [polar(4.0, 45 + 90 * k) for k in range(4)]
    bell(bm, (0, 0), 0.0, 5.8, 1.88, segs=40)
    for c in outb:
        bell(bm, c, 0.0, 5.8, 1.88, segs=40)
    lathe(bm, [(0, 5.0), (R, 5.0)], 64, B)
    lathe(bm, [(R, 5.0), (R, 14.0), (R, 24.0), (R, 27.0), (R, 39.5), (R, L), (0, L)], 64,
          mif=paint(roll=[(5.0, 14.0, 8, 0), (39.5, L, 8, 0)], bands=[(24.0, 27.0, B)]))
    for k, c in enumerate(outb):
        lathe(bm, [(0, 3.0), (1.5, 3.0), (1.5, 5.2), (0.6, 11.5), (0, 12.0)], 32, c=c,
              mif=paint(bands=[(0, 3.05, B)]))
        fin(bm, 45 + 90 * k, [(5.0, 3.2), (8.4, 3.2), (8.4, 4.6), (4.9, 9.0)], 0.25)
    for z in (5.0, 24.0, 27.0, 41.9):
        ring(bm, R, z, 0.08, 0.2, segs=64)


def b_sii(bm):
    R, L = 5.05, 24.9
    lathe(bm, [(0, 3.7), (R - 0.05, 3.7), (R - 0.05, 0), (R, 0)], 64, B)
    lathe(bm, [(R, 0), (R, 5.0), (R, 20.0), (3.3, L), (3.25, L), (R - 0.05, 20.05), (0, 20.8)], 64, H)
    bell(bm, (0, 0), 0.3, 3.4, 1.0)
    for k in range(4):
        bell(bm, polar(2.65, 45 + 90 * k), 0.3, 3.4, 1.0)
    for z in (5.0, 20.0):
        ring(bm, R, z, 0.08, 0.2, segs=64)
    ring(bm, 3.28, L - 0.08, 0.08, 0.15)


def b_sivb(bm):
    R, L = 3.3, 17.8
    bell(bm, (0, 0), -2.0, 3.4, 1.0)
    lathe(bm, [(0, 1.2), (R - 0.04, 1.0), (R - 0.04, 0), (R, 0)], 48, B)
    lathe(bm, [(R, 0), (R, 2.5), (R, 16.9), (R, L), (0, L)], 48,
          mif=paint(roll=[(0, 2.5, 4, 45)], bands=[(16.9, L + 0.01, M)]))
    for az in (90, 270):
        p = polar(R + 0.3, az)
        box(bm, (p[0], p[1], 1.4), (0.6, 1.4, 1.8), H, az)
    for z in (2.5, 16.9):
        ring(bm, R, z, 0.05, 0.12)


# ---------------- Протон-К ----------------
def b_proton1(bm):
    R, L = 3.7, 21.0
    lathe(bm, [(0, 2.6), (2.05, 3.0), (2.05, 19.5), (0, 19.9)], 48, H)
    for k in range(6):
        c = polar(2.85, 30 + 60 * k)
        lathe(bm, [(0, 1.6), (0.72, 1.6), (0.8, 2.8), (0.8, 18.4), (0.35, 20.2), (0, 20.4)], 32, c=c,
              mif=paint(bands=[(0, 2.81, B)]))
        bell(bm, c, 0.0, 2.7, 0.72)
        ring(bm, 0.8, 10.0, 0.03, 0.08, segs=32, c=c)
    truss(bm, 2.0, 19.7, 2.0, L - 0.09, 12, 0.06)


def b_proton2(bm):
    R, L = 2.05, 17.0
    for k in range(4):
        bell(bm, polar(1.15, 45 + 90 * k), -1.2, 2.3, 0.55)
    lathe(bm, [(0, 0.8), (R - 0.03, 0.55), (R - 0.03, 0), (R, 0)], 48, B)
    lathe(bm, [(R, 0), (R, 15.4), (R, 16.0), (R, 16.8), (R, L)], 48, mif=paint(roll=[(16.0, 16.8, 12, 0)]))
    top_bowl(bm, R, L, 15.4, 15.8, mat=B)
    for z in (0.6, 8.0, 15.4):
        ring(bm, R, z, 0.04, 0.1)


def b_proton3(bm):
    R, L = 2.05, 4.1
    bell(bm, (0, 0), -1.0, 1.9, 0.45)
    for k in range(4):
        bell(bm, polar(1.6, 45 + 90 * k), -0.3, 0.6, 0.15, segs=16)
    lathe(bm, [(0, 0.2), (R, 0.0)], 48, B)
    lathe(bm, [(R, 0), (R, 0.5), (R, 3.3), (R, L), (0, L)], 48, mif=paint(bands=[(0, 0.51, B), (3.3, L + 0.01, M)]))
    ring(bm, R, 3.3, 0.04, 0.1)


def b_blokd(bm):
    bell(bm, (0, 0), 0.0, 1.9, 0.55)
    torus(bm, 1.25, 0.9, 0.55, H)
    sphere(bm, 1.35, 3.2, H, segs=40)
    truss(bm, 1.3, 1.4, 1.0, 2.3, 8, 0.04)
    truss(bm, 1.3, 3.6, 1.75, 5.38, 8, 0.04)
    ring(bm, 1.75, 5.42, 0.1, 0.16, B)


HULLS = [
    ("Redstone", b_redstone), ("Juno_Stage1", b_juno1), ("Juno_Cluster11", b_cluster11), ("Juno_Cluster3", b_cluster3),
    ("Atlas_Booster", b_atlas_booster), ("Atlas_Sustainer", b_atlas_mercury), ("Atlas_SustainerAgena", b_atlas_agena),
    ("Atlas_SustainerCentaur", b_atlas_centaur), ("Agena", b_agena), ("Centaur", b_centaur),
    ("Titan_Stage1", b_titan1), ("Titan_Stage2", b_titan2),
    ("Saturn_SIC", b_sic), ("Saturn_SII", b_sii), ("Saturn_SIVB", b_sivb),
    ("Proton_Stage1", b_proton1), ("Proton_Stage2", b_proton2), ("Proton_Stage3", b_proton3), ("BlokD", b_blokd),
]
