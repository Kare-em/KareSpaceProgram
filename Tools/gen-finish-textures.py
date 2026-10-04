"""
Отделки обшивки (GDD §9.5): сгенерированный альбедо -> бесшовный тайл + Normal + Mask для HDRP/Lit.
  python Tools/gen-finish-textures.py [отделка ...]   (без аргументов — все)
Исходники — Tools/textures/hull_src/<src>.jpg (ответы Tools/gen-texture.mjs, уменьшены до 2048; промпты — PROMPTS ниже).
Выход — Assets/_Project/Textures/Hull/<Отделка>/<Отделка>_Albedo|Normal|Mask.png, 1024², бесшовные.
Материалы VesselLit_<Отделка>.mat собирает FlightSceneBuilder.HullFinishes, выбор отделки детали — VesselView.FinishOf.

Пары (меняешь одно — проверяй второе):
  TILE_M здесь ничего не масштабирует — тайл в метрах задаёт FlightSceneBuilder.FinishTileMeters (подобран под
  размер рисунка: плитка 15 см × 8 = 1,2 м и т.п.); normal-сила подобрана под этот тайл.
  metal/smooth пишутся прямо в Mask (R, A): у материалов remap 0…1 (FlightSceneBuilder.FinishMaterial).
  tint = True — альбедо обесцвечено до почти белого, цвет даёт палитра VesselView через MPB (_BaseColor);
  tint = False — цвет в текстуре, VesselView ставит _BaseColor белым (VesselView.SelfColored).
"""
import os
import sys
import numpy as np
from PIL import Image

sys.path.insert(0, os.path.dirname(__file__))
from importlib import import_module
mt = import_module("make-tileable")

ROOT = os.path.join(os.path.dirname(__file__), "..")
SRC = os.path.join(ROOT, "Tools", "textures", "hull_src")
OUT = os.path.join(ROOT, "Assets", "_Project", "Textures", "Hull")
SIZE = 1024

# Общая часть промпта (gen-texture.mjs): вид строго сверху, без перспективы и бликов, ровный свет.
COMMON = ("Seamless tileable PBR albedo texture, orthographic top-down view straight onto a flat surface, perfectly flat, "
          "no perspective, no vanishing point, no directional lighting, no cast shadows, no specular highlights, no reflections, "
          "uniform flat even diffuse illumination. Fills the entire square frame edge to edge, no text, no logos, no markings, "
          "no border, no frame. Subject: ")

# src — файл в hull_src; lum — средняя яркость тонируемого альбедо; sat — сколько цвета оставить (0 — серый);
# h — сила нормали (знак: + светлое выпуклое); metal/smooth — Mask R/A; svar — разброс гладкости по яркости;
# ao — сила AO в швах; det — маска детали (Mask B); hblur — размытие высоты, px (шумные исходники).
FINISHES = {
    "Painted":    dict(src="painted_2",   tint=True,  lum=0.86, sat=0.15, h=2.5,  metal=0.0,  smooth=0.42, svar=0.25, ao=3.0, det=1.0),
    "Stringer":   dict(src="stringer_1",  tint=True,  lum=0.80, sat=0.0,  h=4.0,  metal=0.85, smooth=0.45, svar=0.2,  ao=3.0, det=1.0),
    "Steel":      dict(src="steel_1",     tint=True,  lum=0.85, sat=0.1,  h=1.5,  metal=1.0,  smooth=0.68, svar=0.2,  ao=2.0, det=0.6),
    "Foam":       dict(src="foam_1",      tint=False, h=1.5,  hblur=2.0, metal=0.0,  smooth=0.18, svar=0.1,  ao=2.5, det=0.3),
    "TilesBlack": dict(src="tiles_2",     tint=False, h=-3.0, metal=0.0,  smooth=0.22, svar=0.15, ao=3.0, det=0.3),
    "TilesWhite": dict(src="tilesw_1",    tint=False, h=3.0,  metal=0.0,  smooth=0.25, svar=0.15, ao=3.0, det=0.3),
    "Ablative":   dict(src="ablative_1",  tint=False, h=3.0,  hblur=1.5, metal=0.0,  smooth=0.12, svar=0.15, ao=3.5, det=0.3),
    "FoilGold":   dict(src="foilgold_1",  tint=False, h=4.0,  metal=0.75, smooth=0.55, svar=0.2,  ao=2.0, det=0.0),
    "FoilSilver": dict(src="foilsilver_1", tint=False, h=4.0, metal=0.75, smooth=0.55, svar=0.2,  ao=2.0, det=0.0),
}

