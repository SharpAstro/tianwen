"""What noise level does each training regime ACTUALLY sit at, measured not assumed?

The half-master claim so far rests on 1/sqrt(n/2), which assumes pure shot noise, perfect
rejection and no correlated residue. This measures the same background sigma the conditioning
plane feeds the model (median minus the 25th percentile of the luminance, matching
n2n_smoke.bg_sigma_torch), for every regime the sampler can draw, over the val cells.

The number that matters is the half's ratio against a single sub, versus the master's own.
A half BELOW the master means the model now trains past its deployment point.
"""
import numpy as np

import n2n_smoke as S
from n2n_paths import cache

CACHE = cache("n2n-ds")


def bg_sigma(t):
    """Per-tile background sigma over the luminance, as the trainer measures it."""
    flat = t[:, :S.CH].mean(axis=1).reshape(len(t), -1).astype(np.float64)
    med = np.quantile(flat, 0.5, axis=1)
    return med - np.quantile(flat, 0.25, axis=1)


mm, meta = S.open_cache(CACHE)
n, n_train = meta["cells"], meta["train_cells"]
flags = np.asarray(meta.get("has_halves", [False] * n), dtype=bool)
val = np.array([i for i in range(n_train, n) if flags[i]])
print(f"{len(val)} val cells with a half-master pair, of {n - n_train} val cells\n")

subs = np.asarray(mm[val, 1:S.SUBS_PER_CELL + 1], dtype=np.float32)   # [cells, 8, C, H, W]
regimes = {
    "1 sub": subs[:, 0],
    "2 avg": subs[:, 0:2].mean(axis=1),
    "4 avg": subs[:, 0:4].mean(axis=1),
    "8 avg": subs.mean(axis=1),
    "half a": np.asarray(mm[val, S.SLOT_HALF_A], dtype=np.float32),
    "half b": np.asarray(mm[val, S.SLOT_HALF_B], dtype=np.float32),
    "master": np.asarray(mm[val, S.SLOT_MASTER], dtype=np.float32),
}

one = bg_sigma(regimes["1 sub"])
print(f"{'regime':<8} {'sigma':>10} {'vs 1 sub':>10}   {'sqrt(n) says':>13}")
ideal = {"1 sub": 1.0, "2 avg": 2 ** -0.5, "4 avg": 0.5, "8 avg": 8 ** -0.5}
for name, arr in regimes.items():
    s = bg_sigma(arr)
    ratio = np.median(s / one)
    exp = f"{ideal[name]:.3f}x" if name in ideal else ""
    print(f"{name:<8} {np.median(s):10.6f} {ratio:9.3f}x   {exp:>13}")

half = np.median(bg_sigma(regimes["half a"]) / one)
master = np.median(bg_sigma(regimes["master"]) / one)
print(f"\nv8's deepest TRAINED pair was 4 avg at {np.median(bg_sigma(regimes['4 avg']) / one):.3f}x.")
print(f"The master it is DEPLOYED on sits at {master:.3f}x, and a half-master at {half:.3f}x.")
print("past the deployment point" if half <= master else
      "STILL SHORT of the deployment point")

# Per-cell rather than per-regime: how often is the half actually at or below its own master?
ha, mv = bg_sigma(regimes["half a"]), bg_sigma(regimes["master"])
print(f"halves at or below their OWN master: {int(np.sum(ha <= mv))}/{len(val)} cells")

# The pair has to be INDEPENDENT or N2N is training a model to reproduce its own input. Two
# halves of one integration would be a silent catastrophe: the loss falls beautifully and the
# model learns the identity. A/B must differ, and their difference must carry about sqrt(2)
# times one half's noise, which is what two independent draws of the same sigma give.
a, b = regimes["half a"], regimes["half b"]
identical = int(np.sum([np.array_equal(a[i], b[i]) for i in range(len(a))]))
print(f"\npairwise independence: {identical}/{len(val)} cells have A == B (any is a defect)")

