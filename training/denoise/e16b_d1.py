"""E16b's check D1, as amended (docs/plans/denoiser-training.md, "E16b, amended before any export, cache or model of
it existed"): the control cache holds E16a's cells, except in the sessions whose P0 sample the recipe-3 store moved
(the Pleiades and Triangulum, 2 of 300 cells each, so their seeded picks differ), and on every shared cell its clean
tile (slot 0) is E16a's within 2 fp16 steps, the rounding the stores' own P0 master tiles differ by.

    python e16b_d1.py <E16a cache> <control cache>

Prints one line starting True or False, and exits 1 on False.
"""
import sys

import numpy as np

import n2n_smoke as S

MOVED = ("Pleiades/2025-10-28", "Triangulum-Galaxy/2025-10-28")
MAX_ABS, MAX_MEAN = 4.9e-4, 1e-5

a, ma = S.open_cache(sys.argv[1])
b, mb = S.open_cache(sys.argv[2])
ka, kb = [tuple(k) for k in ma["keys"]], [tuple(k) for k in mb["keys"]]
ia, ib = {k: i for i, k in enumerate(ka)}, {k: i for i, k in enumerate(kb)}
shared = [k for k in ka if k in ib]
moved = sorted({k[0] for k in ka if k not in ib} | {k[0] for k in kb if k not in ia})
unexpected = [s for s in moved if not any(m in s for m in MOVED)]
worst, total, n = 0.0, 0.0, 0
for k in shared:
    d = np.abs(a[ia[k], 0].astype(np.float32) - b[ib[k], 0].astype(np.float32))
    worst, total, n = max(worst, float(d.max())), total + float(d.sum()), n + d.size
mean = total / max(n, 1)
ok = not unexpected and worst <= MAX_ABS and mean < MAX_MEAN
print(ok, f"shared {len(shared)} of {len(ka)} / {len(kb)} cells; clean max |d| {worst:.2e}, mean |d| {mean:.2e}; "
          f"sessions whose cells differ: {[s.split('|')[0] for s in moved]}; unexpected: {[s.split('|')[0] for s in unexpected]}")
sys.exit(0 if ok else 1)
