# Карты высот (DEM) тел для Core/Bodies/Terrain (GDD §2.8): реальный рельеф вместо процедурного шума.
# Источники (качаются в Tools/dem_src/, папка в .gitignore; повторный запуск берёт кеш):
#   Земля    — NOAA NCEI ETOPO 2022, 60″, «surface» (верх ледников), с батиметрией; высоты над геоидом EGM2008.
#              Через OPeNDAP с шагом 2 (2′), затем среднее 3×3 → 0,1°.
#   Луна     — LRO LOLA LDEM_16 (PDS Geosciences), 16 пикс/°, int16 × 0,5 м над сферой 1737,4 км.
#   Марс     — MGS MOLA MEGDR megt90n000eb (PDS Geosciences), 16 пикс/°, int16 м над ареоидом.
#   Меркурий — MESSENGER USGS DEM Global 665 м v2 (USGS Astrogeology), 64 пикс/°, int16 × 0,5 м над 2439,4 км.
#   Венера   — Magellan Topography Global 4641 м v02 (USGS Astrogeology), 8192×4096, int16 м над 6051,0 км.
# Высота в бинаре — над радиусом тела в ядре (SolarSystem.cs): к источнику прибавляется (R_ист − R_тела).
#
# Выход — Assets/_Project/Data/<Body>Height.bytes (формат читает Core HeightMap):
#   'KDEM', int32 W, int32 H, float32 scale, float32 offset, затем W·H int16 (LE); высота = offset + scale·v, м.
#   Строка 0 — северный полюс, x = 0 — 180° з. д. (центры текселей), как у EarthLand.bytes.
# Запуск: python Tools/bake-dem.py [earth moon mars mercury venus] [--check]   (--check — только сверка с текстурами)
import os, sys, struct, urllib.request
import numpy as np
from PIL import Image

Image.MAX_IMAGE_PIXELS = None
ROOT = 'C:/CocosGames/KareSpaceProgram/'
SRC = ROOT + 'Tools/dem_src/'
DST = ROOT + 'Assets/_Project/Data/'
TEX = ROOT + 'Assets/_Project/Textures/Bodies/'

# Радиусы тел в ядре, м (SolarSystem.CreateReal). Пара: меняешь радиус там — перепеки.
BODY_R = {'earth': 6371000, 'moon': 1737400, 'mars': 3389500, 'mercury': 2439700, 'venus': 6051800}
# Размер карты: 0,1° (3600×1800, 13 МБ) — Луна 3 км/тексель, Земля 11 км; Венера 0,125° — у Magellan
# реальное разрешение 10–20 км, мельче смысла нет.
SIZE = {'earth': (3600, 1800), 'moon': (3600, 1800), 'mars': (3600, 1800), 'mercury': (3600, 1800), 'venus': (2880, 1440)}
NAME = {'earth': 'Earth', 'moon': 'Moon', 'mars': 'Mars', 'mercury': 'Mercury', 'venus': 'Venus'}

URL = {
    'moon': 'https://pds-geosciences.wustl.edu/lro/lro-l-lola-3-rdr-v1/lrolol_1xxx/data/lola_gdr/cylindrical/img/ldem_16.img',
    'mars': 'https://pds-geosciences.wustl.edu/mgs/mgs-m-mola-5-megdr-l3-v1/mgsl_300x/meg016/megt90n000eb.img',
    'mercury': 'https://planetarymaps.usgs.gov/mosaic/Mercury_Messenger_USGS_DEM_Global_665m_v2.tif',
    'venus': 'https://planetarymaps.usgs.gov/mosaic/Venus_Magellan_Topography_Global_4641m_v02.tif',
    'earth': 'https://www.ngdc.noaa.gov/thredds/dodsC/global/ETOPO2022/60s/60s_surface_elev_netcdf/ETOPO_2022_v1_60s_N90W180_surface.nc',
}


def fetch(body):
    os.makedirs(SRC, exist_ok=True)
    if body == 'earth':
        return fetch_etopo()
    path = SRC + URL[body].rsplit('/', 1)[1]
    if not os.path.exists(path):
        print('download', URL[body])
        urllib.request.urlretrieve(URL[body], path + '.part')
        os.replace(path + '.part', path)
    return path


