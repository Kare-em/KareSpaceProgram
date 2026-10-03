"""
Процедурные текстуры обшивки бортов (GDD §9.5): ничего не скачивается, всё считается здесь.
Запуск: python Tools/gen-hull-textures.py  ->  Assets/_Project/Textures/Hull/*.png
Затем меню Kare/Build Flight Scene (или FlightSceneBuilder.HullTexturing через execute_code) назначает их VesselLit.mat.

Тайл = HullTileMeters (FlightSceneBuilder) метров по обеим осям, SIZE пикселей, бесшовный.
Пары (меняешь одно — проверяй второе):
  TILE_M = FlightSceneBuilder.HullTileMeters;  ROWS*COLS панелей на тайл — размер панели = TILE_M/COLS x TILE_M/ROWS;
  PX_PER_M = SIZE/TILE_M — ширина шва и шаг заклёпок в пикселях заданы от него.
Слои:
  Albedo  — почти белый (цвет ступени умножается из MPB), швы, заклёпки, подтёки сажи вдоль оси, грязь.
  Normal  — швы-канавки, заклёпки-бугорки, стрингеры (рёбра вдоль оси), мелкое зерно. GL-формат (Y вверх), как ждёт Unity.
  Mask    — HDRP Mask Map: R металличность, G AO (темнее в швах), B маска детали, A гладкость (грязь матовее).
  Detail  — HDRP Detail Map: R оттенок, G нормаль Y, B гладкость, A нормаль X; мелкие продольные царапины.
"""
import os
import numpy as np
from PIL import Image

SIZE = 1024
TILE_M = 2.0
COLS, ROWS = 1, 2
PX_PER_M = SIZE / TILE_M
SEAM_PX = 3          # ширина канавки шва
RIVET_STEP_M = 0.05  # шаг заклёпок вдоль шва
RIVET_R = 3.0        # радиус заклёпки, пикселей
STRINGER_M = 0.5     # шаг стрингеров вдоль окружности
OUT = os.path.join(os.path.dirname(__file__), "..", "Assets", "_Project", "Textures", "Hull")
rng = np.random.default_rng(20261003)


def smooth(t):
    return t * t * (3 - 2 * t)


