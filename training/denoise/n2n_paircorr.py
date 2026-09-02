r"""Is the N2N premise itself broken per-session, and is THAT why 8 sessions beat 60?

Every training pair here is two subs of the same cell, same session, same calibration, same
registration (n2n_smoke.py, the a/b draw). N2N's guarantee -- "the target's noise is independent
of the input's, so the conditional mean is the clean scene" -- therefore holds only as far as
nothing is common-mode between two subs of one session. Walking noise under weak dither, dark
residue that survived the darkscaled bake, seeing bursts shared by neighbouring frames: all of it
is SIGNAL to the training loss, and the optimal N2N answer is to KEEP it. A model taught on such a
session learns "structured high-frequency stuff of this shape must be preserved", which is exactly
a worse noise-vs-faint-amp frontier everywhere, on every observer.

This mechanism fits every result the chain has produced, which no other candidate does:

- it scales with the NUMBER of sessions (probability the pool contains violators) and is
  indifferent to volume / density / epochs -- v19's exact shape, saturation by 21 included,
  because inclusion probability saturates fast while regime diversity would grow smoothly;
- it survives the v20 observer rotation, because the damage is baked into the weights and has
  nothing to do with proximity;
- it CANNOT be fixed by conditioning, richer or not, because conditioning describes the INPUT's
  noise while this is corruption of the TARGET -- which is why v21 had to fail;
- the data already confesses: the half-masters carry correlated residue (1.41x the master where
  sqrt(n) predicts ~8x better, n2n_smoke.py's own docstring), and the bake is "darkscaled, good
  enough on purpose".

And unlike the regime-averaging story it is measurable per session with ZERO training, from the
caches as they sit on the SSD.

## The statistic

For each cell: residuals r_i = highpass(sub_i - master), masked to the faintest half of the
scene (both taken from the master, so the mask and the scene reference are the same for every
sub). Pearson correlation of every sub pair -> an 8x8 matrix per cell. The export sampled the 8
subs SPREAD across the session but kept their chronological order, and the manifest still holds
their frame numbers, so every pair has a time-index separation ds.

- rho_near = mean corr over pairs with ds <= 3 (the closest-in-time pairs)
- rho_far  = mean corr over pairs with ds >= 10
- ADJACENCY EXCESS = rho_near - rho_far, the primary number. Under ideal N2N it is ZERO for any
  white/independent noise, and the master-noise bias (subtracting a shared master correlates all
  residual pairs equally, ~ -sigma^2/N) is pair-independent, so it cancels in the difference.
  Only time-correlated residue can make it positive.
- rho_all  = mean corr over all 28 pairs (context; carries the master bias, so read it relative)
- rho_half = corr(highpass(half_a - master), highpass(half_b - master)) where the cache carries
  the half-master pair: the halves interleave the WHOLE night, so this is the deployment-noise
  version of the same question.

Median over a session's cells; the caches hold 45 train cells per session (n2n-big) and the
four v20 observers (n2n-eval4), so 65 sessions are measurable without touching the archive.

## Pre-registered predictions, written before the first number was looked at

- P1: the statistic varies across sessions by clearly more than its within-session cell scatter
  (else it can explain nothing).
- P2: v19d's 8 sessions sit predominantly in the clean half of the 60-session ranking, with none
  of them in the dirtiest ten.
- P3: the 13 sessions the 8->21 step added include at least one of the dirtiest ten, because
  saturation-by-21 is this mechanism's inclusion probability showing through.

P2 failing kills the hypothesis and says so. P2 and P3 holding does NOT prove causation -- that
takes the v23 training arms (8-dirtiest vs 8-cleanest-disjoint, pinned by name), which is the
confirmation this measurement is designed to select the sessions for.
"""
import csv
import io
import json
import os
import re
import sys
from collections import defaultdict

import numpy as np
from scipy.ndimage import gaussian_filter

import n2n_smoke as S
from n2n_paths import bake, cache

ROOT = bake("2025-2026-darkscaled")
CACHES = [cache("n2n-big"), cache("n2n-eval4")]
HP_SIGMA = 4.0        # highpass: x - gauss(x, 4), kills gradients + smooth scene
SCENE_SIGMA = 8.0     # the faint-mask's smoothing, same scale the conditioning estimator uses
FAINT_FRAC = 0.50     # keep the faintest half of the master's smoothed scene
NEAR_DS, FAR_DS = 3, 10
SUB_RE = re.compile(r"_s(\d+)\.f16$")


def sub_numbers(root):
    """(session, cx, cy) -> sorted frame numbers of the exported subs, from the manifest.

    The cache stores subs in slot order 1..8 = sorted relpath = chronological, but drops the
    frame numbers; the manifest still has them, and the time-index separation of a pair is the
    x-axis the adjacency statistic needs. Read once, sequentially; nothing writes this file now.
    """
    cells = S.load_cells(root, "tiles-manifest.jsonl")
    out = {}
    for key, entry in cells.items():
        nums = []
        for rel in sorted(entry["subs"])[:S.SUBS_PER_CELL]:
            m = SUB_RE.search(rel)
            nums.append(int(m.group(1)) if m else -1)
        out[key] = nums
    return out


