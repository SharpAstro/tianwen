"""The synthetics review, part 1 (the owner, 2026-10-21: "review our synthetics, they still do not look natural enough"):
the same statistics on real master tiles and on the synthetic sources the remover trains and is scored on, over the 8
validation sessions, all through the exporter's own stretch.
  real   the real validation tiles, the masters' own cells (Frame "master")
  synthetic sources, as many as named (Frame "deg000" unless given), for instance
    synth4 the training export: the synthetic background with injected stars and the master's noise
    draws  A's draws: injected stars on the starless plate
Every statistic is in the tile's own noise sigma, so a level or depth difference between sources is not read as a domain gap.

Every path is an argument. A tile root holds `tiles-manifest.jsonl` (one JSON object per line with `SessionId`, `Frame` and
`Tile`, the tile's path relative to the root) and the tiles it names (3 x 256 x 256 little-endian float16, the exporter's
layout). The real root's `master` frames set the validation sessions.
  python domain_stats.py --real <real root> --synthetic synth4=<root>[,<frame>] --synthetic draws=<root> --out stats.json"""
import argparse
import json
import os
import sys
from collections import defaultdict

import numpy as np
from scipy.ndimage import convolve, maximum_filter, median_filter, binary_dilation

REAL_FRAME = 'master'
DEFAULT_SYNTHETIC_FRAME = 'deg000'
TILE = 256
B3 = np.array([1, 4, 6, 4, 1], np.float32) / 16


def val_sessions(real_root):
    real = set()
    with open(os.path.join(real_root, 'tiles-manifest.jsonl'), encoding='utf-8') as f:
        for line in f:
            r = json.loads(line)
            if r['Frame'] == REAL_FRAME:
                real.add(r['SessionId'])
    return real


def tiles(root, frame, sessions):
    with open(os.path.join(root, 'tiles-manifest.jsonl'), encoding='utf-8') as f:
        for line in f:
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


def main(sources, sessions, out):
    result = defaultdict(list)
    for source, (root, frame) in sources.items():
        for sid, t in tiles(root, frame, sessions):
            m = measure(t)
            m['session'] = sid
            result[source].append(m)
        print(f'{source}: {len(result[source])} tiles', flush=True)
    with open(out, 'w', encoding='utf-8') as f:
        json.dump(result, f)


def parse(argv):
    p = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    p.add_argument('--real', required=True, help='the real validation tile root (its `master` frames set the sessions)')
    p.add_argument('--synthetic', action='append', default=[], metavar='NAME=ROOT[,FRAME]',
                   help=f'a synthetic tile root to measure beside real, and the frame to read (default {DEFAULT_SYNTHETIC_FRAME}); repeatable')
    p.add_argument('--out', required=True, help='the result JSON to write')
    a = p.parse_args(argv)
    sources = {'real': (a.real, REAL_FRAME)}
    for spec in a.synthetic:
        name, _, rest = spec.partition('=')
        root, _, frame = rest.partition(',')
        if not name or not root or name in sources:
            p.error('--synthetic is NAME=ROOT[,FRAME], with a name of its own (not "real")')
        sources[name] = (root, frame or DEFAULT_SYNTHETIC_FRAME)
    return sources, val_sessions(a.real), a.out


if __name__ == '__main__':
    main(*parse(sys.argv[1:]))
