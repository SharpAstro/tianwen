r"""Prediction PA's second half: does far-pairing make the 60-arm's OPERATOR less soggy?

v22's `n2n_keptshared.py` measured the mechanism as a transfer property of the operator: on
sessions nobody trained on, the many-session arms keep more of the pair-shared residue (0.71 vs
v19d's 0.63) and more of everything (kept_total 0.33 vs 0.24), while on their own train sessions
the arms are near-equal. PA predicts that removing the correlated residue from the TARGETS moves
that operator property, not merely the frontier: `60 far` should drop toward v19d on both
columns.

Keeping the two halves of PA separate matters. `n2n_rotate.py` reports the OUTCOME (faint
amplitude at matched noise); this reports the MECHANISM (what the operator does to residue).
Either can move without the other, and which combination lands decides what the result means:

  both move          -> the residue pathway is causal AND it is the whole story
  frontier only      -> far-pairing helps for some other reason; the v22 mechanism is a
                        correlate rather than the cause, and 1l needs rewriting
  operator only      -> the pathway is real but too small to matter at the operating point,
                        which is a negative for shipping and a positive for understanding
  neither            -> the residue pathway is dead, and 1l stands as measured-but-unexplained

Same statistic, same masks, same session sets as v22's version. Only the arm list changes, so
the v22 numbers are directly comparable row for row. `8 any E` is here too: PC predicts it
behaves like `8 any D`, and its kept_* columns are the operator-level version of that check.
"""
import io
import json
import os
import sys
from collections import defaultdict

import numpy as np
from scipy.ndimage import gaussian_filter

import n2n_smoke as S
from n2n_paircorr import ROOT, HP_SIGMA, SCENE_SIGMA, FAINT_FRAC, NEAR_DS, FAR_DS, sub_numbers
from n2n_paths import cache

BIG = cache("n2n-big")
D8 = cache("n2n-d8")
C21 = cache("n2n-c21")
E8 = cache("n2n-e8")
EVAL = cache("n2n-eval4")

F8 = cache("n2n-f8")

# PF asks whether the OPERATOR property replicates a third time. The pair-time arms are dropped
# here: v23 settled them at -9.9% / -26.8% (no movement), and this table is about the three
# 8-session draws against the many-session controls.
ARMS = [("60 any", BIG, "n2n_v17c_s%d_final.pt"),
        ("21 any", C21, "n2n_v19c_s%d_final.pt"),
        ("8 any D", D8, "n2n_v19d_s%d_final.pt"),
        ("8 any E", E8, "n2n_v23e_s%d_final.pt"),
        ("8 any F", F8, "n2n_v24f_s%d_final.pt")]


def arm_lists():
    v19 = r"D:\Astro-Dataset\n2n-smoke\v19"
    d8 = S.read_train_names(os.path.join(v19, "armD-8x45.txt"))
    c21 = S.read_train_names(os.path.join(v19, "armC-21x45.txt"))
    return d8, [s for s in c21 if s not in set(d8)]


def cells_for(cache, sessions, block):
    """(mm, [(cache index, full key)]) for the named sessions' cells in this cache."""
    mm, meta = S.open_cache(cache)
    lo = 0 if block == "train" else meta["train_cells"]
    hi = meta["train_cells"] if block == "train" else meta["cells"]
    wanted = set(sessions)
    return mm, [(i, tuple(meta["keys"][i])) for i in range(lo, hi)
                if meta["keys"][i][0] in wanted]


def prep_cell(mm, i, nums):
    """(sub_a 3ch float32, (r_a, r_b, r_c) masked hp residuals) or None."""
    if nums is None:
        return None
    m = np.asarray(mm[i, S.SLOT_MASTER], dtype=np.float32).mean(axis=0)
    m = m[S.BORDER:-S.BORDER, S.BORDER:-S.BORDER]
    scene = gaussian_filter(m, SCENE_SIGMA)
    mask = scene <= np.quantile(scene, FAINT_FRAC)
    if mask.sum() < 5000:
        return None
    m_hp = m - gaussian_filter(m, HP_SIGMA)

    valid = [s for s in range(S.SUBS_PER_CELL) if nums[s] >= 0
             and float(np.asarray(mm[i, s + 1, 0, ::64, ::64]).std()) > 0]
    if len(valid) < 3:
        return None
    pairs = [(abs(nums[q] - nums[p]), p, q) for pi, p in enumerate(valid)
             for q in valid[pi + 1:]]
    ds, a, b = min(pairs)
    if ds > NEAR_DS:
        return None
    c = max(valid, key=lambda s: abs(nums[s] - nums[a]))
    if abs(nums[c] - nums[a]) < FAR_DS:
        return None

    def resid(s):
        t = np.asarray(mm[i, s + 1], dtype=np.float32).mean(axis=0)
        t = t[S.BORDER:-S.BORDER, S.BORDER:-S.BORDER]
        r = (t - gaussian_filter(t, HP_SIGMA) - m_hp)[mask]
        return r - r.mean()

    sub_a = np.ascontiguousarray(np.asarray(mm[i, a + 1], dtype=np.float32))
    return sub_a, (resid(a), resid(b), resid(c)), mask, m_hp


