"""Where the faint stars a remover leaves are: Gaia BP 17-19 on a master, the level set aside (2026-11-20).

    python -I gaia_where.py <input master.fits> <check2 work dir> <judge label>=<starless> <label>=<starless> [...] [--sheet <png>]

Per catalogued star seen in the input (gaia_compact.py's rule: its 3 px disc over its own 16 to 24 px annulus, over 3 rms
sqrt n), the share each remover took with the level set aside (the taken light's disc less its annulus median, over the
input's). Removed = over half. The first remover is the judge. The stars are split three ways, and each split prints the
removed % per remover, the gap to the judge in points and that bin's part of the whole gap:

  crowding    Gaia stars to BP 19 within 24 px of the star (the star itself not counted), in fifths
  background  the input's annulus median over the frame's sky (the green channel's median), in the local rms
  contrast    the star's excess over its seeing threshold (3 rms sqrt n)

--sheet writes a picture to look at before the table is believed: up to six stars of the densest fifth, under twice
their threshold, that the judge removed and the SECOND remover left, 48 px at 6x, the star at the centre. Two rows a
star, each the input and then every remover: first all on the input crop's scale (its median less 3 to plus 9 of the
crop's own neighbour-difference sigma, which the stars inflate), so a level taken shows; then each panel about its OWN
median, less 4 to plus 8 of the plate's pixel rms there, so what is left shows as texture. Green channel throughout.
"""
import sys
from pathlib import Path

import numpy as np
from astropy.io import fits
from astropy.wcs import WCS
from scipy.spatial import cKDTree

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / 'denoise'))
import gaia_vizier  # noqa: E402

args = sys.argv[1:]
sheet = None
if '--sheet' in args:
    i = args.index('--sheet'); sheet = args[i + 1]; del args[i:i + 2]
inp, work = Path(args[0]), Path(args[1])
removers = [(a.split('=', 1)[0], Path(a.split('=', 1)[1])) for a in args[2:]]
if len(removers) < 2:
    sys.exit('a judge and at least one remover are needed')
hdr = fits.getheader(inp)
if hdr.get('PIXORIG') != 1:
    sys.exit(f'{inp.name}: no PIXORIG = 1, so the WCS convention is unknown; refusing')
data = fits.getdata(inp).astype(np.float64)
if data.ndim != 3:
    sys.exit(f'{inp.name}: a three-channel master is needed (the green channel is read)')
H, W = data.shape[-2:]
w = WCS(hdr, naxis=2)
corners = w.all_pix2world([[0, 0], [W - 1, 0], [0, H - 1], [W - 1, H - 1]], 0)
ra, dec = corners[:, 0], corners[:, 1]
gra, gdec, gmag = gaia_vizier.fetch_box((ra.min() + ra.max()) / 2, (dec.min() + dec.max()) / 2,
                                        ra.max() - ra.min() + 0.1, dec.max() - dec.min() + 0.1, mag_max=19.0)
px, py = w.all_world2pix(gra, gdec, 0)
on = (px >= 0) & (px < W) & (py >= 0) & (py < H)
px, py, gmag = px[on], py[on], gmag[on]
ix, iy = np.clip(np.round(px).astype(int), 0, W - 1), np.clip(np.round(py).astype(int), 0, H - 1)
rms = fits.getdata(work / f'{inp.stem}_c1.rms.fits').astype(np.float64)
tree = cKDTree(np.column_stack([px, py]))
yy, xx = np.mgrid[-24:25, -24:25]
rr = np.hypot(xx, yy)
disc, ann = rr < 3, (rr >= 16) & (rr < 24)
keep = (ix >= 24) & (ix < W - 24) & (iy >= 24) & (iy < H - 24)
green = data[1]
sky = float(np.nanmedian(green[np.isfinite(green) & (green != 0)]))
sel = np.where(keep & (gmag >= 17) & (gmag < 19))[0]
if len(sel) > 40000:
    sel = np.random.default_rng(1).choice(sel, 40000, replace=False)   # gaia_compact.py's draw, so the totals agree
plates = []
for label, path in removers:
    st = fits.getdata(path).astype(np.float64)
    plates.append(st[1] if st.ndim == 3 else st)
rows = []
for k in sel:
    x, y = ix[k], iy[k]
    box = (slice(y - 24, y + 25), slice(x - 24, x + 25))
    win = green[box]
    amed = np.median(win[ann])
    excess = (win[disc] - amed).sum()
    thr = 3 * rms[y, x] * np.sqrt(disc.sum())
    if excess > thr:
        shares = []
        for p in plates:
            t = win - p[box]
            shares.append(float((t[disc] - np.median(t[ann])).sum() / excess))
        rows.append([len(tree.query_ball_point([px[k], py[k]], 24)) - 1, (amed - sky) / rms[y, x], excess / thr, x, y] + shares)
