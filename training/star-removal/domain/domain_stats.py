"""The synthetics review, part 1 (the owner, 2026-10-21: "review our synthetics, they still do not look natural enough"):
the same statistics on real master tiles and on the two synthetic sources the remover trains and is scored on, over the 8
validation sessions, all three through the exporter's own stretch.
  real   r2-real-val, the masters' own cells (Frame "master")
  synth4 the training export: the synthetic background with injected stars and the master's noise (Frame "deg000")
  draws  A's draws: injected stars on the starless plate (Frame "deg000")
Every statistic is in the tile's own noise sigma, so a level or depth difference between sources is not read as a domain gap.
Glue for a diagnosis: python domain_stats.py <out.json>"""
import json
import os
import sys
from collections import defaultdict

import numpy as np
from scipy.ndimage import convolve, maximum_filter, median_filter, binary_dilation

ROOT = 'C:/temp/tianwen-scratch'
SOURCES = {
    'real': (f'{ROOT}/r2-real-val', 'master'),
    'synth4': (f'{ROOT}/r1-train-synth4/synth4', 'deg000'),
    'draws': (f'{ROOT}/r1-train/random', 'deg000'),
}
TILE = 256
B3 = np.array([1, 4, 6, 4, 1], np.float32) / 16


def val_sessions():
    real = set()
    for line in open(f'{ROOT}/r2-real-val/tiles-manifest.jsonl', encoding='utf-8'):
        r = json.loads(line)
        if r['Frame'] == 'master':
            real.add(r['SessionId'])
    return real


def tiles(source, sessions):
    root, frame = SOURCES[source]
    for line in open(f'{root}/tiles-manifest.jsonl', encoding='utf-8'):
        r = json.loads(line)
        if r['Frame'] == frame and r['SessionId'] in sessions:
            yield r['SessionId'], np.fromfile(os.path.join(root, r['Tile']), dtype='<f2').astype(np.float32).reshape(3, TILE, TILE)


def starlet(plane, scales=5):
    """A trous B3-spline wavelet planes w1..w5 (about 1, 2, 4, 8, 16 px)."""
    c = plane
    out = []
    for j in range(scales):
        k = np.zeros(4 * (1 << j) + 1, np.float32)
        k[:: 1 << j] = B3
        s = convolve(convolve(c, k[None, :], mode='mirror'), k[:, None], mode='mirror')
        out.append(c - s)
        c = s
    return out


def measure(t):
    m = {}
    hp = t - median_filter(t, size=(1, 5, 5))
    sig = np.array([1.4826 * np.median(np.abs(np.diff(t[c], axis=1))) / np.sqrt(2) for c in range(3)]) + 1e-9
    lum = t.mean(0)
    bg = median_filter(lum, size=31)
    lsig = sig.mean()
    stars = (lum - bg) > 5 * lsig
    mask = binary_dilation(stars, iterations=4)
    sky = ~mask
    m['sky_fraction'] = float(sky.mean())
    m['noise_sigma'] = sig.tolist()
    # Noise texture on the sky: lag-1 correlation of the high-pass residual, the residual's tails, the channels' coupling.
    r = [hp[c][sky] / sig[c] for c in range(3)]
    lag = []
    for c in range(3):
        a = hp[c] / sig[c]
        h = (a[:, 1:] * a[:, :-1])[sky[:, 1:] & sky[:, :-1]]
        v = (a[1:, :] * a[:-1, :])[sky[1:, :] & sky[:-1, :]]
        lag.append(float((h.mean() + v.mean()) / 2 / max(np.mean(a[sky] ** 2), 1e-9)))
    m['noise_lag1'] = lag
    m['noise_kurtosis'] = [float(np.mean((x - x.mean()) ** 4) / max(np.var(x) ** 2, 1e-12) - 3) for x in r]
    m['noise_rg_gb_rb'] = [float(np.corrcoef(r[0], r[1])[0, 1]), float(np.corrcoef(r[1], r[2])[0, 1]), float(np.corrcoef(r[0], r[2])[0, 1])]
    # The sky's texture by scale, stars masked, in the tile's noise (luminance).
    w = starlet(lum)
    m['starlet_rms'] = [float(np.sqrt(np.mean(x[sky] ** 2)) / lsig) for x in w]
    # Stars: peaks over 5 sigma, isolated within 6 px; per channel width and wing share, colour.
    peaks = (lum == maximum_filter(lum, size=13)) & stars
    ys, xs = np.nonzero(peaks)
    snr, fw, wing, col = [], [], [], []
    yy, xx = np.mgrid[-8:9, -8:9]
    rr = np.hypot(yy, xx)
    for y, x in zip(ys, xs):
        if not (8 <= y < TILE - 8 and 8 <= x < TILE - 8):
            continue
        cut = t[:, y - 8:y + 9, x - 8:x + 9] - median_filter(t, size=(1, 31, 31))[:, y, x][:, None, None] if False else \
            t[:, y - 8:y + 9, x - 8:x + 9] - np.median(t[:, y - 8:y + 9, x - 8:x + 9][:, rr > 7], axis=1)[:, None, None]
        pk = cut[:, 8, 8]
        if (pk <= 0).any():
            continue
        snr.append(float((lum[y, x] - bg[y, x]) / lsig))
        fw.append([float(2 * np.sqrt(((cut[c] > pk[c] / 2) & (rr < 6)).sum() / np.pi)) for c in range(3)])
        wing.append([float(cut[c][(rr >= 3) & (rr < 5)].mean() / pk[c]) for c in range(3)])
        col.append([float(pk[0] / pk[1]), float(pk[2] / pk[1])])
    m['stars'] = len(snr)
    m['star_snr'] = snr
    m['star_fwhm'] = fw
    m['star_wing'] = wing
    m['star_colour'] = col
    return m


def main(out):
    sessions = val_sessions()
    result = defaultdict(list)
    for source in SOURCES:
        for sid, t in tiles(source, sessions):
            m = measure(t)
            m['session'] = sid
            result[source].append(m)
        print(f'{source}: {len(result[source])} tiles', flush=True)
    json.dump(result, open(out, 'w'))


if __name__ == '__main__':
    main(sys.argv[1])
