"""Карты спутников NASA/JPL/USGS -> 2:1 равнопромежуточные JPG, долгота 0 в центре, на восток вправо
(как у Solar System Scope, BodyRenderer.LatLonUv: u = (lon+180)/360). Яркость — к геометрическому альбедо
(как Tools/textures/sss_convert.py). Пара: ALBEDO — с NightLight.GeometricAlbedo / фактами NASA."""
import os, sys, numpy as np
from PIL import Image
Image.MAX_IMAGE_PIXELS = None
SRC = os.environ.get('MOON_SRC', 'moon_dl/')  # папка со скачанными GeoTIFF USGS (planetarymaps.usgs.gov / asc-pds-services mosaic), по подпапке на тело
OUT = sys.argv[1] if len(sys.argv) > 1 else '.'
ONLY = sys.argv[2:] 
# id: (файл, сдвиг на пол-ширины (центр исходника — 180°), размер, альбедо, тинт для серых: (светлое, тёмное))
J = {
 'phobos':   ('phobos/Phobos_Viking_Mosaic_40ppd_DLRcontrol.tif', False, 2048, 0.071, ((1.00,.97,.93),(1.10,.95,.85))),
 'deimos':   ('deimos/deimoscyl1.jpg', False, 2048, 0.068, ((1.00,.97,.93),(1.05,.96,.88))),
 'io':       ('io/Io_GalileoSSI-Voyager_Global_Mosaic_ClrMerge_1km.tif', False, 4096, 0.63, None),
 'europa':   ('europa/Europa_Voyager_GalileoSSI_global_mosaic_500m.tif', True, 4096, 0.67, ((.96,.98,1.03),(1.3,.95,.65))),
 'ganymede': ('ganymede/Ganymede_Voyager_GalileoSSI_Global_ClrMosaic_1435m.tif', True, 4096, 0.43, None),
 'callisto': ('callisto/Callisto_Voyager_GalileoSSI_global_mosaic_1km.tif', True, 4096, 0.22, ((.96,.98,1.02),(1.15,.98,.80))),
 'enceladus':('enceladus/Enceladus_Cassini_mosaic_global_110m.tif', False, 4096, 0.62, ((.96,.99,1.04),(.98,.99,1.02))),
 'titan':    ('titan/Titan_ISS_P19658_Mosaic_Global_4km.tif', True, 4096, 0.20, ((1.45,1.0,.45),(1.5,.9,.35))),
 'triton':   ('triton/Triton_Voyager2_ClrMosaic_GlobalFill_600m.tif', False, 4096, 0.76, ((1.0,.93,.88),(1.2,.85,.65))),
 'pluto':    ('pluto/Pluto_NewHorizons_Global_Mosaic_300m_Jul2017_8bit.tif', True, 4096, 0.52, ((1.1,1.0,.88),(1.5,.85,.55))),
 'charon':   ('charon/Charon_NewHorizons_Global_Mosaic_300m_Jul2017_8bit.tif', False, 4096, 0.41, ((.99,1.0,1.01),(1.35,.9,.7))),
}
FILL = {'triton': (1.0, .93, .88)}
CONTRAST = {'titan': 0.5}  # хаза: поверхность с орбиты видна смазанной
LW = np.array([0.2126, 0.7152, 0.0722], np.float32)
def to_lin(a): return np.where(a <= 0.04045, a / 12.92, ((a + 0.055) / 1.055) ** 2.4)
def to_srgb(l): return np.where(l <= 0.0031308, l * 12.92, 1.055 * np.power(np.maximum(l, 0), 1 / 2.4) - 0.055)

def rs(arr2d, size):  # float32 2D -> resize
    return np.asarray(Image.fromarray(arr2d.astype(np.float32), 'F').resize(size, Image.BILINEAR, reducing_gap=2.0) if False else Image.fromarray(arr2d.astype(np.float32), 'F').resize(size, Image.BOX if arr2d.shape[1] > size[0] else Image.BICUBIC))

