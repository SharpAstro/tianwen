"""How many of the store's masters hold a star like the one on HIP 80609's master (Antares; #2292): a star whose light
still stands far out (2026-11-20).

    python -I bright_census.py <store> <out tsv> [bright sigma, default 100] [screen sigma, default 3]

Per session master (the flip variants and side files left out) EVERY bright source is a candidate, by a rule that does
not rank them. The frame is read at every 4th row and column (a quarter of the file's pages, so a run beside a trainer
does not flood the file cache) and smoothed over 3 samples. A candidate is a sample that stands <bright sigma> pixel
sigmas over the frame's median and is the largest within 60 px of itself (a flat top, as a saturated star has, is one
candidate). It is screened on that quarter frame (the median of its 100-140 px ring less the median of its 300-400 px
ring must reach <screen sigma>), and those that pass are measured at full resolution in a crop round the star: the ring
medians less the crop's own sky (its median at 300 to 400 px), on the channels' mean, in pixel sigma (neighbour
differences of a central 512 px crop). Rings: 8-22, 22-40, 40-60, 60-100, 100-140, 140-200, 200-300 px. A ring that
runs off the frame or into the canvas ring is read on what is there, and left blank under a quarter of it.

Why no "N brightest": the first two versions took the 4 and then the 12 brightest peaks of a master. Peak brightness is
flat among saturated stars, so the rank said nothing about a halo, and stars with the largest halos sat behind any N
(the review of #2306 found four on the two Pleiades masters at ranks 15 to 19, after a 12-peak run had been called
complete because each master's 12th peak was under the bar). A test on the last peak taken cannot bound the peaks not
taken.

Over the bar means 5 sigma or more at 100-140 px. The summary splits those rows in two by the profile, because a star's
light falls with distance and a patch of nebula's does not: FALLING where the 8-22 px ring is at least FALL (4) times
the 100-140 px ring, FLAT otherwise. The twenty-four stars it found read 14 to 468. The falling rows are the
ones to look at (bright_census_look.py with a min fall of 4); a spread over 8 sectors is written too, and does not
tell a star from a nebula on its own (a spike spreads a star's ring as a nebula does).

What this rule does not see, so that its count is read for what it is: the fainter of two sources within 60 px of each
other; a star within 60 px of the frame's edge; a source under <bright sigma> on the quarter frame; and a star sitting
on nebula so bright that its own profile reads flat (it is in the flat rows, and its far ring is the nebula's).

Written: one row a candidate measured. Printed: the falling rows over the bar, each with its master's whole name, and
before them a NOT READ or SKIPPED line for each master that could not be searched (a master searched that holds no such
source gives no row, and that is a finding).
The injector draws a halo to 45 px and the plate builder cuts one flat at about 120 px, so a star that stands 5 sigma
over its sky at 100-140 px is one neither has ever shown a model."""
import os, sys, time
import numpy as np
from astropy.io import fits
from scipy.ndimage import label, maximum_filter, maximum_position, uniform_filter

STORE, OUT = sys.argv[1], sys.argv[2]
BRIGHT = float(sys.argv[3]) if len(sys.argv) > 3 else 100.0
SCREEN = float(sys.argv[4]) if len(sys.argv) > 4 else 3.0
BAR, FALL = 5.0, 4.0
EDGES = [8, 22, 40, 60, 100, 140, 200, 300]
R, STEP, APART, NEED = 400, 4, 60, 0.25
yy, xx = np.mgrid[-R:R + 1, -R:R + 1]
dist = np.hypot(xx, yy); ang = np.arctan2(yy, xx)
sector = np.floor((ang + np.pi) / (2 * np.pi) * 8).clip(0, 7).astype(int)
sky_ring = (dist >= 300) & (dist < 400)
rings = [(dist >= lo) & (dist < hi) for lo, hi in zip(EDGES[:-1], EDGES[1:])]
sectors = {i: [rings[i] & (sector == s) for s in range(8)] for i in (4, 5)}
# The same two rings on the quarter frame, as sample offsets, for the screen.
q = R // STEP
qy, qx = np.mgrid[-q:q + 1, -q:q + 1]
qd = np.hypot(qx, qy) * STEP
near = tuple(a - q for a in np.nonzero((qd >= 100) & (qd < 140)))
far = tuple(a - q for a in np.nonzero((qd >= 300) & (qd < 400)))


