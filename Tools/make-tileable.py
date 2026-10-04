"""
Бесшовный тайл из сгенерированной картинки (Tools/gen-texture.mjs тайлинг не гарантирует).
  python Tools/make-tileable.py <вход> <выход.png> [размер=1024]
Как модуль: make_tileable(np.float32 HxWx3, size) -> np.float32 size x size x 3 (0..1).

Шаги (каждый снимает свой вид шва):
 1. Обрезка по периоду: по каждой оси ищем ширину окна Wc (0,6…1 от исходной), при которой столбцы x и x+Wc
    совпадают лучше всего. У плиток, стрингеров, сварных поясов это целое число периодов — сетка замыкается
    без сдвига. У «органики» (пена, фольга, абляция) окно выходит случайным и ничего не портит.
 2. Выравнивание освещения: делим на сильно размытую яркость (σ = 1/6 тайла, с обёрткой). Модель рисует
    виньетку и плавный градиент даже при «flat lighting» — при повторе тайла это читается «шашечками».
 3. Смешивание краёв: сдвиг на полтайла и смешивание по маске с узкой полосой у краёв (FEATHER) — снимает
    остаточный шов после обрезки; только если шов ≥ SEAM_OK внутреннего (иначе смешивание лишь мылит). Полоса узкая, чтобы не было «двойных» швов панелей и рёбер.
"""
import sys
import numpy as np
from PIL import Image

MIN_SHARE = 0.6     # окно периода не меньше 60 % исходника — иначе слишком крупный тайл повторяется заметно
MAX_STRETCH = 0.06  # окно по осям различается не больше чем на 6 % — иначе тайл заметно растянут
PROBE = 24          # столбцов в сравнении x ↔ x+Wc
FLAT_SIGMA = 1 / 6  # σ размытия для выравнивания освещения, доля тайла
FEATHER = 0.12      # полоса смешивания у краёв, доля полутайла
SEAM_OK = 1.6       # шов «замкнут», если перепад через край < 1,6 среднего перепада соседних пикселей


def lum(a):
    return a[..., 0] * 0.2126 + a[..., 1] * 0.7152 + a[..., 2] * 0.0722


def blur_wrap(x, sigma):
    """Гаусс через FFT — с обёрткой, т.е. для уже периодической картинки (2D)."""
    h, w = x.shape
    fy = np.fft.fftfreq(h)[:, None]
    fx = np.fft.fftfreq(w)[None, :]
    g = np.exp(-2 * (np.pi * sigma) ** 2 * (fx ** 2 + fy ** 2))
    return np.real(np.fft.ifft2(np.fft.fft2(x) * g))


def best_period(l, axis, lo=None, hi=None):
    """Ширина окна в [lo, hi], при которой край замыкается лучше всего (по сглаженной яркости)."""
    a = l if axis == 1 else l.T
    w = a.shape[1]
    lo = max(int(w * MIN_SHARE), int(lo or 0))
    hi = min(w - PROBE, int(hi or w))
    best, best_err = w, None
    for wc in range(lo, hi):
        err = np.mean(np.abs(a[:, :PROBE] - a[:, wc:wc + PROBE]))
        if best_err is None or err < best_err:
            best, best_err = wc, err
    return best, best_err


def make_tileable(a, size=1024, crop=True, flatten=True):
    h, w, _ = a.shape
    # Обрезка считается на уменьшенной копии (быстро), окно масштабируется обратно.
    k = max(1, max(h, w) // 1024)
    if crop:
        small = lum(a[::k, ::k])
        wx, ex = best_period(small, 1)
        wy, ey = best_period(small, 0)
        if abs(wx / wy - 1) > MAX_STRETCH:
            # Окно сильно не квадратное — плитки стали бы прямоугольниками. Ищем пару в пределах MAX_STRETCH
            # от окна с меньшей ошибкой края.
            if ex <= ey:
                wy, ey = best_period(small, 0, wx * (1 - MAX_STRETCH), wx * (1 + MAX_STRETCH) + 1)
            else:
                wx, ex = best_period(small, 1, wy * (1 - MAX_STRETCH), wy * (1 + MAX_STRETCH) + 1)
        a = a[: wy * k, : wx * k]
        print(f"  окно {wx * k}x{wy * k} из {w}x{h} (ошибка края x {ex:.3f}, y {ey:.3f})")
    im = Image.fromarray(np.clip(a * 255, 0, 255).astype(np.uint8)).resize((size, size), Image.LANCZOS)
    a = np.asarray(im, dtype=np.float32) / 255
    if flatten:
        l = lum(a) + 1e-3
        low = blur_wrap(l, size * FLAT_SIGMA)
        a = a * (l.mean() / np.maximum(low, 1e-3))[..., None]
    d = np.minimum(np.arange(size), size - 1 - np.arange(size)) / (size / 2)  # 0 у края, 1 в центре
    t = np.clip(d / FEATHER, 0, 1)
    t = t * t * (3 - 2 * t)
    for axis in (1, 0):
        # Шов, который уже замкнут обрезкой, не трогаем: смешивание сдвинутой копии даёт «двойные» рёбра сетки.
        edge = np.mean(np.abs(np.take(a, 0, axis) - np.take(a, -1, axis)))
        inner = np.mean(np.abs(np.diff(a, axis=axis)))
        if edge < SEAM_OK * inner:
            continue
        s = np.roll(a, size // 2, axis)
        m = (t[None, :] if axis == 1 else t[:, None])[..., None]
        a = a * m + s * (1 - m)
    return np.clip(a, 0, 1)

if __name__ == "__main__":
    src, dst = sys.argv[1], sys.argv[2]
    size = int(sys.argv[3]) if len(sys.argv) > 3 else 1024
    a = np.asarray(Image.open(src).convert("RGB"), dtype=np.float32) / 255
    out = make_tileable(a, size)
    Image.fromarray((out * 255 + 0.5).astype(np.uint8)).save(dst)
    print("готово:", dst)
