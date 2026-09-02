r"""Nebulosity kept at MATCHED NOISE: does the star metric's verdict survive a structure column?

n2n_structkeep.py found the raw operators trade in opposite directions: v19d (8 sparse-field
sessions) strips more noise everywhere AND keeps less mid-scale (4-16 px) master-correlated
structure than the 21/60-session arms, on all four observers. But raw-operator numbers are not
the frontier: the v20/v21 tables compare at matched noise 0.90 via the blend-toward-input knob,
and a harder denoiser gets blended further back toward the input there, which restores structure
in proportion. So the shipping question is the blended one:

  at the SAME residual noise on the SAME master, which arm leaves more nebulosity standing?

This mirrors n2n_bars.curve exactly -- same Gate, same forward on the masters, same blend
`master + a * (den - master)`, same noise normalisation and interpolation -- and adds two
structure columns. Because a difference-of-Gaussians is linear, band(blend) = band(master) +
a * (band(den) - band(master)), so one forward per checkpoint yields the whole structure curve
analytically:

  struct_kept(a) = 1 - a * (1 - k),   k = sum <band(den), band(m)> / sum <band(m), band(m)>

summed over cells, in the star-free nebula region (Gate's own star-free mask intersected with
the brighter half of the smoothed scene -- the complement of the faint mask every earlier probe
used). Faint-star amplitude is reported beside it as the cross-check that this run reproduces
the v20/v21 numbers it claims to extend.

## v23: the same table over the causal arms

v22 ran this over 8 / 21 / 60 and answered P8'/P9': at matched noise v19d holds nebulosity at
parity (0.97-1.00, every arm) and leads the 1.5-4 px band, so the star-only gate hid no trade
and v19d dominates end to end.

That verdict does NOT transfer to the v23 arms for free, which is why this is re-run rather than
assumed. `--pair-time far` changes what the operator learned to keep, and v22's own lesson is
that structure behaviour is not predictable from the frontier: the raw-operator deficit it found
was real and vanished under the blend. So if PA holds and `60 far` wins on amplitude, the
question "did it buy that with nebulosity" is open again for a model nobody has looked at.

  P10: `60 far` keeps nebulosity at parity (>= 0.97) like every arm so far. A far-pairing model
       that wins the frontier while dropping below that is not shippable on the frontier alone,
       and would say the residue pathway trades structure for stars rather than removing a
       corruption.
"""
import io
import json
import os
import sys

import numpy as np
from scipy.ndimage import gaussian_filter

import n2n_metrics as M
import n2n_smoke as S
from n2n_gate import Gate, FAINT_BUCKET
from n2n_paths import cache

BIG = cache("n2n-big")
D8 = cache("n2n-d8")
C21 = cache("n2n-c21")
EVAL = cache("n2n-eval4")
CELLS = 48
BLENDS = (1.0, 0.85, 0.7, 0.55, 0.4, 0.25, 0.1)
LEVELS = (0.90, 0.85)
BANDS = [("4-16px", 2.0, 8.0), ("1.5-4px", 0.75, 2.0)]
REGION_FLOOR = 0.55

E8 = cache("n2n-e8")
F8 = cache("n2n-f8")

ARMS = [("60 any", BIG, "n2n_v17c_s%d_final.pt"),
        ("21 any", C21, "n2n_v19c_s%d_final.pt"),
        ("8 D", D8, "n2n_v19d_s%d_final.pt"),
        ("8 E", E8, "n2n_v23e_s%d_final.pt"),
        ("8 F", F8, "n2n_v24f_s%d_final.pt")]


def regions_and_bands(gate):
    """Per-cell nebula region (star-free AND brighter-half scene) + the master's band images."""
    regions, mbands = [], {n: [] for n, _l, _h in BANDS}
    for i, lm in enumerate(gate.lm):
        scene = gaussian_filter(lm, 8.0)
        region = gate.masks[i] & (scene > np.quantile(scene, REGION_FLOOR))
        regions.append(region)
        for name, lo, hi in BANDS:
            b = gaussian_filter(lm, lo) - gaussian_filter(lm, hi)
            mbands[name].append(b)
    return regions, mbands


def struct_k(gate, den_m, regions, mbands):
    """k per band: the summed projection of the denoised band onto the master's, region-masked."""
    la = S.crop(den_m).mean(axis=1)
    out = {}
    for name, lo, hi in BANDS:
        num = den = 0.0
        for i in range(len(la)):
            r = regions[i]
            if r.sum() < 2000:
                continue
            bm = mbands[name][i][r]
            bm = bm - bm.mean()
            bo = (gaussian_filter(la[i], lo) - gaussian_filter(la[i], hi))[r]
            bo = bo - bo.mean()
            num += float(np.dot(bo, bm))
            den += float(np.dot(bm, bm))
        out[name] = num / den
    return out