PROMPTS = {
    "painted_2": "painted aluminium skin of a space launch vehicle stage, off-white very light grey matte paint, a few LARGE smooth "
                 "panels with very thin barely visible seams, sparse tiny flush rivet lines only along the seams, faint vertical grime "
                 "and soot streaks, light weathering, subtle paint chips and fine scratches, mostly clean uniform surface, realistic NASA hardware photo detail.",
    "stringer_1": "bare unpainted aluminium alloy rocket interstage skin with external vertical stringers and corrugation, evenly spaced "
                  "parallel vertical corrugated ribs running top to bottom, rows of small rivets along the ribs, brushed mill-finish grey metal, "
                  "light oxidation and handling smudges, realistic aerospace hardware.",
    "steel_1": "polished stainless steel balloon propellant tank skin of the Atlas rocket, satin finish stainless sheet with faint horizontal "
               "circumferential weld seams, fine brushed grain, subtle warm and cool grey variation, light fingerprints and smudges, realistic close photo detail, evenly lit.",
    "foam_1": "orange-brown spray-on polyurethane foam thermal insulation of the Space Shuttle external tank, rust orange colour, slightly bumpy "
              "organic foam texture with subtle swirls and spray rind, small variations in tone, realistic close photo detail.",
    "tiles_2": "Space Shuttle orbiter belly black HRSI thermal protection tiles, a perfectly regular square grid of exactly 8 by 8 identical square "
               "matte black ceramic tiles, thin straight light grey gaps between tiles, each tile very dark charcoal black with subtle mottling and "
               "slight tone differences between tiles, a few faint pale scuffs, realistic close photo.",
    "tilesw_1": "Buran orbiter upper surface white thermal protection tiles, a perfectly regular square grid of exactly 8 by 8 identical square matte "
                "off-white ceramic tiles, thin straight grey gaps between tiles, each tile white to light cream with subtle mottling and slight tone "
                "differences between tiles, a few faint grey smudges, realistic close photo.",
    "ablative_1": "charred ablative heat shield of a spacecraft re-entry capsule after re-entry, dark brown and black burned resin surface with "
                  "honeycomb pattern faintly visible, cracks, flaking char, soot, small bubbles, uneven brown tones, realistic close photo detail.",
    "foilgold_1": "crinkled gold multi-layer insulation foil (kapton MLI blanket) of a lunar lander, wrinkled gold metallic film with soft folds "
                  "and creases, small seams with stitching, subtle variation from gold to amber, realistic close photo detail, evenly lit.",
    "foilsilver_1": "crinkled silver aluminized mylar multi-layer insulation blanket of a space probe, wrinkled silver metallic film with soft "
                    "folds and creases, small taped seams, subtle variation in grey tones, realistic close photo detail, evenly lit.",
}


def save(a, path):
    Image.fromarray(np.clip(a * 255 + 0.5, 0, 255).astype(np.uint8)).save(path)


def build(name, f):
    print(name)
    src = np.asarray(Image.open(os.path.join(SRC, f["src"] + ".jpg")).convert("RGB"), dtype=np.float32) / 255
    alb = mt.make_tileable(src, SIZE)
    l = mt.lum(alb)
    if f["tint"]:
        # Почти белый: цвет ступени умножается из MPB. Цвет оставляем долей sat, яркость — к средней lum.
        g = l[..., None]
        alb = g + (alb - g) * f["sat"]
        alb = alb * (f["lum"] / alb.mean())
    # Высота — яркость без крупных пятен (иначе вся панель «вздувается»), нормаль — её градиент.
    hgt = l - mt.blur_wrap(l, SIZE / 32)
    if f.get("hblur"):
        # Пена и абляция — пиксельный шум яркости; без размытия нормаль «серая» (наклонена везде).
        hgt = mt.blur_wrap(hgt, f["hblur"])
    hgt = hgt / (np.std(hgt) + 1e-6) * 0.02 * f["h"]
    dx = (np.roll(hgt, -1, 1) - np.roll(hgt, 1, 1)) * 0.5 * SIZE / 64
    dy = (np.roll(hgt, -1, 0) - np.roll(hgt, 1, 0)) * 0.5 * SIZE / 64
    # GL (Y вверх): строки картинки идут вниз, поэтому n.y = +dh/d(строка).
    n = np.stack([-dx, dy, np.ones_like(hgt)], -1)
    n /= np.linalg.norm(n, axis=-1, keepdims=True)
    # AO: насколько точка ниже окрестности (швы, щели между плитками, складки).
    cav = np.maximum(mt.blur_wrap(hgt, 4) - hgt, 0)  # знак h уже учтён в hgt
    ao = np.clip(1 - cav / (np.percentile(cav, 99) + 1e-6) * 0.15 * f["ao"], 0.3, 1)
    # Гладкость: светлее (чище) — глаже, грязь и сажа — матовее.
    lz = (l - l.mean()) / (np.std(l) + 1e-6)
    sm = np.clip(f["smooth"] + f["svar"] * 0.3 * lz, 0.02, 0.95)
    mask = np.stack([np.full_like(l, f["metal"]), ao, np.full_like(l, f["det"]), sm], -1)
    d = os.path.join(OUT, name)
    os.makedirs(d, exist_ok=True)
    save(alb, os.path.join(d, f"{name}_Albedo.png"))
    save(n * 0.5 + 0.5, os.path.join(d, f"{name}_Normal.png"))
    Image.fromarray(np.clip(mask * 255 + 0.5, 0, 255).astype(np.uint8), "RGBA").save(os.path.join(d, f"{name}_Mask.png"))


if __name__ == "__main__":
    names = sys.argv[1:] or list(FINISHES)
    for k in names:
        build(k, FINISHES[k])
