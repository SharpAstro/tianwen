"""How much of a synthetic cache's loss --crowd-weight would reach, before an arm registers its settings.

    python -I crowd_weight_share.py <cache> [--cells 400] [--sigma 3] [--radius 16] [--from 12] [--full 40] [--weight 4]

Reads `cells` training cells at random (seed 0), one drawn input each (slot 1 to `draws`) against its clean target (slot
0) with the draw's own noise plane, as the trainer pairs them, and prints over the pixels inside the trainer's rim: the
count of star-layer peaks about a pixel (crowd_weights' count) by percentile, the share of pixels on the ramp and at the
whole weight, and the share of the weighted mean's mass the weighed-up pixels carry (the dose). Reads only.
"""
import argparse

import numpy as np
import torch

import n2n_smoke as S

p = argparse.ArgumentParser()
p.add_argument("cache")
p.add_argument("--cells", type=int, default=400)
p.add_argument("--sigma", type=float, default=3.0)
p.add_argument("--radius", type=int, default=16)
p.add_argument("--from", dest="start", type=float, default=12.0)
p.add_argument("--full", type=float, default=40.0)
p.add_argument("--weight", type=float, default=4.0)
a = p.parse_args()

mm, meta = S.open_cache(a.cache)
planes, has = S.open_sigma(a.cache, meta)
rng = np.random.default_rng(0)
cells = rng.choice(meta["train_cells"], size=min(a.cells, meta["train_cells"]), replace=False)
B = S.BORDER
counts, weights = [], []
side = 2 * a.radius + 1
for c in cells:
    d = int(rng.integers(1, meta["draws"] + 1))
    x = torch.from_numpy(np.asarray(mm[c, d], np.float32)).unsqueeze(0)
    y = torch.from_numpy(np.asarray(mm[c, 0], np.float32)).unsqueeze(0)
    plane = torch.from_numpy(np.asarray(planes[c, d], np.float32)).unsqueeze(0)
    z = S.star_layer_sigmas(x, y, plane)
    peaks = ((z >= a.sigma) & (z == torch.nn.functional.max_pool2d(z, 3, 1, 1))).float()
    count = torch.nn.functional.avg_pool2d(peaks, side, 1, a.radius, count_include_pad=True) * side * side
    w = S.crowd_weights(x, y, plane, a.sigma, a.radius, a.start, a.full, a.weight)
    counts.append(count[0, 0, B:-B, B:-B].numpy().ravel())
    weights.append(w[0, 0, B:-B, B:-B].numpy().ravel())
count = np.concatenate(counts)
w = np.concatenate(weights)
print(f"{len(cells)} training cells of {a.cache}, one draw each; peaks at {a.sigma:g} sigma or more in the {side} px square")
print("count by percentile: " + ", ".join(f"p{q} {np.percentile(count, q):.0f}" for q in (10, 50, 90, 99, 99.9)))
print(f"pixels on the ramp ({a.start:g} to {a.full:g}): {100 * np.mean((count > a.start) & (count < a.full)):.2f} %; "
      f"at the whole weight {a.weight:g}: {100 * np.mean(count >= a.full):.3f} %")
print(f"mean weight {w.mean():.3f}; the weighed-up pixels' share of the weighted mean's mass "
      f"{100 * w[w > 1].sum() / w.sum():.1f} % (plain: {100 * np.mean(w > 1):.1f} %)")