def load(path, roll, tw):
    im = Image.open(path)
    gray = im.mode in ('L', 'I;16')
    im = im.convert('L') if gray else im.convert('RGB')
    W, H = im.size
    th = tw // 2
    a = np.asarray(im)
    nod = (a == 0) if gray else (a == 0).all(2)
    # Нет данных — нулевые пиксели; доля нулей в тексле даёт вес, остальное усредняется только по данным.
    valid = rs((~nod).astype(np.float32), (tw, th))
    ch = [a] if gray else [a[..., i] for i in range(3)]
    out = []
    for c in ch:
        s = rs(c.astype(np.float32), (tw, th))  # нули входят в сумму как 0
        out.append(s)
    out = np.stack(out, -1)
    del a, nod, ch
    v = np.maximum(valid, 1e-4)[..., None]
    out = np.where(valid[..., None] > 0.05, out / v, 0) / 255.0
    valid = (valid > 0.5).astype(np.float32)
    if roll:
        out = np.roll(out, tw // 2, axis=1); valid = np.roll(valid, tw // 2, axis=1)
    return out.astype(np.float32), valid, gray

def up(a, size):  # (h,w,c) float -> bicubic upsample по каналам
    return np.stack([np.asarray(Image.fromarray(a[..., c], 'F').resize(size, Image.BICUBIC)) for c in range(a.shape[2])], -1)

def inpaint(img, w):
    """Пирамидальная заливка дыр (нет данных): средний тон окрестности, мягко."""
    if w.min() > 0.5: return img
    h, wd = w.shape
    if h < 4 or wd < 4:
        m = (img * w[..., None]).sum((0, 1)) / max(w.sum(), 1)
        return np.where(w[..., None] > 0, img, m)
    def red(x): return x.reshape(h // 2, 2, wd // 2, 2, *x.shape[2:]).mean((1, 3))
    ws = red(w); Is = red(img * w[..., None])
    Ic = np.where(ws[..., None] > 0, Is / np.maximum(ws, 1e-6)[..., None], 0)
    f = inpaint(Ic, (ws > 0).astype(np.float32))
    u = up(f, (wd, h))
    return np.where(w[..., None] > 0.5, img, u)

def main():
    os.makedirs(OUT, exist_ok=True)
    for name, (f, roll, tw, alb, tint) in J.items():
        if ONLY and name not in ONLY: continue
        img, valid, gray = load(SRC + f, roll, tw); img = np.clip(img, 0, 1)
        nod = 1 - valid.mean()
        if nod > 0.001:
            img = inpaint(img, valid)
            # Заливка гладкая — добавим зерно, иначе пятно читается плоской заплаткой.
            rng = np.random.default_rng(7)
            n = np.asarray(Image.fromarray(rng.normal(0, 1, valid.shape).astype(np.float32), 'F').resize((valid.shape[1] // 2, valid.shape[0] // 2), Image.BOX).resize((valid.shape[1], valid.shape[0]), Image.BICUBIC))
            hole = np.asarray(Image.fromarray(1 - valid, 'F').resize((valid.shape[1] // 8, valid.shape[0] // 8), Image.BOX).resize((valid.shape[1], valid.shape[0]), Image.BICUBIC))
            img = np.clip(img * (1 + 0.5 * n[..., None] * np.clip(hole * 1.5, 0, 1)[..., None] * 0.35), 0, 1)
            if name in FILL:
                # Цветная заливка по краям Вояджера даёт зелёный: в дыре оставляем яркость, цвет — нейтральный.
                Lf = (to_lin(img) * LW).sum(2)[..., None]
                ft = np.array(FILL[name], np.float32); ft /= (ft * LW).sum()
                hw = np.clip(hole * 2.5, 0, 1)[..., None]
                img = to_srgb(np.clip(to_lin(img) * (1 - hw) + Lf * ft * hw, 0, 1))
        lin = to_lin(img)
        L = (lin * LW).sum(2) if not gray else lin[..., 0]
        # Средняя яркость по площади (вес cos широты) только по реальным данным.
        lat = np.linspace(np.pi / 2, -np.pi / 2, L.shape[0])
        wt = np.cos(lat)[:, None] * np.ones((1, L.shape[1]))
        vw = wt * np.maximum(valid, 0.0)
        if vw.sum() == 0: vw = wt
        mean = float((L * vw).sum() / vw.sum())
        if gray or tint:  # Triton: цветная мозаика Вояджера искусственно зелёная — берём только яркость
            t0, t1 = [np.array(t, np.float32) for t in tint] if tint else (np.ones(3, np.float32),) * 2
            t0 /= (t0 * LW).sum(); t1 /= (t1 * LW).sum()
            c = CONTRAST.get(name, 1.0)
            L = np.maximum(mean + c * (L - mean), 0)
            d = np.clip((1 - L / mean) / 0.7, 0, 1)[..., None]
            lin = L[..., None] * (t0 * (1 - d) + t1 * d)
        # Мягкое колено у белого: жёсткий клип выжигал ледяные шапки (Тритон, Энцелад). k подбираем так,
        # чтобы после колена средняя яркость осталась альбедо.
        def knee(x): return np.where(x > 0.7, 0.7 + 0.3 * np.tanh((x - 0.7) / 0.3), x)
        lo, hi = 0.0, 50.0
        Ls = (lin * LW).sum(2)
        for _ in range(30):
            k = (lo + hi) / 2
            m = float((knee(Ls * k) * vw).sum() / vw.sum())
            if m < alb: lo = k
            else: hi = k
        lin = np.clip(knee(lin * k), 0, 1)
        res = (np.clip(to_srgb(lin), 0, 1) * 255 + 0.5).astype(np.uint8)
        Image.fromarray(res, 'RGB').save(os.path.join(OUT, name + '.jpg'), quality=92, subsampling=0)
        m2 = float(((lin * LW).sum(2) * vw).sum() / vw.sum())
        print(f'{name}: {tw}x{tw//2} nodata={nod*100:.1f}% k={k:.2f} mean_lin={m2:.3f}', flush=True)
main()
