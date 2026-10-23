"""Star removers on whole real masters, judged against the INPUT's own background, never against a ring the remover also
touched. The old checks (real-star-check.py, remover-compare.py) read a star's core against a ring 6-10 px out, so a
hole wider than that ring reads flat: R2p dug NGC 253's brightest star to -1.2 sigma at the core and -0.7 at 6-10 px, and
that check called it flat (2026-10-20). Glue only: Tianwen's detector (`tianwen image sources --maps --csv`, run per
channel on the input) gives the stars, the smooth background map and the noise map; this compares.

Per star (compact segment on green), per channel, the starless plate less the input's background map, in the noise
map's sigma, averaged over rings 0-3, 3-6, 6-10, 10-14 px:
  - dug: any channel's mean inside 6 px below -0.5 sigma; light left: above +0.5; else flat. (A 6 px disc is ~113
    pixels, so 0.5 sigma is several times the mean's own noise even with drizzle's correlation.)
  - the old ring-relative verdict beside it (3x3 core against the 6-10 px ring, green, +-1), to show what it missed.
  - background: SMOOTH-BRIGHT where the input's background stands 1.5 sigma above the frame's sky (a galaxy, a nebula,
    the LMC's own light), else dark sky; CROWDED where 3 or more sources lie within 15 px.
Per 128 px cell, the starless plate's fine-scale roughness (MAD of itself less a 7 px median) over the noise map:
  the densest tenth of cells (by the input's source count) against the sparsest half, so mottling where stars were
  shows as a ratio above the empty sky's.

    python remover_check2.py <input master.fits> <work dir> <label=starless.fits> [...] --cli <tianwen executable>
(--cli, or the TIANWEN_CLI environment variable: the `tianwen` that runs `image sources`.)
"""
import argparse
import csv
import os
import subprocess
import sys
from pathlib import Path

import numpy as np
from astropy.io import fits
from scipy.ndimage import median_filter

ap = argparse.ArgumentParser()
ap.add_argument('master')
ap.add_argument('work')
ap.add_argument('removers', nargs='+', metavar='label=starless.fits')
ap.add_argument('--cli', default=os.environ.get('TIANWEN_CLI'))
args = ap.parse_args()
if not args.cli:
    sys.exit('no tianwen executable: give --cli or set TIANWEN_CLI')
CLI = args.cli
inp, work = Path(args.master), Path(args.work)
removers = [(a.split('=', 1)[0], Path(a.split('=', 1)[1])) for a in args.removers]
work.mkdir(parents=True, exist_ok=True)
data = fits.getdata(inp).astype(np.float64)
H, W = data.shape[-2:]

bg, rms = [], []
for c in range(3):
    plane = work / f'{inp.stem}_c{c}.fits'
    if not (work / f'{plane.stem}.rms.fits').exists():
        fits.PrimaryHDU(data[c].astype(np.float32)).writeto(plane, overwrite=True)
        subprocess.run([CLI, 'image', 'sources', str(plane), '--maps', '--csv', '--sigma', '5', '-o', str(work), '--top', '0'],
                       capture_output=True, text=True, check=True)
    bg.append(fits.getdata(work / f'{plane.stem}.background.fits').astype(np.float64))
    rms.append(fits.getdata(work / f'{plane.stem}.rms.fits').astype(np.float64))
bg, rms = np.array(bg), np.array(rms)
sky_level = np.median(bg[1])
rows = [r for r in csv.DictReader(open(work / f'{inp.stem}_c1.sources.csv')) if r['compact'] == '1']
S = np.array([[float(r['x']), float(r['y']), float(r['peak_snr'])] for r in rows])
S = S[(S[:, 0] > 30) & (S[:, 0] < W - 30) & (S[:, 1] > 30) & (S[:, 1] < H - 30)]
YY, XX = np.mgrid[-24:25, -24:25]
RR = np.hypot(XX, YY)
yy, xx = np.mgrid[-14:15, -14:15]
rr = np.hypot(xx, yy)
rings = [(rr < 3), (rr >= 3) & (rr < 6), (rr >= 6) & (rr < 10), (rr >= 10) & (rr < 14)]
inner = rr < 6
core3, ring610 = (np.abs(xx) <= 1) & (np.abs(yy) <= 1), (rr >= 6) & (rr <= 10)
from scipy.spatial import cKDTree
tree = cKDTree(S[:, :2])
crowd = np.array([len(tree.query_ball_point(p, 15)) - 1 for p in S[:, :2]])
BANDS = [(20, 100), (100, 1000), (1000, 1e12)]

