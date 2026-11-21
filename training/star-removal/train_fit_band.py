"""Does a model give back its training target on its own training inputs in crowded sky? (R4f's rerun, 2026-11-21.)

    python -I train_fit_band.py <trainer dir> <cache> <checkpoint> <session substring> [more substrings...]

A substring is looked for anywhere in a session's name, so a bare date takes every session of that night: 2024-10-02
alone is the Tarantula's session and the SMC's. Name the object with the date to read one of them.

For every training cell of the sessions named, in the cache's own (stretched) domain: the target (slot 0), the first
input draw (slot 1) and the model's output on that draw. Prints, per channel and as medians over the cells, the plan's
grain statistic (band.py's: through a 7 px median, the 9 px box mean less the 41 px one, rms over the inner tile) in
units of the TARGET's sigma of a 9 px mean for all three images, so they stand on one scale; the output's band over the
target's per cell; and how much of the input differs from the target at all (the share of pixels that are the same
value, which says whether anything but drawn stars separates the two); and the mean over the cells of the share
of pixels half a sigma under, and half a sigma over, the target. The model is fed the STORED per-pixel plane it trained
on (cond_map checkpoints only; sigma.f16, slot 1), never the broadcast scalar. Cells whose target holds a block of 400 exact
zeros (the knot defect, #2276) are left out.
"""
import os
import sys

import numpy as np
from scipy.ndimage import median_filter, uniform_filter

TRAINER, CACHE, CKPT = sys.argv[1:4]
KEYS = [k.lower() for k in sys.argv[4:]]
sys.path.insert(0, TRAINER)
import torch  # noqa: E402
import n2n_smoke as S  # noqa: E402


def sigma_of(c):
    return 1.4826 * np.median(np.abs(np.diff(c, axis=1))) / np.sqrt(2)


def band(c, sig):
    m = median_filter(c, 7)
    b = uniform_filter(m, 9) - uniform_filter(m, 41)
    return float(np.sqrt(np.mean(b[40:-40, 40:-40] ** 2)) / max(sig / 9.0, 1e-12))


def main():
    mm, meta = S.open_cache(CACHE)
    model, _ = S.load_model(CACHE, CKPT, "cpu")
    model.eval()
    if not model.cond_map:
        # What S.denoise refuses: a model that conditions on the scalar is not the one this script is written for.
        raise SystemExit(f"{CKPT} conditions on the tile's scalar sigma, not the stored per-pixel plane (cond_map is "
                         f"false); this script feeds the stored plane and refuses a checkpoint trained otherwise")
    sigma, has = S.open_sigma(CACHE, meta)
    if sigma is None:
        raise SystemExit(f"{CACHE} holds no sigma.f16, so the stored plane the model trained on cannot be read")
    train = set(meta["train_sessions"])
    for key in KEYS:
        cells = [i for i, k in enumerate(meta["keys"]) if key in k[0].lower() and k[0] in train]
        rows, same, under, over, left = [], [], [], [], 0
        for i in cells:
            target = np.asarray(mm[i, S.SLOT_MASTER], np.float32)
            draw = np.asarray(mm[i, 1], np.float32)
            if (target == 0).all(axis=0).sum() >= 400:
                left += 1
                continue
            if not has[i, 1]:
                raise SystemExit(f"cell {i} has no stored plane for slot 1; the model cannot be fed what it trained on")
            with torch.no_grad():
                out = model(S.with_plane(torch.from_numpy(draw[None]),
                                         np.asarray(sigma[i, 1], np.float32)[None])).numpy()[0]
            sig = [sigma_of(c) for c in target]
            rows.append([[band(img[c], sig[c]) for c in range(target.shape[0])] for img in (target, draw, out)])
            diff = np.abs(draw - target)
            same.append([float((diff[c] <= 0.25 * sig[c]).mean()) for c in range(target.shape[0])])
            signed = draw - target
            under.append([float((signed[c] <= -0.5 * sig[c]).mean()) for c in range(target.shape[0])])
            over.append([float((signed[c] >= 0.5 * sig[c]).mean()) for c in range(target.shape[0])])
        r = np.array(rows)
        f = lambda v: " / ".join(f"{x:.1f}" for x in v)
        g = lambda v: " / ".join(f"{x:.2f}" for x in v)
        print(f"{key}: {len(rows)} training cells read, {left} left out for a zero block")
        if not len(rows):
            continue
        print(f"  band, median over the cells (target's sigma of a 9 px mean): target {f(np.median(r[:, 0], axis=0))}; "
              f"input draw {f(np.median(r[:, 1], axis=0))}; the model's output {f(np.median(r[:, 2], axis=0))}")
        ratio = r[:, 2] / np.maximum(r[:, 0], 1e-9)
        print(f"  output over target, per cell: median {g(np.median(ratio, axis=0))}; 10th to 90th percentile "
              f"{g(np.percentile(ratio, 10, axis=0))} to {g(np.percentile(ratio, 90, axis=0))}")
        quiet = r[:, 0].mean(axis=1) < np.median(r[:, 0].mean(axis=1))
        print(f"  the quieter half of the cells (target band {f(np.median(r[quiet, 0], axis=0))}): output {f(np.median(r[quiet, 2], axis=0))}; "
              f"the busier half (target {f(np.median(r[~quiet, 0], axis=0))}): output {f(np.median(r[~quiet, 2], axis=0))}")
        print(f"  share of an input's pixels within a quarter sigma of the target's (no star's light on them): median {g(np.median(np.array(same), axis=0))}")
        print(f"  share of an input's pixels half a sigma UNDER the target's, mean over the cells: {g(np.mean(under, axis=0))}; "
              f"half a sigma OVER it: {g(np.mean(over, axis=0))}")


main()