def lum(tile):
    """Channel-mean luminance as float32, rim-cropped like everything else here."""
    t = np.asarray(tile, dtype=np.float32).mean(axis=0)
    return t[S.BORDER:-S.BORDER, S.BORDER:-S.BORDER]


def cell_stats(mm, i, has_half, nums):
    """One cell's (rho_near, rho_far, rho_all, rho_half), any of them None when unmeasurable."""
    m = lum(mm[i, S.SLOT_MASTER])
    scene = gaussian_filter(m, SCENE_SIGMA)
    mask = scene <= np.quantile(scene, FAINT_FRAC)
    if mask.sum() < 5000:
        return None
    m_hp = m - gaussian_filter(m, HP_SIGMA)

    resid = []
    for s in range(1, S.SUBS_PER_CELL + 1):
        t = lum(mm[i, s])
        if float(t.std()) == 0.0:            # unwritten slot: the cell had fewer than 8 subs
            resid.append(None)
            continue
        r = (t - gaussian_filter(t, HP_SIGMA) - m_hp)[mask]
        r = r - r.mean()
        n = float(np.sqrt((r * r).sum()))
        resid.append(r / n if n > 0 else None)

    pairs = defaultdict(list)
    rho_all = []
    for a in range(S.SUBS_PER_CELL):
        for b in range(a + 1, S.SUBS_PER_CELL):
            if resid[a] is None or resid[b] is None or nums[a] < 0 or nums[b] < 0:
                continue
            rho = float(np.dot(resid[a], resid[b]))
            rho_all.append(rho)
            ds = abs(nums[b] - nums[a])
            if ds <= NEAR_DS:
                pairs["near"].append(rho)
            elif ds >= FAR_DS:
                pairs["far"].append(rho)

    rho_half = None
    if has_half:
        ha, hb = lum(mm[i, S.SLOT_HALF_A]), lum(mm[i, S.SLOT_HALF_B])
        if float(ha.std()) > 0 and float(hb.std()) > 0:
            ra = (ha - gaussian_filter(ha, HP_SIGMA) - m_hp)[mask]
            rb = (hb - gaussian_filter(hb, HP_SIGMA) - m_hp)[mask]
            ra, rb = ra - ra.mean(), rb - rb.mean()
            den = float(np.sqrt((ra * ra).sum() * (rb * rb).sum()))
            if den > 0:
                rho_half = float(np.dot(ra, rb)) / den

    near = float(np.mean(pairs["near"])) if pairs["near"] else None
    far = float(np.mean(pairs["far"])) if pairs["far"] else None
    return (near, far, float(np.mean(rho_all)) if rho_all else None, rho_half)


def main():
    nums = sub_numbers(ROOT)
    print(f"manifest: sub numbers for {len(nums)} cells")

    per_session = defaultdict(lambda: defaultdict(list))
    for cache in CACHES:
        mm, meta = S.open_cache(cache)
        keys = meta["keys"]
        halves = meta.get("has_halves") or [False] * meta["cells"]
        done = 0
        for i, key in enumerate(keys):
            key = tuple(key)
            if key not in nums:
                continue
            st = cell_stats(mm, i, halves[i], nums[key])
            if st is None:
                continue
            bag = per_session[key[0]]
            for name, v in zip(("near", "far", "all", "half"), st):
                if v is not None:
                    bag[name].append(v)
            done += 1
            if done % 300 == 0:
                print(f"  {os.path.basename(cache)}: {done} cells", flush=True)
        print(f"{os.path.basename(cache)}: {done} cells measured")

    rows = []
    for session, bag in per_session.items():
        near, far = bag.get("near", []), bag.get("far", [])
        # The adjacency excess is computed per cell where both sides exist, so a session whose
        # sampling left no near pairs reports None rather than a number built from different cells.
        row = {
            "session": session,
            "cells": len(bag.get("all", [])),
            "rho_all": np.median(bag["all"]) if bag.get("all") else None,
            "rho_near": np.median(near) if near else None,
            "rho_far": np.median(far) if far else None,
            "excess": (np.median(near) - np.median(far)) if (near and far) else None,
            "excess_iqr": (float(np.subtract(*np.percentile(near, [75, 25]))) if near else None),
            "rho_half": np.median(bag["half"]) if bag.get("half") else None,
            "n_near": len(near), "n_far": len(far),
        }
        rows.append(row)

    rows.sort(key=lambda r: (r["excess"] is None, -(r["excess"] or 0)))
    out = os.path.join("..", "paircorr-sessions.csv")
    with io.open(out, "w", encoding="utf-8", newline="") as f:
        w = csv.DictWriter(f, fieldnames=list(rows[0].keys()))
        w.writeheader()
        w.writerows(rows)
    print(f"\nwritten {out}\n")

    def fmt(v):
        return "   -  " if v is None else f"{v:+.4f}"

    print(f"{'session':64s} {'cells':>5s} {'excess':>8s} {'rho_near':>9s} "
          f"{'rho_far':>8s} {'rho_all':>8s} {'rho_half':>9s}")
    for r in rows:
        print(f"{r['session'][:64]:64s} {r['cells']:5d} {fmt(r['excess']):>8s} "
              f"{fmt(r['rho_near']):>9s} {fmt(r['rho_far']):>8s} "
              f"{fmt(r['rho_all']):>8s} {fmt(r['rho_half']):>9s}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