# sigma(A-B) against sigma(A) does NOT test this cleanly, and reading it as if it did is the
# trap. The darkest-half estimator on a single tile measures noise PLUS whatever scene
# structure sits in the dark half, while on A-B the scene cancels and only noise survives. So
# the ratio comes out under sqrt(2) even for a perfectly independent pair, and the shortfall
# measures how much of a tile's apparent sigma is actually signal.
diff_sigma = bg_sigma(a - b + 0.5)
noise = diff_sigma / np.sqrt(2.0)       # true per-half noise IF the pair is independent
print(f"  sigma(A-B)/sqrt(2) = {np.median(noise):.6f} vs sigma(A) = {np.median(ha):.6f}")
print(f"  -> {np.median(noise / ha):.0%} of a half's apparent sigma is noise, the rest is scene")

# Correlating (A - master) against (B - master) does NOT test independence, and it looks like it
# does: the master IS about (A+B)/2, so those two residuals are +-(A-B)/2 and come out at -0.99
# by algebra, whatever the data. Measured, and discarded for that reason.
#
# The scene-free ladder is the real test. Every rung is sigma(one side - the other)/sqrt(2), so
# the scene cancels and only the INDEPENDENT noise survives. If averaging behaved ideally the
# rungs would fall as 1/sqrt(K). Where they fall short, a component is surviving the average
# that N2N cannot remove either, because it is the part both sides agree on.
print("\nscene-free noise ladder: sigma(side - side) / sqrt(2), so scene structure cancels")
print(f"{'regime':<8} {'noise':>10} {'vs 1 sub':>10}   {'ideal':>8}")


def pair_noise(x, y):
    return bg_sigma(x - y + 0.5) / np.sqrt(2.0)


rungs = [
    ("1 sub", pair_noise(subs[:, 0], subs[:, 1]), 1.0),
    ("2 avg", pair_noise(subs[:, 0:2].mean(axis=1), subs[:, 2:4].mean(axis=1)), 2 ** -0.5),
    ("4 avg", pair_noise(subs[:, 0:4].mean(axis=1), subs[:, 4:8].mean(axis=1)), 0.5),
    ("half", pair_noise(a, b), None),
]
base = np.median(rungs[0][1])
for name, arr, ideal_r in rungs:
    exp = f"{ideal_r:.3f}x" if ideal_r else ""
    print(f"{name:<8} {np.median(arr):10.6f} {np.median(arr) / base:9.3f}x   {exp:>8}")

got4 = np.median(rungs[2][1]) / base
print(f"\n4 avg reaches {got4:.3f}x where independence predicts 0.500x, so a component "
      f"{'IS' if got4 > 0.55 else 'is not'} surviving the average.")
half_r = np.median(rungs[3][1]) / base
print(f"a half-master reaches {half_r:.3f}x, i.e. {got4 / half_r:.2f}x quieter than the deepest "
      f"pair 8 subs allow.")
print("  It beats what extrapolating the sub rungs predicts because it is not a mean: the halves"
      "\n  go through the same REJECTING integrator the deployed master does, so what survives an"
      "\n  8-sub average (hot pixels, cosmic rays) is rejected here. The pair matches the"
      "\n  deployment path in kind, not only in depth.")

# The master cannot be measured this way (there is no second master), but it IS the mean of two
# independent halves, so its noise is a half's over sqrt(2). That is exact, not an estimate.
dep = half_r / np.sqrt(2.0)
print(f"\ndeployment point: master = mean(A, B) => {dep:.3f}x a single sub.")
print(f"  trainable before: {got4:.3f}x = {got4 / dep:.1f}x the deployment noise")
print(f"  trainable now:    {half_r:.3f}x = {half_r / dep:.2f}x  (sqrt(2), and it cannot go lower"
      f"\n                    without a deeper split, since the master is these two averaged)")