def main():
    import torch
    dev = "cuda" if torch.cuda.is_available() else "cpu"
    nums = sub_numbers(ROOT)
    d8_names, added13 = arm_lists()
    observers = json.load(open(os.path.join(EVAL, "meta.json"),
                               encoding="utf-8"))["val_sessions"]

    sets = [("A both-trained", *cells_for(D8, d8_names, "train")),
            ("B big-only", *cells_for(BIG, added13, "train")),
            ("C observers", *cells_for(EVAL, observers, "val"))]

    models = {}
    for label, cache, pattern in ARMS:
        for si in range(3):
            try:
                models[(label, si)] = S.load_model(cache, pattern % si, dev)
            except FileNotFoundError:
                print(f"  {label} seed {si}: no _final.pt (the gate never passed it)")
    print(f"device {dev}, {len(models)} checkpoints")

    accum = defaultdict(lambda: np.zeros(6, dtype=np.float64))
    counts = defaultdict(int)
    with torch.no_grad():
        for set_name, mm, cells in sets:
            used = 0
            for i, key in cells:
                p = prep_cell(mm, i, nums.get(key))
                if p is None:
                    continue
                sub_a, (r_a, r_b, r_c), mask, m_hp = p
                x = torch.from_numpy(sub_a)[None].to(dev)
                denom = np.array([0, float(np.dot(r_a, r_b)), 0,
                                  float(np.dot(r_a, r_c)), 0, float(np.dot(r_a, r_a))])
                for (label, si), (model, planes) in models.items():
                    out = model(S.with_sigma(x, 1.0, planes) if planes else x)
                    o = out[0].float().mean(dim=0).cpu().numpy()
                    o = o[S.BORDER:-S.BORDER, S.BORDER:-S.BORDER]
                    r_o = (o - gaussian_filter(o, HP_SIGMA) - m_hp)[mask]
                    r_o = r_o - r_o.mean()
                    k = (set_name, label, si)
                    accum[k] += denom
                    accum[k][0] += float(np.dot(r_o, r_b))
                    accum[k][2] += float(np.dot(r_o, r_c))
                    accum[k][4] += float(np.dot(r_o, r_a))
                    counts[k] += 1
                used += 1
                if used % 100 == 0:
                    print(f"  {set_name}: {used} cells", flush=True)
            print(f"{set_name}: {used} usable cells", flush=True)

    print(f"\n{'set':16s} {'arm':10s} {'cells':>5s} {'kept_shared':>12s} {'kept_far':>9s} "
          f"{'kept_total':>11s}   per-seed kept_shared")
    report = {}
    for set_name, _, _ in sets:
        for label, _c, _p in ARMS:
            per_seed, tot, n = [], np.zeros(6), 0
            for si in range(3):
                k = (set_name, label, si)
                if k not in accum:
                    continue
                v = accum[k]
                per_seed.append(v[0] / v[1] if v[1] else float("nan"))
                tot += v
                n = counts[k]
            if not per_seed:
                continue
            ks, kf, kt = tot[0] / tot[1], tot[2] / tot[3], tot[4] / tot[5]
            report[f"{set_name}|{label}"] = {"kept_shared": ks, "kept_far": kf,
                                             "kept_total": kt, "per_seed_shared": per_seed,
                                             "cells": n}
            seeds = "  ".join(f"{v:+.3f}" for v in per_seed)
            print(f"{set_name:16s} {label:10s} {n:5d} {ks:+12.3f} {kf:+9.3f} {kt:+11.3f}   {seeds}")

    # PA's operator half, stated rather than left to the reader: how far did far-pairing travel
    # from 60-any toward 8-any-D on the sessions nobody trained on? 0% = no movement, 100% = it
    # became the few-session operator. Reported for near too, where PB expects a NEGATIVE number.
    print()
    c = "C observers"
    for col in ("kept_shared", "kept_total"):
        try:
            many, few = report[f"{c}|60 any"][col], report[f"{c}|8 any D"][col]
            gap = many - few
            for arm in ("60 far", "60 near"):
                if f"{c}|{arm}" not in report:
                    continue
                moved = (many - report[f"{c}|{arm}"][col]) / gap * 100 if gap else float("nan")
                print(f"  {col:11s}: {arm} travelled {moved:+6.1f}% of the 60-to-8 gap "
                      f"({many:.3f} -> {report[f'{c}|{arm}'][col]:.3f}, 8-arm at {few:.3f})")
        except KeyError:
            print(f"  {col}: incomplete, cannot compute the travel fraction")

    with io.open(os.path.join("..", "keptshared-results.json"), "w", encoding="utf-8") as f:
        json.dump(report, f, indent=1)
    print("\nwritten ../keptshared-results.json")
    return 0


if __name__ == "__main__":
    sys.exit(main())
