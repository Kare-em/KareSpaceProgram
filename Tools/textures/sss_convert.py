"""Карты тел Solar System Scope (CC BY 4.0, solarsystemscope.com/textures) -> Assets/_Project/Textures/Bodies.
Яркость приводится к геометрическому альбедо тела (как карта Луны LROC к 0,12): средняя линейная яркость по
площади = альбедо из NightLight.GeometricAlbedo — иначе освещение HDRP в люксах даёт Юпитер тусклее Луны.
Пара: таблица ALBEDO — с NightLight.GeometricAlbedo. Земля не трогается (облака — отдельный слой)."""
import os, shutil, numpy as np
from PIL import Image
Image.MAX_IMAGE_PIXELS = None
SRC = os.path.join(os.path.dirname(__file__), 'sss')
DST = os.path.join(os.path.dirname(__file__), '..', '..', 'Assets', '_Project', 'Textures', 'Bodies')
ALBEDO = {'mercury': 0.142, 'venus': 0.69, 'mars': 0.17, 'jupiter': 0.538, 'saturn': 0.499, 'uranus': 0.488, 'neptune': 0.442}
MAPS = [('mercury', '8k_mercury.jpg', 'MercuryMap.jpg'), ('venus', '4k_venus_atmosphere.jpg', 'VenusMap.jpg'),
        ('mars', '8k_mars.jpg', 'MarsMap.jpg'), ('jupiter', '8k_jupiter.jpg', 'JupiterMap.jpg'),
        ('saturn', '8k_saturn.jpg', 'SaturnMap.jpg'), ('uranus', '2k_uranus.jpg', 'UranusMap.jpg'),
        ('neptune', '2k_neptune.jpg', 'NeptuneMap.jpg')]
COPY = [('8k_earth_daymap.jpg', 'EarthDay.jpg'), ('8k_earth_nightmap.jpg', 'EarthNight.jpg'),
        ('8k_earth_clouds.jpg', 'EarthClouds.jpg'), ('8k_saturn_ring_alpha.png', 'SaturnRing.png')]
# Читаемые малые копии — цвет патча вблизи (BodyRenderer.SurfaceColor) у тел с грунтом.
SMALL = [('MarsMap.jpg', 'MarsSmall.png', 1024), ('EarthDay.jpg', 'EarthSmall.png', 2048)]

def to_lin(a): return np.where(a <= 0.04045, a / 12.92, ((a + 0.055) / 1.055) ** 2.4)
def to_srgb(l): return np.where(l <= 0.0031308, l * 12.92, 1.055 * np.power(l, 1 / 2.4) - 0.055)

def mean_lin(im):
    a = to_lin(np.asarray(im.convert('RGB').resize((512, 256)), dtype=np.float32) / 255)
    L = (a * [0.2126, 0.7152, 0.0722]).sum(2)
    w = np.cos(np.linspace(-np.pi / 2, np.pi / 2, 256))[:, None]
    return float((L * w).sum() / (w.sum() * 512))

os.makedirs(DST, exist_ok=True)
for body, src, dst in MAPS:
    im = Image.open(os.path.join(SRC, src)).convert('RGB')
    k = ALBEDO[body] / mean_lin(im)
    if abs(np.log(k)) > 0.1:
        a = to_lin(np.asarray(im, dtype=np.float32) / 255) * k
        im = Image.fromarray((np.clip(to_srgb(np.clip(a, 0, 1)), 0, 1) * 255 + 0.5).astype(np.uint8))
    im.save(os.path.join(DST, dst), quality=95)
    print(body, src, '->', dst, 'k=%.2f' % k, 'mean=%.3f' % mean_lin(im))
for src, dst in COPY:
    shutil.copyfile(os.path.join(SRC, src), os.path.join(DST, dst)); print('copy', dst)
for src, dst, w in SMALL:
    Image.open(os.path.join(DST, src)).convert('RGB').resize((w, w // 2), Image.LANCZOS).save(os.path.join(DST, dst)); print('small', dst)
