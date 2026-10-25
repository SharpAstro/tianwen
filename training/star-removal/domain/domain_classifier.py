"""The synthetics review, part 2: a small CNN told to tell real tiles from synthetic ones, tested on a camera it never saw
(leave one camera out over the 8 validation sessions), so it cannot win by recognising a session. Its AUC says how far the
synthetics are from real (0.5: indistinguishable); the input variants say which component gives them away:
  full    the crop, each channel in its own noise sigma, asinh-compressed
  high    the crop minus its 7 px box mean: noise and star cores
  low     the 7 px box mean, 8x subsampled: the background's structure
  sky     only crops with no pixel over 5 sigma above the local sky: background and noise without stars
  stars   24 px crops centred on a star between 20 and 500 sigma
  noise   16 px crops with no pixel over 4 sigma above the local sky, minus their 5 px box mean: the noise texture alone
Runs on the CPU (R2r holds the GPU).

The classifier's own spread: each fold's net starts from a seed (`--seeds`), and the same crops are trained and read at each, so
the spread of a variant's AUC over seeds is the classifier's noise, which a change to a synthetic export must clear to count.

Every path is an argument; nothing is read from a fixed folder. A tile root holds `tiles-manifest.jsonl` (one JSON object per
line with `SessionId`, `Frame` and `Tile`, the tile's path relative to the root) and the tiles the manifest names (3 x 256 x
256 little-endian float16, the exporter's layout). The real root's `master` frames set the validation sessions; a synthetic's
frames are read for those sessions only.

  python domain_classifier.py --real <real root> --synthetic synth4=<root>[,<frame>] --out cls-synth4.json \\
      --variants stars,noise --seeds 0,1,2
"""
import argparse
import json
import os
import sys

import numpy as np
import torch
from scipy.ndimage import maximum_filter, median_filter, uniform_filter

TILE = 256
REAL_FRAME = 'master'
DEFAULT_SYNTHETIC_FRAME = 'deg000'
VARIANTS = ('full', 'high', 'low', 'sky', 'stars', 'noise')
DEFAULT_VARIANTS = ('full', 'high', 'low', 'sky', 'stars')


def camera(sid):
    return sid.split('|')[1]


def manifest(root):
    with open(os.path.join(root, 'tiles-manifest.jsonl'), encoding='utf-8') as f:
        return [json.loads(line) for line in f if line.strip()]


def load(root, frame, sessions):
    """The tiles of `frame` in `root` for `sessions`, each as (camera, z): the tile in its own noise sigma per channel."""
    out = []
    for r in manifest(root):
        if r['Frame'] == frame and r['SessionId'] in sessions:
            t = np.fromfile(os.path.join(root, r['Tile']), dtype='<f2').astype(np.float32).reshape(3, TILE, TILE)
            sig = np.array([1.4826 * np.median(np.abs(np.diff(t[c], axis=1))) / np.sqrt(2) for c in range(3)]) + 1e-9
            z = (t - np.median(t, axis=(1, 2), keepdims=True)) / sig[:, None, None]
            out.append((camera(r['SessionId']), z))
    return out


def crops(tiles, variant):
    xs, cams = [], []
    for cam, z in tiles:
        lum = z.mean(0)
        if variant == 'stars':
            bg = median_filter(lum, size=31)
            peaks = (lum == maximum_filter(lum, size=9)) & ((lum - bg) > 20) & ((lum - bg) < 500)
            for y, x in zip(*np.nonzero(peaks)):
                if 12 <= y < TILE - 12 and 12 <= x < TILE - 12:
                    xs.append(z[:, y - 12:y + 12, x - 12:x + 12])
                    cams.append(cam)
            continue
        if variant == 'noise':
            local = median_filter(lum, size=31)
            hp = z - uniform_filter(z, size=(1, 5, 5))
            for y in range(0, TILE, 16):
                for x in range(0, TILE, 16):
                    if ((lum[y:y + 16, x:x + 16] - local[y:y + 16, x:x + 16]) > 4).any():
                        continue
                    xs.append(hp[:, y:y + 16, x:x + 16])
                    cams.append(cam)
            continue
        local = median_filter(lum, size=31) if variant == 'sky' else None
        for y in range(0, TILE, 64):
            for x in range(0, TILE, 64):
                c = z[:, y:y + 64, x:x + 64]
                if variant == 'sky' and ((lum[y:y + 64, x:x + 64] - local[y:y + 64, x:x + 64]) > 5).any():
                    continue
                if variant == 'high':
                    c = c - uniform_filter(c, size=(1, 7, 7))
                elif variant == 'low':
                    c = uniform_filter(c, size=(1, 7, 7))[:, ::8, ::8]
                xs.append(c)
                cams.append(cam)
    x = np.arcsinh(np.stack(xs) / 3.0).astype(np.float32)
    return x, np.array(cams)


class Net(torch.nn.Module):
    def __init__(self):
        super().__init__()
        c = torch.nn.Conv2d
        self.f = torch.nn.Sequential(c(3, 16, 3, padding=1), torch.nn.ReLU(), c(16, 32, 3, stride=2, padding=1), torch.nn.ReLU(),
                                     c(32, 64, 3, stride=2, padding=1), torch.nn.ReLU(), c(64, 64, 3, padding=1), torch.nn.ReLU())
        self.h = torch.nn.Linear(64, 1)

    def forward(self, x):
        return self.h(self.f(x).mean((2, 3))).squeeze(1)