def median_of(values, mask):
    """The median of a ring's pixels that are there; NaN under a quarter of the ring."""
    v = values[mask]
    v = v[np.isfinite(v)]
    return float(np.median(v)) if v.size >= NEED * int(mask.sum()) else float("nan")


def quarter_ring(padded, py, px, offsets):
    v = padded[py + q + 1 + offsets[0], px + q + 1 + offsets[1]]
    v = v[np.isfinite(v)]
    return float(np.median(v)) if v.size >= NEED * offsets[0].size else float("nan")


def crop_round(d, y0, x0, half):
    """The channels' mean in a square round (y0, x0); NaN outside the frame and where the canvas ring's zeros are."""
    H, W = d.shape[-2:]
    ya, xa = y0 - half, x0 - half
    sy0, sy1, sx0, sx1 = max(ya, 0), min(y0 + half + 1, H), max(xa, 0), min(x0 + half + 1, W)
    c = np.asarray(d[..., sy0:sy1, sx0:sx1], np.float32)
    c = c if c.ndim == 2 else c[:3].mean(axis=0)
    out = np.full((2 * half + 1, 2 * half + 1), np.nan, np.float32)
    out[sy0 - ya:sy1 - ya, sx0 - xa:sx1 - xa] = np.where(np.isfinite(c) & (c != 0), c, np.nan)
    return out


