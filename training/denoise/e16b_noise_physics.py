"""The two things ASTRA-SR's ref [29] says a noise model must get right, measured on this bake's half pairs in LINEAR.

1. Variance against level: [29]'s dominant terms are shot noise (signal-dependent) and read noise (signal-independent).
   Our injection's model is variance = sigma_bg^2 * clamp(L / bg, 0.25, 1000): all shot, a floor at a quarter. Fit
   var = a * (L / bg) + b per channel on the half pairs, and report the read fraction b / (a + b) at the background,
   and the measured sigma(L) / sigma(bg) against the model's sqrt(L / bg).
2. Noise shape: band1 / band0 (NoiseField.BandSigmasOf's bands) of the half pairs' linear difference, per channel,
   against the injected draws' linear noise (draw minus clean) of the failed E16b export.

Each half is unstretched through its OWN recorded stretch; the level is the master's own channel, low-passed sigma 3.
"""
import json, os, sys, re
from collections import defaultdict
import numpy as np
from scipy.ndimage import gaussian_filter

BAKE = 'D:/Astro-Dataset/2026-09-29-full'
EXPORT = 'C:/temp/tianwen-scratch/degraded/e16b'
SESSIONS = ["Pleiades/2025-10-28", "Helix-Nebula/2025-08-20|SVBONY SV605CC|Helix Nebula|Optolong L-Ultimate 3nm",
            "Tarantula-Nebula/2025-11-01", "Large-Magellanic-Cloud/2023-09-15", "HIP-80609/2026-04-21|ZWO ASI533MC Pro|HIP 80609|Optolong L-Quad Enhance",
            "COO-71/2026-01-01", "HD-76360/2026-01-11", "HD-77683/2026-02-18", "Tarantula-Nebula/2024-10-02"]
CELLS = int(sys.argv[1]) if len(sys.argv) > 1 else 80
RIM = 16
SIZE = 256
EDGES = np.array([0.5, 0.8, 1.25, 2, 3, 5, 8, 13, 21, 34, 55, 100])


def mtf(m, x):
    x = np.clip(x, 0.0, 1.0)
    return (m - 1) * x / ((2 * m - 1) * x - m)


def unstretch(y, orig_min, balance):
    return mtf(1.0 - balance, y) + orig_min


def tile(root, rel, ch=3):
    a = np.fromfile(os.path.join(root, rel), dtype=np.float16).astype(np.float64)
    return a.reshape(ch, SIZE, SIZE)


def bands(field):
    b = [field] + [gaussian_filter(field, s, mode='nearest', truncate=3.0) for s in (1.0, 2.0, 4.0)]
    out = []
    for i in range(3):
        d = (b[i] - b[i + 1])[RIM:-RIM, RIM:-RIM].ravel()
        out.append(1.4826 * np.median(np.abs(d - np.median(d))))
    return out


def matches(sid, key):
    return sid.startswith(key) or key in sid if '|' in key else key in sid.split('|')[0]


rows = defaultdict(lambda: defaultdict(dict))  # session -> (x,y) -> frame -> row
with open(os.path.join(BAKE, 'tiles-manifest.jsonl'), encoding='utf-8') as f:
    for line in f:
        if '"halfmaster_' not in line and '"master"' not in line:
            continue
        r = json.loads(line)
        sid = r['SessionId']
        if '|flip=' in sid or not any(matches(sid, k) for k in SESSIONS):
            continue
        rows[sid][(r['CellX'], r['CellY'])][r['Frame']] = r

