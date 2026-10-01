# Нарезка сгенерированных атласов (Tools/gen-texture.mjs) на текстуры проекта.
# Генерим атласами по несколько штук — один запрос дешевле четырёх.
#   python Tools/slice-atlas.py surfaces <atlas.png> <outdir>   — 2x2 поверхности, бесшовные
#   python Tools/slice-atlas.py icons    <atlas.png> <out.png>  — 4x4 иконок → чистый атлас с альфой
import sys, os
from PIL import Image, ImageChops
import numpy as np

# Генератор рисует сетку-разделитель ~1% ширины: срезаем с запасом, иначе линия попадёт в тайл.
INSET = 0.03

def cells(img, n):
    w, h = img.size
    cw, ch = w // n, h // n
    dx, dy = int(cw * INSET), int(ch * INSET)
    for r in range(n):
        for c in range(n):
            yield r, c, img.crop((c * cw + dx, r * ch + dy, (c + 1) * cw - dx, (r + 1) * ch - dy))

def tileable(im):
    # Классика: сдвиг на полразмера (шов уходит в центр) и смешивание с оригиналом по маске,
    # где у краёв берётся сдвинутая копия (она по краям непрерывна), в центре — оригинал.
    a = np.asarray(im.convert('RGB'), dtype=np.float32)
    h, w, _ = a.shape
    s = np.roll(np.roll(a, h // 2, 0), w // 2, 1)
    y = np.minimum(np.arange(h), h - 1 - np.arange(h)) / (h / 2)
    x = np.minimum(np.arange(w), w - 1 - np.arange(w)) / (w / 2)
    m = np.clip(np.minimum.outer(y, x) * 2.5, 0, 1)[..., None]
    return Image.fromarray((a * m + s * (1 - m)).astype(np.uint8))

mode, src, out = sys.argv[1], sys.argv[2], sys.argv[3]
img = Image.open(src).convert('RGB')
if mode == 'surfaces':
    names = {(0, 0): 'Concrete', (0, 1): 'SteppeDetail', (1, 0): 'Regolith', (1, 1): 'MarsSoil'}
    os.makedirs(out, exist_ok=True)
    for r, c, im in cells(img, 2):
        tileable(im.resize((1024, 1024), Image.LANCZOS)).save(os.path.join(out, names[(r, c)] + '.png'))
elif mode == 'icons':
    S = 128
    atlas = Image.new('RGBA', (S * 4, S * 4), (255, 255, 255, 0))
    for r, c, im in cells(img, 4):
        lum = im.convert('L').resize((S, S), Image.LANCZOS)
        icon = Image.new('RGBA', (S, S), (255, 255, 255, 255))
        icon.putalpha(lum)  # белая иконка, форма — в альфе: красится в HUD через GUI.color
        atlas.paste(icon, (c * S, r * S))
    atlas.save(out)
elif mode == 'macro':
    tileable(img.resize((1024, 1024), Image.LANCZOS)).save(out)
