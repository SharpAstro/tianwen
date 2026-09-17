"""Render the suspect ASI462 dark sets beside their bias and a real light from the same camera.

Two tiles per frame: a FIXED linear 0..2000 ADU scale shared by every row (so levels compare), and
the frame's own p1..p99.5 stretch (so structure shows: a gradient means light, a uniform lift or a
corner glow means heat). 4x4 block means, which also fold the Bayer mosaic out. READ ONLY.
"""
import csv
import os
import numpy as np
import astropy.io.fits as fits
from PIL import Image, ImageDraw

GAP = r'C:/temp/e2/stage0-gap.csv'
OUT = r'C:/temp/e2/asi462-darks-sample.png'
FIXED_MAX = 2000.0

paths = {}
for r in csv.DictReader(open(GAP, encoding='utf-8')):
    paths.setdefault(os.path.dirname(r['path']), []).append(r['path'])
for v in paths.values():
    v.sort(key=str.lower)


def frames(dir_suffix, which):
    d = next(k for k in paths if k.replace('\\', '/').endswith(dir_suffix))
    ps = paths[d]
    pick = {'first': ps[0], 'middle': ps[len(ps) // 2], 'last': ps[-1]}
    return [(w, pick[w]) for w in which]


rows = []
rows += frames('Bias_ZWO_ASI462_230g_16b/2021-02-18T10_11_09', ['middle'])
rows += frames('Dark_ZWO_ASI462_10s_230g_16b/2021-02-18T09_34_59', ['first', 'middle', 'last'])
rows += frames('Dark_ZWO_ASI462_7s_307g_16b/2021-02-15T09_42_01', ['middle'])
rows += frames('Dark_ZWO_ASI462_2s_85g_16b/2021-02-10T12_10_16', ['middle'])
rows += frames('Cen A 10s RGB/2021-02-18T23_20_42/rawframes', ['middle'])


def blocks(a, f=4):
    h, w = (a.shape[0] // f) * f, (a.shape[1] // f) * f
    return a[:h, :w].reshape(h // f, f, w // f, f).mean(axis=(1, 3))


def to8(a, lo, hi):
    return Image.fromarray((np.clip((a - lo) / max(hi - lo, 1e-9), 0, 1) * 255).astype(np.uint8))


tiles = []
for which, p in rows:
    h = fits.getheader(p)
    a = blocks(np.asarray(fits.getdata(p), dtype=np.float64))
    lo, hi = np.percentile(a, 1), np.percentile(a, 99.5)
    # Edge-to-centre: the mean of the outer 10% border against the central 50%.
    H, W = a.shape
    centre = np.median(a[H // 4:3 * H // 4, W // 4:3 * W // 4])
    left, right = np.median(a[:, :W // 10]), np.median(a[:, -W // 10:])
    top, bottom = np.median(a[:H // 10, :]), np.median(a[-H // 10:, :])
    label = (f"{os.path.basename(os.path.dirname(p)) if 'rawframes' not in p else 'Cen A 10s g230 LIGHT'}  [{which}]  "
             f"exp={h.get('EXPTIME')}s gain={h.get('GAIN')} T={h.get('CCD-TEMP')}C  {str(h.get('DATE-OBS'))[:19]}")
    stats = (f"median {np.median(a):.0f} ADU   centre {centre:.0f}  L {left:.0f} R {right:.0f} T {top:.0f} B {bottom:.0f}")
    print(label)
    print('   ', stats)
    tiles.append((label, stats, to8(a, 0, FIXED_MAX), to8(a, lo, hi)))

tw, th = tiles[0][2].size
pad, text_h = 8, 34
canvas = Image.new('L', (2 * tw + 3 * pad, len(tiles) * (th + text_h + pad) + pad), 40)
draw = ImageDraw.Draw(canvas)
y = pad
for label, stats, fixed, own in tiles:
    draw.text((pad, y), label, fill=235)
    draw.text((pad, y + 14), stats + '     left: fixed 0..2000 ADU   right: own p1..p99.5', fill=180)
    y += text_h
    canvas.paste(fixed, (pad, y))
    canvas.paste(own, (2 * pad + tw, y))
    y += th + pad
canvas.save(OUT)
print('wrote', OUT, canvas.size)
