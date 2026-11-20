"""The census's candidates to look at (2026-11-20): 800 px crops at a third of the scale, one tile a star.
    python -I bright_census_look.py <store> <census tsv> <out png> [how many, default 30] [min 100-140 excess, default 5]
                                    [min fall, default 0] [first, default 1] [fall under, default none]
Tiles in the order of the 100-140 px excess, largest first; each through its own stretch (the crop's median less 2 sigma
to its 99.7th percentile, asinh). <min fall> keeps the rows whose 8-22 px ring is at least that many times the
100-140 px ring (4 is the census's FALLING rows) and <fall under> those under that many (2 and 4 are the flat rows
nearest the cut); <first> starts the sheet at that row of the order, so a long list is looked at in sheets of
<how many>. A crop that runs off the frame is black there. Prints the list with a number a tile, so a look can name
them."""
import csv, sys
import numpy as np
from astropy.io import fits
from PIL import Image, ImageDraw

STORE, TSV, OUT = sys.argv[1:4]
N = int(sys.argv[4]) if len(sys.argv) > 4 else 30
MIN = float(sys.argv[5]) if len(sys.argv) > 5 else 5.0
FALL = float(sys.argv[6]) if len(sys.argv) > 6 else 0.0
FIRST = int(sys.argv[7]) if len(sys.argv) > 7 else 1
UNDER = float(sys.argv[8]) if len(sys.argv) > 8 else float("inf")
rows = [r for r in csv.DictReader(open(TSV, encoding="utf-8"), delimiter="\t")
        if float(r["r100_140"]) >= MIN and FALL * float(r["r100_140"]) <= float(r["r8_22"]) < UNDER * float(r["r100_140"])]
rows.sort(key=lambda r: -float(r["r100_140"]))
print(f"{len(rows)} rows at {MIN:g} sigma or more at 100-140 px" + (f" and a fall of {FALL:g} or more" if FALL else "")
      + (f" and under {UNDER:g}" if UNDER < float("inf") else "") + f"; this sheet from row {FIRST}")
R, T, K = 400, 266, 3
tiles = []
for i, r in list(enumerate(rows))[FIRST - 1:FIRST - 1 + N]:
    x, y = int(r["x"]), int(r["y"])
    with fits.open(f"{STORE}/session-masters/{r['session']}.fits", memmap=True) as h:
        d = h[0].data
        H, W = d.shape[-2:]
        y0, y1, x0, x1 = max(y - R, 0), min(y + R, H), max(x - R, 0), min(x + R, W)
        part = np.nan_to_num(np.asarray(d[..., y0:y1, x0:x1], np.float32))
    part = part[None] if part.ndim == 2 else part[:3]
    part = np.repeat(part, 3, axis=0) if part.shape[0] == 1 else part
    seen = part.reshape(3, -1)
    med = np.median(seen, axis=1)
    lo = med - 2 * 1.4826 * np.median(np.abs(seen - med[:, None]), axis=1)
    hi = np.maximum(np.percentile(seen, 99.7, axis=1), lo + 1e-6)
    c = np.broadcast_to(lo[:, None, None], (3, 2 * R, 2 * R)).copy()        # off the frame: the stretch's black
    c[:, y0 - (y - R):y1 - (y - R), x0 - (x - R):x1 - (x - R)] = part
    n = 2 * R // K * K
    small = c[:, :n, :n].reshape(3, n // K, K, n // K, K).mean(axis=(2, 4))
    t = np.clip((small - lo[:, None, None]) / (hi - lo)[:, None, None], 0, 1)
    im = Image.fromarray((255 * np.arcsinh(t * 20) / np.arcsinh(20)).astype(np.uint8).transpose(1, 2, 0)).resize((T, T))
    ImageDraw.Draw(im).text((4, 2), f"{i + 1}", fill=(255, 255, 0))
    tiles.append(im)
    print(f"{i + 1:3d}  100-140 {float(r['r100_140']):6.1f}  8-22 {float(r['r8_22']):7.1f}  spread {r['spread_100_140']:>5s}  peak {r['peak_sigma']:>6s}  ({x},{y})  {r['session']}")
cols = 6
sheet = Image.new("RGB", (cols * (T + 2), ((len(tiles) + cols - 1) // cols) * (T + 2)), (0, 0, 0))
for i, im in enumerate(tiles):
    sheet.paste(im, ((i % cols) * (T + 2), (i // cols) * (T + 2)))
sheet.save(OUT)
print(OUT)