for sid in sorted(rows):
    cells = [c for c in sorted(rows[sid]) if {'master', 'halfmaster_a', 'halfmaster_b'} <= rows[sid][c].keys()]
    if not cells:
        continue
    step = max(1, len(cells) // CELLS)
    cells = cells[::step][:CELLS]
    m0 = rows[sid][cells[0]]['master']
    bg = m0['NoiseBackground']
    sig = m0['NoiseSigma']
    per_bin = [[[] for _ in range(len(EDGES) - 1)] for _ in range(3)]
    shape_pairs = [[] for _ in range(3)]
    for c in cells:
        fr = rows[sid][c]
        lin = {}
        for k in ('master', 'halfmaster_a', 'halfmaster_b'):
            t = tile(BAKE, fr[k]['Tile'])
            om, ba = fr[k]['StretchOrigMin'], fr[k]['StretchBalance']
            lin[k] = np.stack([unstretch(t[ch], om[ch], ba[ch]) for ch in range(3)])
        d = (lin['halfmaster_a'] - lin['halfmaster_b']) / np.sqrt(2.0)
        for ch in range(3):
            level = gaussian_filter(lin['master'][ch], 3.0, mode='nearest', truncate=3.0)[RIM:-RIM, RIM:-RIM].ravel()
            dd = d[ch][RIM:-RIM, RIM:-RIM].ravel()
            r = level / bg[ch]
            idx = np.digitize(r, EDGES) - 1
            for b in range(len(EDGES) - 1):
                sel = dd[idx == b]
                if sel.size:
                    per_bin[ch][b].append(sel)
            shape_pairs[ch].append(bands(d[ch]))
    print(f"\n{sid.split('|')[0]}  cells {len(cells)}")
    for ch in range(3):
        rs, var, cnt = [], [], []
        for b in range(len(EDGES) - 1):
            if not per_bin[ch][b]:
                continue
            v = np.concatenate(per_bin[ch][b])
            if v.size < 2000:
                continue
            s = 1.4826 * np.median(np.abs(v - np.median(v)))
            rs.append(np.sqrt(EDGES[b] * EDGES[b + 1]))
            var.append(s * s)
            cnt.append(v.size)
        rs, var, cnt = np.array(rs), np.array(var), np.array(cnt)
        # Fit var = a*r + b over bins up to 13x background (above that a bin is mostly star cores, where two halves'
        # different seeing adds a structure term that is not noise), weighted as relative errors.
        fit = rs <= 13
        if fit.sum() >= 2:
            A = np.vstack([rs[fit], np.ones(fit.sum())]).T
            w = 1.0 / var[fit]
            a, b0 = np.linalg.lstsq(A * w[:, None], var[fit] * w, rcond=None)[0]
            read = b0 / (a + b0)
        else:
            a = b0 = read = float('nan')
        ref = np.interp(1.0, rs, np.sqrt(var)) if rs.size else float('nan')
        prof = '  '.join(f"{r:5.1f}:{np.sqrt(v) / ref:4.2f}/{np.sqrt(max(r, 0.25)):4.2f}" for r, v in zip(rs, var))
        sb = np.median(np.array(shape_pairs[ch]), axis=0)
        print(f"  c{ch}: read fraction at bg {read:+.2f}  | sigma(L)/sigma(bg) measured/model at L/bg: {prof}")
        print(f"       half-pair band1/band0 {sb[1] / sb[0]:.3f}  band2/band1 {sb[2] / sb[1]:.3f}")

# The injected draws of the failed E16b export (warped, sigma 0.5): draw minus clean, in linear through the master's
# stretch, for the same sessions.
print("\ninjected draws (failed E16b export, --shape warped --warp-sigma 0.5), draw minus clean in linear:")
exp = defaultdict(lambda: defaultdict(dict))
with open(os.path.join(EXPORT, 'tiles-manifest.jsonl'), encoding='utf-8') as f:
    for line in f:
        r = json.loads(line)
        sid = r['SessionId']
        if sid in rows and r['Frame'] in ('master', 'deg000', 'deg001'):
            exp[sid][(r['CellX'], r['CellY'])][r['Frame']] = r
for sid in sorted(exp):
    stretch = next(iter(rows[sid].values()))['master']
    om, ba = stretch['StretchOrigMin'], stretch['StretchBalance']
    ratios = [[] for _ in range(3)]
    for c in sorted(exp[sid])[:40]:
        fr = exp[sid][c]
        if not {'master', 'deg000'} <= fr.keys():
            continue
        clean = tile(EXPORT, fr['master']['Tile'])
        drawn = tile(EXPORT, fr['deg000']['Tile'])
        for ch in range(3):
            n = unstretch(drawn[ch], om[ch], ba[ch]) - unstretch(clean[ch], om[ch], ba[ch])
            s = bands(n)
            ratios[ch].append(s[1] / s[0])
    print(f"  {sid.split('|')[0]}: band1/band0 " + ' / '.join(f"{np.median(r):.3f}" for r in ratios if r))