def auc(score, label):
    order = np.argsort(score)
    ranks = np.empty(len(score))
    ranks[order] = np.arange(1, len(score) + 1)
    pos = label == 1
    return float((ranks[pos].sum() - pos.sum() * (pos.sum() + 1) / 2) / (pos.sum() * (~pos).sum()))


def fold(xr, xs, cr, cs, held, epochs=6, seed=0):
    rng = np.random.default_rng(seed)
    torch.manual_seed(seed)
    tr = np.concatenate([xr[cr != held], xs[cs != held]])
    ty = np.concatenate([np.zeros((cr != held).sum()), np.ones((cs != held).sum())]).astype(np.float32)
    te = np.concatenate([xr[cr == held], xs[cs == held]])
    ey = np.concatenate([np.zeros((cr == held).sum()), np.ones((cs == held).sum())])
    # Balance the classes in every batch.
    w = np.where(ty == 1, 0.5 / max(ty.sum(), 1), 0.5 / max((1 - ty).sum(), 1))
    net = Net()
    opt = torch.optim.Adam(net.parameters(), 1e-3)
    loss = torch.nn.BCEWithLogitsLoss()
    steps = max(1, len(tr) // 128) * epochs
    for _ in range(steps):
        idx = rng.choice(len(tr), 128, p=w / w.sum())
        b = torch.from_numpy(tr[idx])
        k = int(rng.integers(4))
        b = torch.rot90(b, k, (2, 3))
        if rng.random() < 0.5:
            b = torch.flip(b, (3,))
        opt.zero_grad()
        l = loss(net(b), torch.from_numpy(ty[idx]))
        l.backward()
        opt.step()
    with torch.no_grad():
        s = np.concatenate([net(torch.from_numpy(te[i:i + 512])).numpy() for i in range(0, len(te), 512)])
    return auc(s, ey), int((cr == held).sum()), int((cs == held).sum())


def spread(values):
    """Mean and the sample standard deviation over seeds (0 for a single seed, which says nothing about the spread)."""
    v = np.asarray(values, dtype=np.float64)
    return float(v.mean()), float(v.std(ddof=1)) if len(v) > 1 else 0.0


def main(real_root, synthetic, synthetic_root, synthetic_frame, out, variants, seeds, epochs):
    sessions = {r['SessionId'] for r in manifest(real_root) if r['Frame'] == REAL_FRAME}
    real = load(real_root, REAL_FRAME, sessions)
    syn = load(synthetic_root, synthetic_frame, sessions)
    result = {}
    for variant in variants:
        xr, cr = crops(real, variant)
        xs, cs = crops(syn, variant)
        per_seed = {}
        for seed in seeds:
            folds = {}
            for held in sorted(set(cr)):
                folds[held] = fold(xr, xs, cr, cs, held, epochs=epochs, seed=seed)
            mean = float(np.mean([a for a, _, _ in folds.values()]))
            per_seed[str(seed)] = {'auc_mean': mean, 'folds': folds}
            print(f'{synthetic} {variant:5s} seed {seed}: AUC {mean:.3f}  ' + '  '.join(f'{k} {a:.3f} ({nr}/{ns})' for k, (a, nr, ns) in folds.items()), flush=True)
        mean, std = spread([s['auc_mean'] for s in per_seed.values()])
        # A camera's AUC over seeds: the spread of one fold, which the mean over cameras averages down.
        cameras = {k: spread([s['folds'][k][0] for s in per_seed.values()]) for k in sorted(set(cr))}
        result[variant] = {'auc_mean': mean, 'auc_std': std, 'seeds': list(seeds), 'per_seed': per_seed,
                           'camera_auc_mean_std': cameras, 'real_crops': int(len(xr)), 'synthetic_crops': int(len(xs))}
        print(f'{synthetic} {variant:5s}: AUC {mean:.3f} +- {std:.3f} over seeds {",".join(str(s) for s in seeds)}', flush=True)
    with open(out, 'w', encoding='utf-8') as f:
        json.dump(result, f, indent=1)


def parse(argv):
    p = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    p.add_argument('--real', required=True, help='the real validation tile root (its `master` frames set the sessions)')
    p.add_argument('--synthetic', required=True, metavar='NAME=ROOT[,FRAME]',
                   help=f'the synthetic tile root to tell from real, and the frame to read (default {DEFAULT_SYNTHETIC_FRAME})')
    p.add_argument('--out', required=True, help='the result JSON to write')
    p.add_argument('--variants', default=','.join(DEFAULT_VARIANTS), help=f'comma list of {",".join(VARIANTS)}')
    p.add_argument('--seeds', default='0', help='comma list of the nets\' seeds, three or more to read the classifier\'s spread')
    p.add_argument('--epochs', type=int, default=6)
    p.add_argument('--threads', type=int, default=4, help='torch CPU threads')
    a = p.parse_args(argv)
    name, _, rest = a.synthetic.partition('=')
    root, _, frame = rest.partition(',')
    if not name or not root:
        p.error('--synthetic is NAME=ROOT[,FRAME]')
    variants = tuple(v for v in a.variants.split(',') if v)
    unknown = [v for v in variants if v not in VARIANTS]
    if unknown:
        p.error(f'unknown variant {unknown}, choose from {VARIANTS}')
    seeds = tuple(int(s) for s in a.seeds.split(',') if s != '')
    if not seeds:
        p.error('--seeds needs at least one seed')
    torch.set_num_threads(a.threads)
    return a.real, name, root, frame or DEFAULT_SYNTHETIC_FRAME, a.out, variants, seeds, a.epochs


if __name__ == '__main__':
    main(*parse(sys.argv[1:]))