def fetch_etopo():
    """ETOPO 2022 60″ через OPeNDAP с шагом 2 → 10800×5400 (2′), строка 0 — юг. Кеш — int16 .npy (117 МБ)."""
    path = SRC + 'etopo2022_60s_surface_stride2.npy'
    if os.path.exists(path):
        return path
    rows, cols, step = 10800, 21600, 600  # 600 строк результата за запрос ≈ 26 МБ
    out = np.empty((rows // 2, cols // 2), np.int16)
    for r0 in range(0, rows // 2, step):
        r1 = min(rows // 2, r0 + step)
        q = f'{URL["earth"]}.dods?z.z[{2 * r0}:2:{2 * r1 - 2}][0:2:{cols - 1}]'
        print('opendap rows', r0, r1)
        d = urllib.request.urlopen(q, timeout=600).read()
        i = d.find(b'Data:\n') + 6
        n = struct.unpack('>i', d[i:i + 4])[0]
        z = np.frombuffer(d, '>f4', n, i + 8).reshape(r1 - r0, cols // 2)
        out[r0:r1] = np.clip(np.round(z), -32000, 32000).astype(np.int16)
    np.save(path, out)
    return path


def load(body):
    """Исходник → float32 (H, W): строка 0 — север, столбец 0 — центр на −180° + полтекселя; NaN — нет данных.
    Высота уже над радиусом тела в ядре."""
    p = fetch(body)
    R = BODY_R[body]
    if body == 'earth':
        a = np.load(p).astype(np.float32)[::-1]  # OPeNDAP: строка 0 — юг (lat[0] = −89,99)
        return a  # столбец 0 — −180° (GeoTransform −180)
    if body == 'moon':
        a = np.fromfile(p, '<i2').reshape(2880, 5760).astype(np.float32) * 0.5  # SCALING_FACTOR 0.5, OFFSET 1737400
        a += 1737400 - R
    elif body == 'mars':
        a = np.fromfile(p, '>i2').reshape(2880, 5760).astype(np.float32)  # MSB, над ареоидом
        a += 0  # ареоид ≈ средний радиус 3389,5 км — уровень «моря» Марса
    elif body == 'mercury':
        raw = np.memmap(p, '<i2', 'r', 92981, (11520, 23040))  # ISIS StartByte 92982 (с единицы)
        parts = []
        for r0 in range(0, 11520, 1152):  # кусками: целиком во float32 это 1 ГБ на копию
            c = raw[r0:r0 + 1152].astype(np.float32)
            c[c <= -32764] = np.nan  # спец. значения ISIS (Null/LRS/LIS/HIS/HRS)
            parts.append(block_mean(c * 0.5 + (2439400 - R), 4))  # 64 → 16 пикс/°
        a = np.concatenate(parts)
    elif body == 'venus':
        raw = np.fromfile(p, '<i2', offset=33415).reshape(4096, 8192)  # полосы TIFF без сжатия, подряд
        a = raw.astype(np.float32)
        a[raw == -32768] = np.nan  # GDAL nodata — пропуски покрытия Magellan
        a += 6051000 - R
        return a  # у этой карты столбец 0 уже −180° (tiepoint −19 009 777 м)
    # PDS/ISIS: столбец 0 — долгота 0° (WESTERNMOST 0), сдвиг на полкруга → столбец 0 = −180°.
    return np.roll(a, -a.shape[1] // 2, axis=1)


def block_mean(a, k):
    h, w = a.shape[0] // k, a.shape[1] // k
    b = a[:h * k, :w * k].reshape(h, k, w, k)
    ok = ~np.isnan(b)
    s = np.where(ok, b, 0).sum(axis=(1, 3))
    c = ok.sum(axis=(1, 3))
    with np.errstate(invalid='ignore'):
        return np.where(c > 0, s / np.maximum(c, 1), np.nan).astype(np.float32)


def fill_nan(a):
    """Дыры (пропуски покрытия) — пирамидой: среднее по грубому уровню, рекурсивно."""
    m = np.isnan(a)
    if not m.any():
        return a
    if min(a.shape) < 4:
        a = a.copy(); a[m] = np.nanmean(a) if (~m).any() else 0; return a
    c = fill_nan(block_mean(a, 2))
    up = np.repeat(np.repeat(c, 2, 0), 2, 1)
    up = np.pad(up, ((0, a.shape[0] - up.shape[0]), (0, a.shape[1] - up.shape[1])), mode='edge')
    a = a.copy(); a[m] = up[m]
    return a


def resample(a, W, H):
    a = fill_nan(a)
    if a.shape[1] % W == 0 and a.shape[0] % H == 0 and a.shape[1] // W == a.shape[0] // H:
        return block_mean(a, a.shape[1] // W)
    # Площадное усреднение (BOX) по долготе по кругу: края шва ±180° не «тянут» друг друга.
    return np.asarray(Image.fromarray(a, 'F').resize((W, H), Image.BOX), np.float32)


def earth_fix_inland(h):
    """Суша ниже уровня моря (Прикаспий, Каттара, Мёртвая долина, польдеры) по правилу «ниже нуля — вода»
    стала бы озером. Берём маску суши EarthLand.bytes (поле 0,5 — берег): глубоко в материке (поле ≥ 0,75,
    ≥ 2 текселя маски ≈ 40 км от берега) отрицательное поднимаем до +1 м. Каспий в маске — вода, остаётся.
    Дно озёр ниже уровня моря (ETOPO «surface» даёт батиметрию озёр: Байкал, Ладога) тоже становится сушей —
    озёра в игре сухие котловины, как и все озёра выше нуля."""
    d = open(DST + 'EarthLand.bytes', 'rb').read()
    mw, mh = struct.unpack('<ii', d[:8])
    field = np.frombuffer(d, np.uint8, mw * mh, 8).reshape(mh, mw) / 255.0
    H, W = h.shape
    ys = ((np.arange(H) + 0.5) / H * mh).astype(int)
    xs = ((np.arange(W) + 0.5) / W * mw).astype(int)
    f = field[ys][:, xs]
    # Только |широта| < 60°: в маске (по снимку) морской лёд Арктики — «суша», и правило поднимало бы
    # Северный Ледовитый океан до +1 м (5,8 % всех текселей, замер 04.10.2026).
    lat = 90 - (np.arange(H) + 0.5) / H * 180
    fix = (h < 1) & (f >= 0.75) & (np.abs(lat) < 60)[:, None]
    print(f'   earth: подняты низины суши ниже 0 — {fix.sum()} текселей ({fix.mean() * 100:.2f} %)')
    h = h.copy(); h[fix] = 1
    return h


def write(body, h):
    W, H = h.shape[1], h.shape[0]
    v = np.clip(np.round(h), -32767, 32767).astype('<i2')
    path = DST + NAME[body] + 'Height.bytes'
    with open(path, 'wb') as fo:
        fo.write(b'KDEM' + struct.pack('<iiff', W, H, 1.0, 0.0))
        fo.write(v.tobytes())
    return path


def sample(h, lat, lon):
    H, W = h.shape
    x = int(((lon + 180) / 360 * W) % W)
    y = min(H - 1, max(0, int((90 - lat) / 180 * H)))
    return float(h[y, x])


# Ориентиры для проверки привязки (широта, долгота в.д. от −180 до 180, ожидаемое, м — порядок величины).
LANDMARKS = {
    'earth': [('Эверест', 27.99, 86.93, '>5000 (0,1° сглаживает)'), ('Анды, Аконкагуа', -32.65, -70.01, '>3500'),
              ('Марианская впадина', 11.35, 142.2, '<-8000'), ('Байконур', 45.92, 63.34, '≈90'),
              ('Канаверал', 28.61, -80.60, '≈0..3'), ('Каспий', 42.0, 50.5, '<0 вода')],
    'moon': [('Тихо (дно)', -43.31, -11.36, '<-2500'), ('Море Спокойствия', 8.5, 31.4, '≈-1900'),
             ('Море Дождей', 33, -16, '≈-2500'), ('Южный полюс — Эйткен', -53, -169, '<-6000'),
             ('Высшая точка (Энгельгардт)', 5.4, -158.6, '>9000')],
    'mars': [('Олимп', 18.65, -133.8, '≈21000'), ('Эллада (дно)', -42.4, 70.5, '<-7000'),
             ('Арсия', -8.26, -120.09, '≈17000'), ('Долины Маринер', -7, -70, '<-2000')],
    'mercury': [('Калорис', 31.5, 162.7, '<0 бассейн'), ('Рахманинов', 27.6, 57.6, '<-2000')],
    'venus': [('Максвелл', 65.2, 3.3, '>9000'), ('Земля Афродиты', -5, 105, '>1000'), ('Равнина Аталанты', 46, 165, '<0')],
}


def tex_lum(body, W, H):
    f = {'earth': 'EarthDay.jpg', 'moon': 'MoonLroc8k.png', 'mars': 'MarsMap.jpg', 'mercury': 'MercuryMap.jpg',
         'venus': 'VenusMap.jpg'}[body]
    im = Image.open(TEX + f).convert('L').resize((W, H), Image.BOX)
    return np.asarray(im, np.float32), f


def highpass(a, k=8):
    """Минус размытие окном k: крупный тренд (полушария, альбедо морей) не тянет корреляцию."""
    from numpy.lib.stride_tricks import sliding_window_view as sw
    p = np.pad(a, ((k, k), (k, k)), mode='wrap')
    c = p.cumsum(0).cumsum(1)
    c = np.pad(c, ((1, 0), (1, 0)))
    n = 2 * k + 1
    s = c[n:, n:] - c[:-n, n:] - c[n:, :-n] + c[:-n, :-n]
    return a - s[:a.shape[0], :a.shape[1]] / (n * n)


def check(body):
    """Сверка долготной привязки DEM с цветовой картой тела: корреляция по сдвигу долготы (FFT по строкам).
    Признаки — высокочастотная высота и «отмывка» (производная по долготе) против яркости текстуры."""
    path = DST + NAME[body] + 'Height.bytes'
    d = open(path, 'rb').read()
    W, H = struct.unpack('<ii', d[4:12])
    h = np.frombuffer(d, '<i2', W * H, 20).reshape(H, W).astype(np.float32)
    if body == 'earth':
        # Земля: берег. Суша DEM (h > 0) против «не-океана» снимка (океан тёмно-синий).
        rgb = np.asarray(Image.open(TEX + 'EarthDay.jpg').convert('RGB').resize((W, H), Image.BOX), np.float32)
        water = (rgb[..., 2] > rgb[..., 0] + 10) & (rgb[..., 2] > rgb[..., 1]) & (rgb.sum(-1) < 300)
        land_tex = (~water).astype(np.float32)
        land_dem = (h > 0).astype(np.float32)
        lat = np.cos(np.deg2rad(90 - (np.arange(H) + 0.5) / H * 180))[:, None]
        band = (np.abs(90 - (np.arange(H) + 0.5) / H * 180) < 60)[:, None]  # без льдов полярных шапок
        agree = ((land_tex == land_dem) * lat * band).sum() / (lat * band * np.ones((1, W))).sum()
        feats = [('суша', land_dem, land_tex)]
        print(f'   earth: совпадение суша/вода DEM и EarthDay (|широта| < 60°) {agree * 100:.1f} %')
    else:
        lum, f = tex_lum(body, W, H)
        shade = np.roll(h, -1, 1) - np.roll(h, 1, 1)  # отмывка с востока/запада — тени рельефа на снимке
        feats = [('высота', highpass(h), highpass(lum)), ('отмывка', highpass(shade, 4), highpass(lum, 4))]
    wlat = np.cos(np.deg2rad(90 - (np.arange(H) + 0.5) / H * 180))[:, None]
    for name, A, B in feats:
        A = (A - A.mean()) * np.sqrt(wlat); B = (B - B.mean()) * np.sqrt(wlat)  # вес площади cos(широты)
        for flip in (False, True):
            Bf = B[::-1] if flip else B
            c = np.fft.irfft(np.conj(np.fft.rfft(A, axis=1)) * np.fft.rfft(Bf, axis=1), n=W, axis=1).sum(0)
            c /= np.sqrt((A ** 2).sum() * (Bf ** 2).sum())
            k = int(np.argmax(np.abs(c)))
            shift = (k if k <= W // 2 else k - W) * 360 / W
            print(f'   {body} {name}{" (перевёрнута по широте)" if flip else ""}: corr(0°) = {c[0]:+.3f}, '
                  f'пик {c[k]:+.3f} при сдвиге {shift:+.1f}°')


def main():
    args = [a for a in sys.argv[1:] if not a.startswith('--')]
    bodies = args or ['earth', 'moon', 'mars', 'mercury', 'venus']
    only_check = '--check' in sys.argv
    for body in bodies:
        if not only_check:
            W, H = SIZE[body]
            a = load(body)
            h = resample(a, W, H)
            if body == 'earth':
                h = earth_fix_inland(h)
            p = write(body, h)
            print(f'{body}: {W}×{H}, {2 * np.pi * BODY_R[body] / W / 1000:.2f} км/тексель на экваторе, '
                  f'{h.min():.0f}…{h.max():.0f} м, медиана {np.median(h):.0f} м, {os.path.getsize(p) / 1e6:.1f} МБ')
            for name, la, lo, want in LANDMARKS[body]:
                print(f'   {name}: {sample(h, la, lo):.0f} м (ожидаемо {want})')
        check(body)


if __name__ == '__main__':
    main()
