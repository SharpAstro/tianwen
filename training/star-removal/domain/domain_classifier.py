"""The synthetics review, part 2: a small CNN told to tell real tiles from synthetic ones, tested on a camera it never saw
(leave one camera out over the 8 validation sessions), so it cannot win by recognising a session. Its AUC says how far the
synthetics are from real (0.5: indistinguishable); the input variants say which component gives them away:
  full    the crop, each channel in its own noise sigma, asinh-compressed
  high    the crop minus its 7 px box mean: noise and star cores
  low     the 7 px box mean, 8x subsampled: the background's structure
  sky     only crops with no pixel over 5 sigma above the local sky: background and noise without stars
  stars   24 px crops centred on a star between 20 and 500 sigma
  noise   16 px crops with no pixel over 4 sigma above the local sky, minus their 5 px box mean: the noise texture alone
Runs on the CPU (R2r holds the GPU). Glue for a diagnosis: python domain_classifier.py <synthetic source> <out.json>"""
import json
import os
import sys

import numpy as np
import torch
from scipy.ndimage import maximum_filter, median_filter, uniform_filter

torch.set_num_threads(4)
ROOT = 'C:/temp/tianwen-scratch'
SOURCES = {
    'real': (f'{ROOT}/r2-real-val', 'master'),
    'synth4': (f'{ROOT}/r1-train-synth4/synth4', 'deg000'),
    'draws': (f'{ROOT}/r1-train/random', 'deg000'),
}
TILE = 256


def camera(sid):
    return sid.split('|')[1]


def load(source):
    root, frame = SOURCES[source]
    real_sessions = {json.loads(l)['SessionId'] for l in open(f'{ROOT}/r2-real-val/tiles-manifest.jsonl', encoding='utf-8')}
    out = []
    for line in open(f'{root}/tiles-manifest.jsonl', encoding='utf-8'):
        r = json.loads(line)
        if r['Frame'] == frame and r['SessionId'] in real_sessions:
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


def main(synthetic, out, variants=('full', 'high', 'low', 'sky', 'stars')):
    real, syn = load('real'), load(synthetic)
    result = {}
    for variant in variants:
        xr, cr = crops(real, variant)
        xs, cs = crops(syn, variant)
        folds = {}
        for held in sorted(set(cr)):
            folds[held] = fold(xr, xs, cr, cs, held)
        mean = float(np.mean([a for a, _, _ in folds.values()]))
        result[variant] = {'auc_mean': mean, 'folds': folds, 'real_crops': int(len(xr)), 'synthetic_crops': int(len(xs))}
        print(f'{synthetic} {variant:5s}: AUC {mean:.3f}  ' + '  '.join(f'{k} {a:.3f} ({nr}/{ns})' for k, (a, nr, ns) in folds.items()), flush=True)
    json.dump(result, open(out, 'w'), indent=1)


if __name__ == '__main__':
    main(sys.argv[1], sys.argv[2], *([sys.argv[3].split(',')] if len(sys.argv) > 3 else []))