masters = sorted(f for f in os.listdir(f"{STORE}/session-masters") if f.endswith(".fits") and "flip=" not in f)
rows = []
gave_row = set()      # the masters that yielded at least one row
read = 0              # the masters searched; one that could not be searched is named (NOT READ, SKIPPED), never dropped silently
found = measured = at_edge = unread = 0
t0 = time.time()
with open(OUT, "w", encoding="utf-8") as out:
    out.write("session\tx\ty\tpeak_sigma\t" + "\t".join(f"r{lo}_{hi}" for lo, hi in zip(EDGES[:-1], EDGES[1:])) + "\tspread_100_140\tspread_140_200\n")
    for k, name in enumerate(masters):
        try:
            with fits.open(f"{STORE}/session-masters/{name}", memmap=True) as h:
                d = h[0].data
                if d is None or d.ndim not in (2, 3):
                    print(f"NOT READ {name}: no 2-D or 3-D image data", flush=True)
                    continue
                H, W = d.shape[-2:]
                if min(H, W) < 2 * R + 200:
                    print(f"NOT READ {name}: {W} x {H} px is under the {2 * R + 200} px a star's rings need", flush=True)
                    continue
                lum4 = np.asarray(d[..., ::STEP, ::STEP], np.float32)
                lum4 = lum4 if lum4.ndim == 2 else lum4[:3].mean(axis=0)
                cy, cx = H // 2, W // 2
                c = np.asarray(d[..., cy - 256:cy + 256, cx - 256:cx + 256], np.float32)
                c = c if c.ndim == 2 else c[:3].mean(axis=0)
                sig = 1.4826 * float(np.nanmedian(np.abs(np.diff(c, axis=1)))) / np.sqrt(2)
                if not np.isfinite(sig) or sig <= 0:
                    print(f"NOT READ {name}: no usable pixel sigma in the central crop", flush=True)
                    continue
                ok4 = np.isfinite(lum4) & (lum4 != 0)
                frame_sky = float(np.median(lum4[ok4]))
                s = uniform_filter(np.where(ok4, lum4, frame_sky), 3)
                padded = np.pad(np.where(ok4, lum4, np.nan), q + 1, constant_values=np.nan)
                top = (s == maximum_filter(s, 2 * (APART // STEP) + 1)) & (s > frame_sky + BRIGHT * sig)
                patches, n = label(top, structure=np.ones((3, 3), int))    # a flat top is one candidate, not one a sample
                peaks = maximum_position(s, patches, np.arange(1, n + 1)) if n else []
                found += n
                m = APART // STEP
                h4, w4 = s.shape
                taken = []
                for py, px in peaks:
                    py, px = int(py), int(px)
                    if py < m or px < m or py >= h4 - m or px >= w4 - m:
                        at_edge += 1
                        continue
                    near_q, far_q = quarter_ring(padded, py, px, near), quarter_ring(padded, py, px, far)
                    if not (np.isfinite(near_q) and np.isfinite(far_q)):
                        unread += 1
                        continue
                    if (near_q - far_q) / sig < SCREEN:
                        continue
                    crop = crop_round(d, py * STEP, px * STEP, R + 8)
                    inner = uniform_filter(np.nan_to_num(crop[R:R + 17, R:R + 17], nan=frame_sky), 3)   # the centre, within 8 px
                    dy, dx = np.unravel_index(int(np.argmax(inner)), inner.shape)
                    x, y = px * STEP - 8 + dx, py * STEP - 8 + dy
                    if any(abs(x - tx) < 20 and abs(y - ty) < 20 for tx, ty in taken):
                        continue                                           # two candidates that refine to one star
                    cut = crop[dy:dy + 2 * R + 1, dx:dx + 2 * R + 1]
                    sky = median_of(cut, sky_ring)
                    if not np.isfinite(sky):
                        unread += 1
                        continue
                    vals = [(median_of(cut, r) - sky) / sig for r in rings]
                    spread = []
                    for i in (4, 5):
                        sec = np.array([median_of(cut, sm) - sky for sm in sectors[i]]) / sig
                        spread.append(float((np.nanmax(sec) - np.nanmin(sec)) / vals[i]) if vals[i] > 0.5 and np.isfinite(sec).any() else float("nan"))
                    core = cut[R - 1:R + 2, R - 1:R + 2]
                    peak = (float(np.nanmax(core)) - sky) / sig if np.isfinite(core).any() else float("nan")
                    row = (name[:-5], x, y, peak, vals, spread)
                    rows.append(row)
                    taken.append((x, y))
                    measured += 1
                    out.write(f"{row[0]}\t{x}\t{y}\t{peak:.0f}\t" + "\t".join(f"{v:.2f}" for v in vals) + f"\t{spread[0]:.2f}\t{spread[1]:.2f}\n")
                    gave_row.add(name)       # only once a row is written, so a master that fails later is still counted right
                read += 1                    # searched; no row is then a finding (no source with a far ring), not a loss
                out.flush()
        except Exception as e:  # a master that cannot be read is named and the census goes on
            print(f"SKIPPED {name}: {type(e).__name__} {e}", flush=True)
        if (k + 1) % 25 == 0:
            print(f"{k + 1}/{len(masters)} masters, {len(rows)} rows, {time.time() - t0:.0f} s", flush=True)
over = [r for r in rows if r[4][4] >= BAR]
falling = sorted((r for r in over if r[4][0] >= FALL * r[4][4]), key=lambda r: -r[4][4])
print(f"{len(masters)} masters found, {read} searched ({len(masters) - read} named above as NOT READ or SKIPPED), "
      f"{len(gave_row)} with a source that passed the screen")
print(f"{found} bright sources (over {BRIGHT:g} sigma on the quarter frame, the largest within {APART} px); {at_edge} within {APART} px of an edge and "
      f"{unread} with too little of their rings in the frame were not read; {measured} passed the screen ({SCREEN:g} sigma) and were measured")
print(f"standing {BAR:g} sigma or more at 100-140 px: {len(over)} in {len({r[0] for r in over})} masters")
print(f"  FALLING (8-22 px at least {FALL:g} times 100-140 px, a star's profile): {len(falling)} in {len({r[0] for r in falling})} masters; look at every one")
print(f"  FLAT (nebula, a cluster's or a galaxy's body, or a star on nebula that bright): {len(over) - len(falling)}")
print("    x     y   peak   " + " ".join(f"{lo}-{hi}".rjust(8) for lo, hi in zip(EDGES[:-1], EDGES[1:])) + "   spread        master")
for r in falling:
    print(f"{r[1]:5d} {r[2]:5d} {r[3]:6.0f}   " + " ".join(f"{v:8.1f}" for v in r[4]) + f"   {r[5][0]:5.2f} / {r[5][1]:5.2f}   {r[0]}")
