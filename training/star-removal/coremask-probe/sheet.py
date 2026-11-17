"""Every core the mask protects, as a crop of its master (220 px a side at 1:1 or shrunk to hold the core) with the mask's
0.5 contour in red. Sheets of 48."""
import json, os, sys
import numpy as np
from astropy.io import fits
from PIL import Image, ImageDraw
# python sheet.py <survey.jsonl> <session-masters folder> <probe out dir>; the sheets are written beside the survey
SURVEY, S, OUT = sys.argv[1], sys.argv[2], sys.argv[3]
R = [json.loads(l) for l in open(SURVEY, encoding='utf-8')]
W = [r for r in R if r['cores']]
tot_in, tot_all = {}, {}
for r in R:
    for k, v in r['inside'].items(): tot_in[k] = tot_in.get(k, 0) + v
print(len(R), 'masters;', len(W), 'with a core;', sum(len(r['cores']) for r in W), 'cores')
print('stars inside', tot_in)
tiles = []
for r in sorted(W, key=lambda r: r['stem']):
    d = np.asarray(fits.getdata(f"{S}/{r['stem']}.fits"), dtype=np.float32)
    if d.ndim == 2: d = d[None]
    d = np.nan_to_num(d)
    w = np.fromfile(os.path.join(OUT, r['stem'] + ".weight.u8"), dtype=np.uint8).reshape(r['Height'], r['Width'])
    for c in r['cores']:
        half = int(max(110, 1.6 * np.sqrt(c['CoreAreaPx'])))
        y0, y1, x0, x1 = max(0, c['Y'] - half), min(r['Height'], c['Y'] + half), max(0, c['X'] - half), min(r['Width'], c['X'] + half)
        crop = d[:, y0:y1, x0:x1]; m = w[y0:y1, x0:x1] >= 128
        lo = np.percentile(crop, 2); hi = np.percentile(crop, 99.9)
        x = np.clip((crop - lo) / max(hi - lo, 1e-12), 0, 1)
        x = np.arcsinh(x * 20) / np.arcsinh(20)
        rgb = (255 * (x if x.shape[0] == 3 else np.repeat(x[:1], 3, 0))).astype(np.uint8).transpose(1, 2, 0).copy()
        edge = m & ~(np.roll(m, 1, 0) & np.roll(m, -1, 0) & np.roll(m, 1, 1) & np.roll(m, -1, 1))
        rgb[edge] = (255, 40, 40)
        im = Image.fromarray(rgb).resize((220, 220), Image.LANCZOS)
        ins = r['inside']
        bright = sum(v for k, v in ins.items() if k in ('100-1000', '1000+', 'saturated'))
        tiles.append((im, r['stem'], f"pk {c['peak']:.0f}  {c['CoreAreaPx']} px  stars in {sum(ins.values())}, bright {bright}"))
per = 48
for s in range(0, len(tiles), per):
    sheet = Image.new('RGB', (8 * 228 + 8, 6 * 262 + 8), (18, 18, 18)); dr = ImageDraw.Draw(sheet)
    for i, (im, stem, cap) in enumerate(tiles[s:s + per]):
        x, y = 8 + (i % 8) * 228, 8 + (i // 8) * 262
        sheet.paste(im, (x, y + 30))
        parts = stem.split('_')
        dr.text((x, y), f"{s + i + 1}. {'_'.join(parts[2:4])[:34]}", fill=(235, 235, 235))
        dr.text((x, y + 14), cap[:40], fill=(170, 200, 255))
    sheet.save(os.path.join(OUT, f"cores-{s // per + 1}.png")); print('sheet', s // per + 1)
print(len(tiles), 'tiles')
