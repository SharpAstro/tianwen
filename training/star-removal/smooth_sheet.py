"""One captioned sheet of what a remover does to a smoothed master, beside what it does to the master itself (#2341).

    python -I smooth_sheet.py <x> <y> <size> <label> <out.png> <master> <its plate> <smoothed master> <its plate>

Four panels at 1:1 from one crop (array columns x, rows y, the crop's centre): the master, the remover's plate of it,
the master smoothed (smoothed_black_point.py writes that copy), the remover's plate of the smoothed copy. One stretch
for all four (asinh, black at the crop's 1st percentile of the master less 3 sky sigma, the channels linked), so a
level that moved shows as a level that moved. Under each panel: the crop's median against the master's, in the
master's sky noise.
"""
import sys

import numpy as np
from astropy.io import fits
from PIL import Image, ImageDraw, ImageFont

x, y, size, label, out = int(sys.argv[1]), int(sys.argv[2]), int(sys.argv[3]), sys.argv[4], sys.argv[5]
NAMES = ("the master", "the plate of the master", "the master smoothed", "the plate of the smoothed copy")
paths = list(zip(NAMES, sys.argv[6:10]))
if len(paths) != 4:
    raise SystemExit(__doc__)


def crop(path):
    d = np.asarray(fits.getdata(path)).astype(np.float64)
    d = d[None] if d.ndim == 2 else d
    h = size // 2
    c = d[:, y - h:y + h, x - h:x + h]
    return np.repeat(c, 3, axis=0) if c.shape[0] == 1 else c


crops = [crop(p) for _, p in paths]
ref = np.nan_to_num(crops[0])
sigma = 1.4826 / np.sqrt(2) * np.median(np.abs(np.diff(ref, axis=2)))
black = np.percentile(ref, 1) - 3 * sigma
white = np.percentile(ref, 99.7)
soft = 8 * sigma


def show(c):
    v = np.arcsinh(np.clip(np.nan_to_num(c) - black, 0, None) / soft) / np.arcsinh((white - black) / soft)
    return (np.clip(v, 0, 1).transpose(1, 2, 0) * 255).astype(np.uint8)


scale = 2 if size <= 300 else 1
pad, cap = 8, 40
tile = size * scale
sheet = Image.new("RGB", (4 * tile + 5 * pad, tile + cap + 34 + 2 * pad), (24, 24, 24))
draw = ImageDraw.Draw(sheet)
try:
    font = ImageFont.truetype("arial.ttf", 15)
    big = ImageFont.truetype("arialbd.ttf", 17)
except OSError:
    font = big = ImageFont.load_default()
draw.text((pad, pad), f"{label}: crop of {size} px at x {x}, y {y}, shown at {scale}x, one stretch for all four panels",
          fill=(235, 235, 235), font=big)
for i, ((name, _), c) in enumerate(zip(paths, crops)):
    x0 = pad + i * (tile + pad)
    sheet.paste(Image.fromarray(show(c)).resize((tile, tile), Image.NEAREST), (x0, 34 + pad))
    level = (float(np.nanmedian(c)) - float(np.nanmedian(crops[0]))) / sigma
    draw.text((x0, 34 + pad + tile + 4), name, fill=(235, 235, 235), font=font)
    draw.text((x0, 34 + pad + tile + 21), f"median {level:+.1f} master sigma against the master", fill=(170, 170, 170), font=font)
sheet.save(out)
print(out, "sky sigma of the crop", f"{sigma:.3g}")