rows = np.array(rows)
if len(rows) < 100:
    sys.exit(f'{inp.name}: only {len(rows)} stars of BP 17-19 seen; too few to split')
S = 5   # the first share's column
print(f"{inp.name}: {len(rows)} stars of BP 17-19 seen; removed over half with the level set aside: "
      + ", ".join(f"{lab} {100 * np.mean(rows[:, S + i] > 0.5):.1f}" for i, (lab, _) in enumerate(removers)))


def split(col, title, edges, fmt):
    print(f"\n{title}")
    print(f"{'bin':>16s} {'stars':>7s} {'share':>6s}  " + "  ".join(f"{lab:>14s}" for lab, _ in removers)
          + "   then per remover: its gap to the judge in points / the bin's part of the whole gap")
    for lo, hi in zip(edges[:-1], edges[1:]):
        m = (rows[:, col] >= lo) & (rows[:, col] < hi)
        if m.sum() < 30:
            continue
        r = [100 * np.mean(rows[m, S + i] > 0.5) for i in range(len(removers))]
        tail = "".join(f"   {lab}: {r[0] - r[i]:+5.1f} / {(r[0] - r[i]) * m.sum() / len(rows):+5.2f}" for i, (lab, _) in enumerate(removers) if i > 0)
        print(f"{fmt(lo, hi):>16s} {m.sum():7d} {100 * m.sum() / len(rows):5.1f}%  " + "  ".join(f"{v:14.1f}" for v in r) + tail)


fifths = np.unique(np.quantile(rows[:, 0], np.linspace(0, 1, 6)))
fifths[-1] += 1
split(0, "by crowding: catalogued stars to BP 19 within 24 px", fifths, lambda lo, hi: f"{lo:.0f} to {hi - 1:.0f}")
split(1, "by background: the annulus median over the frame's sky, in local rms", np.array([-np.inf, 2, 5, 10, 20, 50, np.inf]),
      lambda lo, hi: f"under {hi:.0f}" if np.isinf(lo) else f"{lo:.0f} and over" if np.isinf(hi) else f"{lo:.0f} to {hi:.0f}")
split(2, "by contrast: the star's excess over its seeing threshold (3 rms sqrt n)", np.array([1, 1.5, 2, 3, 5, 10, np.inf]),
      lambda lo, hi: f"{lo:.0f} and over" if np.isinf(hi) else f"{lo:.1f} to {hi:.1f}")

if sheet:
    from PIL import Image, ImageDraw
    worst = rows[(rows[:, 0] >= fifths[-2]) & (rows[:, 2] < 2) & (rows[:, S] > 0.5) & (rows[:, S + 1] < 0.5)]
    if len(worst) == 0:
        sys.exit('no star of the densest fifth under twice its threshold was removed by the judge and left by the second remover; no sheet')
    pick = worst[np.random.default_rng(2).choice(len(worst), min(6, len(worst)), replace=False)]
    images = [green] + plates

    def tile(a, lo, hi):
        return np.pad(np.kron((np.clip((a - lo) / (hi - lo), 0, 1) * 255).astype(np.uint8), np.ones((6, 6), np.uint8)), 2, constant_values=255)

    out = []
    for r in pick:
        x, y = int(r[3]), int(r[4])
        box = (slice(y - 24, y + 24), slice(x - 24, x + 24))
        ref = green[box]
        sig = 1.4826 * np.median(np.abs(np.diff(ref, axis=1))) / np.sqrt(2)
        out.append(np.concatenate([tile(a[box], np.median(ref) - 3 * sig, np.median(ref) + 9 * sig) for a in images], axis=1))
        out.append(np.concatenate([tile(a[box], np.median(a[box]) - 4 * rms[y, x], np.median(a[box]) + 8 * rms[y, x]) for a in images], axis=1))
    body = np.concatenate(out, axis=0)
    head = Image.new("L", (body.shape[1], 30), 255)
    draw = ImageDraw.Draw(head)
    for i, lab in enumerate(["input"] + [lab for lab, _ in removers]):
        draw.text((i * 292 + 6, 2), lab, fill=0)
    draw.text((6, 16), "per star two rows: all on the input's scale (level), then each about its own median, -4 to +8 pixel rms (texture)", fill=0)
    Image.fromarray(np.concatenate([np.asarray(head), body], axis=0)).save(sheet)
    print(f"\n{sheet}: {len(pick)} of {len(worst)} such stars, two rows each: input, " + ", ".join(lab for lab, _ in removers)
          + "; the star is at the centre of each 48 px tile")
