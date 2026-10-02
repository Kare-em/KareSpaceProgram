# Карта суши Земли для Core/Bodies/Terrain (GDD §2.8): из ассета Planet Earth Free/Earth4kNormal.png.
# У этой нормали океан ровно плоский (127,127,255), у суши есть рельеф — получается чистая маска без облаков
# (доля суши по площади 30,8 % против реальных 29 %).
# Выход — Assets/_Project/Data/EarthLand.bytes: int32 W, int32 H, затем W*H байт поля суши и W*H байт «горности».
#   поле суши: среднее размытий маски на 1…20 px — 128 у берега, растёт вглубь материка (плавный берег и
#   глубина шельфа без карты высот); горность: размытая крутизна нормали, 255 — Гималаи/Анды.
# Запуск: python Tools/bake-earth-land.py
import struct, numpy as np
from PIL import Image

ROOT = 'C:/CocosGames/KareSpaceProgram/'
SRC = ROOT + 'Assets/Planet Earth Free/Materials/Earth4kNormal.png'
DST = ROOT + 'Assets/_Project/Data/EarthLand.bytes'
W, H = 2048, 1024  # ≈ 20 км на тексель на экваторе; мельче берег дорисует шум Terrain.CoastNoise

a = np.asarray(Image.open(SRC).convert('RGB')).astype(np.int32)
ocean = (a[..., 0] == 127) & (a[..., 1] == 127) & (a[..., 2] == 255)
land = (~ocean).astype(np.float32)
rough = (np.abs(a[..., 0] - 127) + np.abs(a[..., 1] - 127)).astype(np.float32) * land

def down(x):  # 4096×2048 → 2048×1024 средним
    return x.reshape(H, 2, W, 2).mean(axis=(1, 3))

def box(x, r, axis, wrap):
    if r <= 0: return x
    if wrap:
        pad = np.concatenate([np.take(x, range(-r, 0), axis), x, np.take(x, range(0, r), axis)], axis)
    else:
        n = x.shape[axis]
        pad = np.concatenate([np.repeat(np.take(x, [0], axis), r, axis), x, np.repeat(np.take(x, [n - 1], axis), r, axis)], axis)
    c = np.cumsum(pad, axis=axis, dtype=np.float64)
    c = np.concatenate([np.zeros_like(np.take(c, [0], axis)), c], axis)
    n = x.shape[axis]
    return ((np.take(c, range(2 * r + 1, 2 * r + 1 + n), axis) - np.take(c, range(0, n), axis)) / (2 * r + 1)).astype(np.float32)

def blur(x, r):  # три прохода ящика ≈ гаусс; по долготе — по кругу
    for _ in range(3):
        x = box(box(x, r, 1, True), r, 0, False)
    return x

L = down(land)
# Дырки-«озёра» в плоской суше (Сахара: 2 % пикселей с нулевым наклоном) — закрыть: точка суши, если вокруг
# в радиусе 2 px суши больше 60 %.
L = np.where(blur(L, 1) > 0.6, 1.0, L)
field = (blur(L, 1) + blur(L, 3) + blur(L, 8) + blur(L, 20)) / 4
# У берега нормаль ассета резко ломается (ступенька «плоский океан — суша»), это не горы: крутизну берём
# только в глубине суши (вокруг в 3 px — вся суша) и подчёркиваем крупные хребты степенью.
R = blur(down(rough) * (blur(L, 3) > 0.98), 6)
R = np.clip(R / np.percentile(R[L > 0.5], 99.5), 0, 1) ** 1.5

f8 = np.clip(field * 255 + 0.5, 0, 255).astype(np.uint8)
r8 = np.clip(R * 255 + 0.5, 0, 255).astype(np.uint8)
import os
os.makedirs(os.path.dirname(DST), exist_ok=True)
with open(DST, 'wb') as fo:
    fo.write(struct.pack('<ii', W, H))
    fo.write(f8.tobytes())
    fo.write(r8.tobytes())
Image.fromarray(np.stack([f8, r8, np.zeros_like(f8)], -1)).save(ROOT + 'Tools/earth-land-preview.png')
lat = np.cos(np.deg2rad(90 - (np.arange(H) + 0.5) / H * 180))[:, None]
print('land frac', ((f8 >= 128) * lat).sum() / (lat.sum() * W), 'bytes', os.path.getsize(DST))