def periodic_noise(gx, gy):
    """Бесшовный value-noise: сетка gx*gy, растянутая на SIZE*SIZE с обёрткой."""
    g = rng.random((gy, gx))
    ys = np.arange(SIZE) * gy / SIZE
    xs = np.arange(SIZE) * gx / SIZE
    y0 = np.floor(ys).astype(int); x0 = np.floor(xs).astype(int)
    fy = smooth(ys - y0)[:, None]; fx = smooth(xs - x0)[None, :]
    y1 = (y0 + 1) % gy; x1 = (x0 + 1) % gx
    a = g[np.ix_(y0, x0)]; b = g[np.ix_(y0, x1)]
    c = g[np.ix_(y1, x0)]; d = g[np.ix_(y1, x1)]
    return (a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy


def fbm(base_x, base_y, octaves=5):
    out = np.zeros((SIZE, SIZE)); amp = 1.0; tot = 0.0
    for o in range(octaves):
        out += amp * periodic_noise(base_x * 2 ** o, base_y * 2 ** o)
        tot += amp; amp *= 0.5
    return out / tot


def build_height():
    """Высота (больше — выше) и маска швов/заклёпок/рёбер."""
    yy, xx = np.mgrid[0:SIZE, 0:SIZE].astype(np.float64)
    pw, ph = SIZE / COLS, SIZE / ROWS
    # Горизонтальные швы — по границам рядов; вертикальные — со сдвигом на полпанели в нечётных рядах.
    dist_h = np.abs(((yy % ph) + ph / 2) % ph - ph / 2)
    row = np.floor(yy / ph).astype(int)
    shift = (row % 2) * (pw / 2)
    dist_v = np.abs((((xx + shift) % pw) + pw / 2) % pw - pw / 2)
    seam_d = np.minimum(dist_h, dist_v)
    seam = np.clip(1 - seam_d / (SEAM_PX * 0.5), 0, 1)
    seam = smooth(seam)

    # Заклёпки: две линии вдоль каждого шва на расстоянии 2*SEAM от оси.
    step = RIVET_STEP_M * PX_PER_M
    off = SEAM_PX * 2.2
    rivets = np.zeros((SIZE, SIZE))
    def dots(coord_along, coord_across_dist, mask_line):
        a = (coord_along % step) - step / 2
        r = np.hypot(a, coord_across_dist - off)
        return np.clip(1 - r / RIVET_R, 0, 1) * mask_line
    # около горизонтальных швов: вдоль x, поперёк — dist_h
    rivets = np.maximum(rivets, dots(xx, dist_h, (dist_v > off * 1.5).astype(float)))
    rivets = np.maximum(rivets, dots(yy, dist_v, (dist_h > off * 1.5).astype(float)))
    rivets = smooth(rivets)

    # Стрингеры: широкие плоские рёбра вдоль оси (вертикаль), шаг STRINGER_M.
    sp = STRINGER_M * PX_PER_M
    sd = np.abs(((xx % sp) + sp / 2) % sp - sp / 2)
    stringer = smooth(np.clip(1 - sd / (sp * 0.06), 0, 1))

    grain = fbm(64, 64, 3)
    height = (-0.6 * seam + 0.55 * rivets + 0.1 * stringer + 0.05 * (grain - 0.5))
    return height, seam, rivets, stringer


def normal_from_height(h, strength):
    # u — вправо, v — вверх (строки картинки идут вниз => dv = -d/drow). Обёртка np.roll — бесшовно.
    du = (np.roll(h, -1, axis=1) - np.roll(h, 1, axis=1)) * 0.5
    dv = -(np.roll(h, -1, axis=0) - np.roll(h, 1, axis=0)) * 0.5
    nx = -du * strength; ny = -dv * strength; nz = np.ones_like(h)
    ln = np.sqrt(nx * nx + ny * ny + nz * nz)
    return nx / ln, ny / ln, nz / ln


def save(arr, name):
    os.makedirs(OUT, exist_ok=True)
    Image.fromarray(np.clip(arr * 255 + 0.5, 0, 255).astype(np.uint8)).save(os.path.join(OUT, name))


def main():
    height, seam, rivets, stringer = build_height()

    # Грязь: низкочастотные пятна + вертикальные подтёки (сажа/конденсат стекают вдоль оси).
    blotch = fbm(4, 4, 5)
    streak = fbm(48, 3, 4)  # вытянуто вдоль v: много столбцов, мало строк по высоте
    streak = np.clip((streak - 0.45) * 3.0, 0, 1)
    soot = np.clip(0.5 * blotch + 0.38 * streak - 0.3, 0, 1)
    # Швы и заклёпки копят грязь.
    soot = np.clip(soot + 0.5 * seam + 0.2 * rivets, 0, 1)

    base = 0.9 - 0.24 * soot
    base *= 1.0 - 0.10 * seam
    base = np.clip(base + 0.05 * rivets, 0, 1)
    albedo = np.stack([base, base * 0.985, base * 0.96], axis=-1)  # чуть тёплый серый
    save(albedo, "HullAlbedo.png")

    nx, ny, nz = normal_from_height(height, 6.0)
    save(np.stack([nx * 0.5 + 0.5, ny * 0.5 + 0.5, nz * 0.5 + 0.5], axis=-1), "HullNormal.png")

    # Mask Map: R металл, G AO, B маска детали, A гладкость.
    metal = np.clip(0.75 - 0.3 * soot, 0, 1)
    ao = np.clip(1 - 0.4 * seam - 0.15 * np.clip(soot, 0, 1), 0, 1)
    smooth_a = np.clip(0.62 - 0.35 * soot - 0.1 * seam, 0, 1)
    save(np.stack([metal, ao, np.ones_like(ao), smooth_a], axis=-1), "HullMask.png")

    # Detail Map: продольные царапины + зерно. R=0.5 — нейтраль.
    sc = np.clip(fbm(160, 6, 3) - 0.5, -0.5, 0.5)
    gr = fbm(256, 256, 2) - 0.5
    dh = sc * 0.8 + gr * 0.4
    dx, dy, _ = normal_from_height(dh, 3.0)
    r = 0.5 + 0.35 * dh
    b = 0.5 + 0.3 * dh
    save(np.stack([r, dy * 0.5 + 0.5, b, dx * 0.5 + 0.5], axis=-1), "HullDetail.png")
    print("ok ->", os.path.abspath(OUT))


if __name__ == "__main__":
    main()
