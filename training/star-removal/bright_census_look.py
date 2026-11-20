"""The census's candidates to look at (2026-11-20): 800 px crops at a third of the scale, one tile a star.
    python -I bright_census_look.py <store> <census tsv> <out png> [how many, default 30] [min 100-140 excess, default 5]
Tiles in the order of the 100-140 px excess, largest first; each through its own stretch (the crop's median less 2 sigma
to its 99.7th percentile, asinh). Prints the list with a number a tile, so a look can name them."""
import csv, sys
import numpy as np
from astropy.io import fits
from PIL import Image, ImageDraw

STORE, TSV, OUT = sys.argv[1:4]
N = int(sys.argv[4]) if len(sys.argv) > 4 else 30
MIN = float(sys.argv[5]) if len(sys.argv) > 5 else 5.0
rows = [r for r in csv.DictReader(open(TSV, encoding="utf-8"), delimiter="\t") if float(r["r100_140"]) >= MIN]
rows.sort(key=lambda r: -float(r["r100_140"]))
rows = rows[:N]
R, T = 400, 266
tiles = []
for i, r in enumerate(rows):
    x, y = int(r["x"]), int(r["y"])
    with fits.open(f"{STORE}/session-masters/{r['session']}.fits", memmap=True) as h:
        d = h[0].data
        c = np.asarray(d[..., y - R:y + R, x - R:x + R], np.float32)
    c = np.nan_to_num(c[None] if c.ndim == 2 else c[:3])
    c = np.repeat(c, 3, axis=0) if c.shape[0] == 1 else c
    k = 3
    hh, ww = c.shape[1] // k * k, c.shape[2] // k * k
    small = c[:, :hh, :ww].reshape(3, hh // k, k, ww // k, k).mean(axis=(2, 4))
    lo = np.median(small, axis=(1, 2)) - 2 * 1.4826 * np.median(np.abs(small - np.median(small, axis=(1, 2), keepdims=True)), axis=(1, 2))
    hi = np.maximum(np.percentile(small.reshape(3, -1), 99.7, axis=1), lo + 1e-6)
    t = np.clip((small - lo[:, None, None]) / (hi - lo)[:, None, None], 0, 1)
    im = Image.fromarray((255 * np.arcsinh(t * 20) / np.arcsinh(20)).astype(np.uint8).transpose(1, 2, 0)).resize((T, T))
    ImageDraw.Draw(im).text((4, 2), f"{i + 1}", fill=(255, 255, 0))
    tiles.append(im)
    print(f"{i + 1:2d}  100-140 {float(r['r100_140']):6.1f}  spread {r['spread_100_140']:>5s}  peak {r['peak_sigma']:>6s}  {r['session'][:70]}  ({x},{y})")
cols = 6
sheet = Image.new("RGB", (cols * (T + 2), ((len(tiles) + cols - 1) // cols) * (T + 2)), (0, 0, 0))
for i, im in enumerate(tiles):
    sheet.paste(im, ((i % cols) * (T + 2), (i // cols) * (T + 2)))
sheet.save(OUT)
print(OUT)