print(f'{inp.name}: {len(S)} stars (compact, 5 sigma, green), frame sky {sky_level:.4g}')
for label, path in removers:
    st = fits.getdata(path).astype(np.float64)
    res = (st - bg) / rms
    verdict, old, smooth, colour, kept = [], [], [], [], []
    used_idx = []
    for k, ((x, y, snr), n) in enumerate(zip(S, crowd)):
        ix, iy = int(round(x)), int(round(y))
        win = st[:, iy - 24:iy + 25, ix - 24:ix + 25]
        # the starless plate's OWN level just outside this star: a 16-24 px annulus, other sources' cores masked
        near = S[(np.abs(S[:, 0] - x) < 30) & (np.abs(S[:, 1] - y) < 30)]
        keep = (RR >= 16) & (RR < 24)
        for ox, oy, _ in near:
            keep &= np.hypot(XX - (ox - ix), YY - (oy - iy)) >= 5
        if keep.sum() < 40:
            continue
        ref = np.median(win[:, keep], axis=1)
        sig = rms[:, iy, ix]
        inn = np.array([(win[c][RR < 6].mean() - ref[c]) / sig[c] for c in range(3)])
        verdict.append('dug' if inn.min() < -0.5 else 'left' if inn.max() > 0.5 else 'flat')
        g = win[1] / sig[1]
        o = g[(np.abs(XX) <= 1) & (np.abs(YY) <= 1)].mean() - np.median(g[(RR >= 6) & (RR <= 10)])
        old.append('dug' if o < -1 else 'left' if o > 1 else 'flat')
        smooth.append((bg[1, iy, ix] - sky_level) / sig[1] > 1.5)
        colour.append(inn)
        used_idx.append(k)
    verdict, old, smooth, colour = np.array(verdict), np.array(old), np.array(smooth), np.array(colour)
    used = np.array(used_idx)
    print(f'\n  {label}')
    for cls_name, cls in (('dark sky', ~smooth), ('smooth-bright background', smooth)):
        for lo, hi in BANDS:
            sel = cls & (S[used, 2] >= lo) & (S[used, 2] < hi)
            if sel.sum() < 5:
                continue
            v, o = verdict[sel], old[sel]
            dug_col = np.median(colour[sel & (verdict == 'dug')], axis=0) if np.any(sel & (verdict == 'dug')) else [np.nan] * 3
            print(f'    {cls_name:25s} SNR {lo:>4}-{hi if hi < 1e11 else "max":<5} n {sel.sum():5d}: '
                  f'flat {np.mean(v == "flat") * 100:5.1f} %, dug {np.mean(v == "dug") * 100:5.1f} %, left {np.mean(v == "left") * 100:5.1f} % '
                  f'(old ring check: flat {np.mean(o == "flat") * 100:5.1f} %); dug ones R/G/B {dug_col[0]:+.2f}/{dug_col[1]:+.2f}/{dug_col[2]:+.2f}')
    crowded = crowd[used] >= 3
    if crowded.sum() >= 5:
        v = verdict[crowded]
        print(f'    crowded (3+ within 15 px)  n {crowded.sum():5d}: flat {np.mean(v == "flat") * 100:5.1f} %, dug {np.mean(v == "dug") * 100:5.1f} %, left {np.mean(v == "left") * 100:5.1f} %')
    # flux: per ISOLATED star (no other source within 20 px), the light the remover took (input less starless) inside
    # 10 px as a share of the star's own flux there (input less its local background: the input's 16-24 px annulus,
    # other cores masked); 1 conserves it, below 1 leaves starlight, above 1 takes background. Per colour.
    isolated = np.array([len(tree.query_ball_point(p, 20)) == 1 for p in S[:, :2]])
    frac = []
    for (x, y, snr), iso in zip(S, isolated):
        if not iso or snr < 100:
            continue
        ix, iy = int(round(x)), int(round(y))
        win_in = data[:, iy - 24:iy + 25, ix - 24:ix + 25]
        win_st = st[:, iy - 24:iy + 25, ix - 24:ix + 25]
        ann = (RR >= 16) & (RR < 24)
        bkg = np.median(win_in[:, ann], axis=1)
        ap = RR < 10
        star = np.array([(win_in[c][ap] - bkg[c]).sum() for c in range(3)])
        took = np.array([(win_in[c][ap] - win_st[c][ap]).sum() for c in range(3)])
        if np.all(star > 0):
            frac.append((snr, *(took / star)))
    frac = np.array(frac)
    for lo, hi in ((100, 1000), (1000, 1e12)):
        f = frac[(frac[:, 0] >= lo) & (frac[:, 0] < hi)] if len(frac) else frac
        if len(f) >= 5:
            q = np.percentile(f[:, 1:], [10, 50, 90], axis=0)
            print(f'    flux taken / star flux, isolated SNR {lo}-{hi if hi < 1e11 else "max"} (n {len(f)}): median R/G/B '
                  f'{q[1, 0]:.2f}/{q[1, 1]:.2f}/{q[1, 2]:.2f}, G p10-p90 {q[0, 1]:.2f}-{q[2, 1]:.2f}')
    # field flux: per 128 px cell, the share of the input's light above the frame's sky that the remover took
    above = data[1] - sky_level
    took_all = data[1] - st[1]
    shares = []
    for y0 in range(0, H - 128, 128):
        for x0 in range(0, W - 128, 128):
            n_src = np.sum((S[:, 0] >= x0) & (S[:, 0] < x0 + 128) & (S[:, 1] >= y0) & (S[:, 1] < y0 + 128))
            a = above[y0:y0 + 128, x0:x0 + 128].sum()
            if a > 0:
                shares.append((n_src, took_all[y0:y0 + 128, x0:x0 + 128].sum() / a))
    shares = np.array(shares)
    d_ = shares[:, 0] >= np.percentile(shares[:, 0], 90)
    s_ = shares[:, 0] <= np.percentile(shares[:, 0], 50)
    print(f'    field light taken (green, above the frame sky): densest tenth of cells {np.median(shares[d_, 1]) * 100:.0f} %, '
          f'sparsest half {np.median(shares[s_, 1]) * 100:.0f} %, whole frame {np.nansum(took_all) / np.nansum(above) * 100:.0f} %')
    # haze and blotches, per 128 px cell: the starless green's level against the input's background map (unresolved
    # light kept or taken) and its band-pass at blotch scale (a 3 px mean less a 25 px median), both over the noise map
    from scipy.ndimage import uniform_filter
    g = st[1]
    # the filters spread a NaN (the canvas ring) across whatever they touch, so the ring is filled first and every cell
    # it reaches skipped below
    g_filled = np.where(np.isfinite(g), g, np.nanmedian(g))
    band = uniform_filter(g_filled, 3) - median_filter(g_filled, size=25)
    cells = []
    for y0 in range(0, H - 128, 128):
        for x0 in range(0, W - 128, 128):
            n_src = np.sum((S[:, 0] >= x0) & (S[:, 0] < x0 + 128) & (S[:, 1] >= y0) & (S[:, 1] < y0 + 128))
            noise = np.median(rms[1, y0:y0 + 128, x0:x0 + 128])
            b = band[y0:y0 + 128, x0:x0 + 128]
            # a cell the canvas ring (NaN) reaches, through the filters, is no cell of the frame
            if not (np.isfinite(b).all() and np.isfinite(g[y0:y0 + 128, x0:x0 + 128]).all()):
                continue
            blotch = 1.4826 * np.median(np.abs(b - np.median(b))) / noise
            haze = np.median(g[y0:y0 + 128, x0:x0 + 128] - bg[1, y0:y0 + 128, x0:x0 + 128]) / noise
            cells.append((n_src, blotch, haze))
    cells = np.array(cells)
    dense = cells[:, 0] >= np.percentile(cells[:, 0], 90)
    sparse = cells[:, 0] <= np.percentile(cells[:, 0], 50)
    print(f'    blotch-scale structure over the noise: densest tenth of cells {np.median(cells[dense, 1]):.2f}, sparsest half {np.median(cells[sparse, 1]):.2f}; '
          f'level against the input background: densest {np.median(cells[dense, 2]):+.2f}, sparsest {np.median(cells[sparse, 2]):+.2f} sigma')
