# Бетон стартового стола и площадок (GDD §7): Textures/Ground/Concrete.png + ConcreteNormal.png.
# Тайл 8 м = 2×2 плиты по 4 м (пара: LaunchPadView.ConcreteTile). Прежняя текстура из атласа несла крупные чёрные
# потёки — на площадке 44 м они повторялись пятнами «грязи» (Play 06.10.2026). Здесь всё процедурное и периодическое:
# шум — через FFT (периодичен по построению), швы — на краях и посередине, так что тайл бесшовный.
# Запуск: python Tools/gen-concrete.py
import numpy as np
from PIL import Image

N = 1024                 # px на тайл
TILE_M = 8.0             # м на тайл (пара: LaunchPadView.ConcreteTile)
PX_M = N / TILE_M        # 128 px/м
PLATE = N // 2           # плита 4 м
SEAM_W = 0.025 * PX_M    # полуширина шва ≈ 2,5 см
rng = np.random.default_rng(7)
OUT = "Assets/_Project/Textures/Ground/"


def band_noise(lo_m, hi_m, seed):
    """Периодический шум с длинами волн lo..hi м, нормирован к σ = 1."""
    r = np.random.default_rng(seed)
    f = np.fft.fftfreq(N) * PX_M          # циклов на метр
    fx, fy = np.meshgrid(f, f)
    k = np.hypot(fx, fy)
    spec = np.fft.fft2(r.standard_normal((N, N)))
    mask = (k >= 1 / hi_m) & (k <= 1 / lo_m)
    # 1/f — крупные пятна сильнее мелких, как у настоящего бетона
    w = np.where(mask, 1 / np.maximum(k, 1e-6), 0)
    n = np.real(np.fft.ifft2(spec * w))
    return (n - n.mean()) / n.std()


y, x = np.mgrid[0:N, 0:N].astype(np.float32)
# расстояние до ближайшего шва (швы на 0, 512, 1024 по обеим осям — периодично)
dx = np.abs(((x + PLATE / 2) % PLATE) - PLATE / 2)
dy = np.abs(((y + PLATE / 2) % PLATE) - PLATE / 2)
dseam = np.minimum(dx, dy)

# Тон: светлый серый бетон, у каждой плиты свой оттенок (заливали в разные дни)
base = np.full((N, N), 0.60, np.float32)
plate_id = (x // PLATE).astype(int) + 2 * (y // PLATE).astype(int)
shade = np.array([0.0, 0.025, -0.02, 0.012])
base += shade[plate_id]
mottle = band_noise(0.25, 3.0, 1)        # пятнистость затирки
grain = band_noise(0.004, 0.03, 2)       # заполнитель
stain = band_noise(0.8, 4.0, 3)          # слабые разводы
tone = base + 0.026 * mottle + 0.03 * grain - 0.04 * np.clip(stain - 0.6, 0, None)

# Каверны: редкие тёмные точки 2–4 мм
pits = rng.random((N, N)) < 0.0012
pits_soft = np.real(np.fft.ifft2(np.fft.fft2(pits.astype(np.float32)) * np.fft.fft2(
    np.exp(-(np.minimum(x, N - x) ** 2 + np.minimum(y, N - y) ** 2) / 1.2))))
tone -= 0.18 * np.clip(pits_soft, 0, 1)

# Грязь в швах и у кромок: темнее в пределах ~6 см
edge_dirt = np.exp(-dseam / (0.06 * PX_M))
tone -= 0.08 * edge_dirt * (0.6 + 0.4 * np.clip(band_noise(0.1, 0.6, 4), -1, 1))
groove = np.clip(1 - dseam / SEAM_W, 0, 1)
tone -= 0.25 * groove

# Тонкие трещины: несколько случайных блужданий по плитам
cracks = np.zeros((N, N), np.float32)
for _ in range(9):
    px, py = rng.uniform(0, N, 2)
    ang = rng.uniform(0, 2 * np.pi)
    for _ in range(int(rng.uniform(80, 260))):
        ang += rng.normal(0, 0.25)
        px = (px + np.cos(ang) * 1.5) % N
        py = (py + np.sin(ang) * 1.5) % N
        cracks[int(py), int(px)] = 1
cracks_soft = np.clip(np.real(np.fft.ifft2(np.fft.fft2(cracks) * np.fft.fft2(
    np.exp(-(np.minimum(x, N - x) ** 2 + np.minimum(y, N - y) ** 2) / 0.6)))), 0, 1)
tone -= 0.12 * cracks_soft

tone = np.clip(tone, 0.05, 0.95)
# чуть тёплый оттенок (портландцемент), sRGB
rgb = np.stack([tone * 1.02, tone * 1.0, tone * 0.96], -1)
Image.fromarray((np.clip(rgb, 0, 1) * 255).astype(np.uint8)).save(OUT + "Concrete.png")

# Нормали: шов — канавка, заполнитель и каверны — мелкий рельеф. Высота в мм → наклон.
h = -6.0 * groove - 1.0 * np.clip(pits_soft, 0, 1) * 3 + 0.25 * grain - 1.5 * cracks_soft + 0.6 * mottle
h_m = h / 1000.0
gx = (np.roll(h_m, -1, 1) - np.roll(h_m, 1, 1)) * PX_M / 2
gy = (np.roll(h_m, -1, 0) - np.roll(h_m, 1, 0)) * PX_M / 2
STRENGTH = 4.0   # усиление: мм-рельеф иначе невидим под косым Солнцем
nx, ny, nz = -gx * STRENGTH, gy * STRENGTH, np.ones_like(gx)
ln = np.sqrt(nx * nx + ny * ny + nz * nz)
nrm = np.stack([nx / ln, ny / ln, nz / ln], -1) * 0.5 + 0.5
Image.fromarray((nrm * 255).astype(np.uint8)).save(OUT + "ConcreteNormal.png")
print("ok", tone.mean(), tone.min(), tone.max())
