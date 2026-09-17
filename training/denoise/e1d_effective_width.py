"""Re-analysis of E1d's rows against the kernel ACTUALLY applied. PsfKernel.Build point-samples the
Moffat at pixel centres, so a nominal 0.5 or 1.0 px kernel blurs less than its label says. For each
est-w / est-c row at those two widths: the effective continuous-Moffat width k_eff of the discrete
kernel composed with a Moffat core of the row's own measured clean FWHM (beta 3; beta moves k_eff by
under 0.03), and the estimate over k_eff beside the estimate over the nominal width."""
import re, sys, numpy as np
from collections import defaultdict
from moffat_discrete_kernel import discrete_kernel, place_discrete, composed_cont, invert_cont, grid_profile, fwhm_of, convolve

path = sys.argv[1] if len(sys.argv) > 1 else r"C:/temp/e2/oracle-e1d.txt"
cache = {}
def k_eff(core, k):
    key = (round(core, 2), k)
    if key not in cache:
        ax, w = discrete_kernel(k, 4.0, radius=int(np.ceil(3 * k)) + 2, area_sub=1)
        o = fwhm_of(convolve(grid_profile(core, 3.0), place_discrete(ax, w)))
        cache[key] = invert_cont(core, 3.0, o) if o > core * 1.001 else 0.0
    return cache[key]

rows = defaultdict(list)   # (arm, inj, noise) -> list of (estW/t nominal, estW/keff, ratio r/t exact? no)
for line in open(path, encoding="utf-8"):
    t = line.split()
    for arm in ("est-w", "est-c"):
        if arm in t:
            i = t.index(arm)
            try:
                inj = float(t[i - 2])
            except ValueError:
                break
            noise = t[i - 1]
            if inj not in (0.5, 1.0) or noise not in ("no", "yes"): break
            rest = t[i + 1:]
            if rest and rest[0].startswith("(no"): break
            try:
                truth = float(rest[1]); est_t = float(rest[12]); fit = rest[14]
            except (ValueError, IndexError):
                break
            ke = k_eff(truth, inj)
            est_px = est_t * inj
            rows[(arm, inj, noise)].append((est_t, est_px / ke if ke > 0 else float("nan"), truth, ke, fit))
            break

print("point-sampled beta-4 kernel; k_eff from the row's clean FWHM at beta 3; floor 0.1 px is the probe's MinEstimatedFwhm")
print(f"{'arm':6} {'inj':>4} {'noise':>5} {'n':>3} {'est/nominal p50':>15} {'est/k_eff p50':>13} {'est/k_eff p10':>13} {'est/k_eff p90':>13} {'k_eff/nominal range':>20}")
for key in sorted(rows):
    v = rows[key]
    a = np.array([x[0] for x in v]); b = np.array([x[1] for x in v]); kr = np.array([x[3] / key[1] for x in v])
    print(f"{key[0]:6} {key[1]:4.1f} {key[2]:>5} {len(v):3d} {np.median(a):15.2f} {np.nanmedian(b):13.2f} {np.nanpercentile(b, 10):13.2f} {np.nanpercentile(b, 90):13.2f} {kr.min():9.2f} to {kr.max():.2f}")
print()
print("per row at 1.0 px (est-c): master clean FWHM, k_eff, est px, est/k_eff, fit source")
for key in sorted(rows):
    if key[0] != "est-c" or key[1] != 1.0: continue
    for est_t, r, truth, ke, fit in rows[key]:
        print(f"  noise {key[2]:>3}  clean {truth:4.2f}  k_eff {ke:4.2f}  est {est_t * key[1]:4.2f} px  est/k_eff {r:4.2f}  {fit}")