def curve(gate, model, planes, regions, mbands):
    """(noise, faint amp, struct per band) across the blend knob; struct is analytic in a."""
    den_m = gate._forward(model, planes, gate.masters)
    k = struct_k(gate, den_m, regions, mbands)
    rows = []
    for a in BLENDS:
        la = S.crop(gate.masters + a * (den_m - gate.masters)).mean(axis=1)
        noise = float(np.mean([M.bg_stats(t)[1] for t in la])) / gate.base_noise
        amp, _det, _ = M.measure(la, gate.stars, gate.lm)
        row = dict(a=a, noise=noise, amp=float(amp[FAINT_BUCKET]))
        for name, _lo, _hi in BANDS:
            row[f"struct {name}"] = 1.0 - a * (1.0 - k[name])
        rows.append(row)
    return rows


def at(rows, level, key):
    order = sorted(rows, key=lambda r: r["noise"])
    xs = [r["noise"] for r in order]
    if level < xs[0] or level > xs[-1]:
        return None
    return float(np.interp(level, xs, [r[key] for r in order]))


def short(session):
    parts = session.split("|")
    return f"{parts[1].replace('ZWO ', '').replace('SVBONY ', '')} / {parts[2]}"[:42]


def main():
    import torch
    dev = "cuda" if torch.cuda.is_available() else "cpu"
    mm, meta = S.open_cache(EVAL)
    observers = S.observer_cells(meta, 0, CELLS)
    print(f"device {dev}, {len(observers)} observers")

    models = {}
    for label, cache, pattern in ARMS:
        for si in range(3):
            try:
                models[(label, si)] = S.load_model(cache, pattern % si, dev)
            except FileNotFoundError:
                print(f"  {label} seed {si}: no _final.pt")
    print(f"{len(models)} checkpoints\n")

    results = {}
    for session, cells in observers:
        gate = Gate(mm, cells, dev)
        regions, mbands = regions_and_bands(gate)
        print(f"=== {short(session)} ({len(cells)} cells) ===", flush=True)
        for label, _c, _p in ARMS:
            rows3 = [curve(gate, *models[(label, si)], regions, mbands)
                     for si in range(3) if (label, si) in models]
            for lv in LEVELS:
                for key in ["amp"] + [f"struct {n}" for n, _l, _h in BANDS]:
                    vals = [at(r, lv, key) for r in rows3]
                    got = [v for v in vals if v is not None]
                    results[(session, label, lv, key)] = \
                        (float(np.mean(got)) if got else None, len(got), vals)
        for label, _c, _p in ARMS:
            cells_txt = []
            for key in ("amp", "struct 4-16px", "struct 1.5-4px"):
                m, n, _ = results[(session, label, 0.90, key)]
                cells_txt.append("   -  " if m is None else f"{m:.3f}({n})")
            print(f"    {label:10s} @0.90  amp {cells_txt[0]}  "
                  f"struct4-16 {cells_txt[1]}  struct1.5-4 {cells_txt[2]}")
        print()

    for lv in LEVELS:
        for key, title in (("struct 4-16px", "nebulosity kept, 4-16 px band"),
                           ("struct 1.5-4px", "fine structure kept, 1.5-4 px band"),
                           ("amp", "faint amplitude kept (cross-check vs v20/v21)")):
            print(f"=== {title}, at matched noise {lv:.2f} ===")
            print(f"{'observer':44s}" + "".join(f"{l:>12s}" for l, _c, _p in ARMS))
            for session, _cells in observers:
                row = f"{short(session):44s}"
                for label, _c, _p in ARMS:
                    m, n, _ = results[(session, label, lv, key)]
                    row += "       -    " if m is None else f"{m:9.3f}({n})"
                print(row)
            print()

    with io.open(os.path.join("..", "structfrontier-results.json"), "w",
                 encoding="utf-8") as f:
        json.dump({f"{s}|{l}|{lv}|{k}": {"mean": v[0], "n": v[1], "per_seed": v[2]}
                   for (s, l, lv, k), v in results.items()}, f, indent=1)
    print("written ../structfrontier-results.json")
    return 0


if __name__ == "__main__":
    sys.exit(main())
