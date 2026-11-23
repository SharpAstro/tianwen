"""The planted-star test, step 1 (2026-11-23): add stars of known light to a master, on bright nebula and on dark sky.

    python -I plant_stars.py <master> <gaia.csv> <out folder> --stamp layer --plate <a remover's starless plate of the master>
                             [--seeds 1,2,3] [--per-block 2] [--light 7,150]
    python -I plant_stars.py <master> <gaia.csv> <out folder> --stamp psf | donor

The question it was built for: is a faint star on bright nebula left because of the nebula under it, or because it is
faint? A real star cannot say: its light is known only as measured, and on bright nebula half of what a catalogue
calls a star at 20 sky sigma is a bump of the nebula (nebula_stars_row.py's magnitude check). A planted star's light is
known exactly, and the same star is added twice: once on bright nebula (a 32 px block 20 sky sigma or more over the
frame's sky, as nebula_stars_row.py has it) and once on dark sky (under 5), each spot with no catalogued star brighter
than BP 17.5 within 8 px and no other plant within 14 px. The spots depend on the seed alone, so the three stamps
below are planted at the same places.

What is planted (--stamp), each a 21 px patch tapered from 1 at 4.5 px to 0 at 7.5 px and added at whole pixels unless
said otherwise:
  layer   a real dark-sky star's own light without its noise: the star layer (master less --plate) of a star that
          remover took whole (0.9 to 1.1 of its light). The faithful one: a real star's shape, the noise of the spot.
  psf     the master's mean star (the median of its bright isolated dark-sky stars, 150 to 1500 sky sigma, centred and
          normalised), noise-free, at a random sub-pixel place, its light drawn evenly in log over --light.
  donor   a real dark-sky star's patch of the MASTER less its far ring: its noise comes with it, so the spot holds the
          noise twice inside 7.5 px. Kept because it needs no plate and no interpolation.
A donor (layer, donor) is a catalogued star on dark sky with no other within 2.5 mag of it inside 12 px (a Milky Way
field holds a catalogued star every 8 px, most far under the noise), whose light is --light sky sigma; donors are
drawn evenly in log light. "Light" is nebula_stars_row.py's: a 2 px disc less the ring median 5 to 9 px out, in the
master's sky noise.

Per seed: planted-<seed>.fits (the master plus the stamps, the master's header) and plants-<seed>.tsv (kind, x, y, the
donor, the light planted in sky sigma, the spot's level and its local noise over the sky's). plant_read.py reads them.
A one-plane master is expected (the Sagittarius scoring master is one).
"""
import argparse
import csv
import os

import numpy as np
from astropy.io import fits
from scipy.spatial import cKDTree

p = argparse.ArgumentParser()
p.add_argument("master")
p.add_argument("gaia")
p.add_argument("out")
p.add_argument("--stamp", choices=("layer", "psf", "donor"), required=True)
p.add_argument("--plate", default=None, help="--stamp layer: the starless plate whose star layer the stamps are cut from")
p.add_argument("--seeds", default="1,2,3")
p.add_argument("--per-block", type=int, default=2)
p.add_argument("--light", default="7,150")
a = p.parse_args()
if (a.stamp == "layer") != (a.plate is not None):
    p.error("--plate goes with --stamp layer, and only with it")
LO, HI = (float(v) for v in a.light.split(","))
BRIGHT, DARK, B, R = 20.0, 5.0, 32, 10

hdr = fits.getheader(a.master)
raw = np.asarray(fits.getdata(a.master))
if raw.ndim != 2:
    raise SystemExit("a one-plane master is expected")
m = np.nan_to_num(raw.astype(np.float64))
H, W = m.shape
noise = 1.4826 * float(np.median(np.abs(np.diff(m[::3], axis=1)))) / np.sqrt(2)
bh, bw = H // B, W // B
lvl = np.median(m[:bh * B, :bw * B].reshape(bh, B, bw, B).transpose(0, 2, 1, 3).reshape(bh, bw, -1), axis=2)
lvl = (lvl - np.percentile(lvl, 5)) / noise
cat = np.array([(float(r["x"]), float(r["y"]), float(r["mag"])) for r in csv.DictReader(open(a.gaia, encoding="utf-8"))])
tree = cKDTree(cat[:, :2])

yy, xx = np.mgrid[-R:R + 1, -R:R + 1]
rr = np.hypot(xx, yy)
taper = 0.5 - 0.5 * np.cos(np.pi * np.clip((7.5 - rr) / 3.0, 0.0, 1.0))
far = (rr >= 7.5) & (rr <= 10.0)
disc = rr <= 2.0
ring = (rr >= 5.0) & (rr <= 9.0)


def row_light(stamp):
    return float((stamp - np.median(stamp[ring]))[disc].sum())


def local_noise(x, y):
    q = m[y - R:y + R + 1, x - R:x + R + 1]
    return 1.4826 * float(np.median(np.abs(np.diff(q, axis=1)))) / np.sqrt(2) / noise


def dark_isolated(i, within_mag):
    fx, fy, mag = cat[i]
    x, y = int(round(fx)), int(round(fy))
    if not (16 <= x < bw * B - 16 and 16 <= y < bh * B - 16) or lvl[y // B, x // B] >= DARK:
        return None
    if any(j != i and cat[j, 2] < mag + within_mag for j in tree.query_ball_point((fx, fy), 12.0)):
        return None
    return x, y


psf, donors = None, []
if a.stamp == "psf":
    from scipy.ndimage import shift as nd_shift
    stack, peak_cap = [], 0.5 * float(m.max())
    for i in range(len(cat)):
        at = dark_isolated(i, 4.0)
        if at is None:
            continue
        q = m[at[1] - R:at[1] + R + 1, at[0] - R:at[0] + R + 1]
        s = q - np.median(q[far])
        if not (150 <= row_light(s) / noise < 1500) or q.max() > peak_cap:
            continue
        core = np.clip(s, 0, None) * (rr <= 4)
        s = nd_shift(s, (-float((core * yy).sum() / core.sum()), -float((core * xx).sum() / core.sum())), order=3, mode="nearest")
        stack.append(s / row_light(s))
    psf = np.median(np.array(stack), axis=0) * taper
    psf /= row_light(psf)
    print(f"the master's mean star: {len(stack)} stars stacked; light inside 2 px over all of it {psf[disc].sum() / psf.sum():.2f}")
else:
    layer = m - np.nan_to_num(np.asarray(fits.getdata(a.plate)).astype(np.float64)) if a.stamp == "layer" else None
    for i in range(len(cat)):
        at = dark_isolated(i, 2.5)
        if at is None:
            continue
        x, y = at
        q = m[y - R:y + R + 1, x - R:x + R + 1]
        if a.stamp == "donor":
            stamp = (q - np.median(q[far])) * taper
            light = row_light(stamp) / noise
        else:
            seen = row_light(q) / noise
            ql = layer[y - R:y + R + 1, x - R:x + R + 1]
            stamp = (ql - np.median(ql[ring])) * taper
            light = row_light(stamp) / noise
            if seen <= 0 or not (0.9 <= light / seen <= 1.1):     # a star the plate's remover took whole
                continue
        if LO <= light < HI:
            donors.append((x, y, float(cat[i, 2]), light, stamp))
    logl = np.log([d[3] for d in donors])
    print(f"{len(donors)} donors of {LO:g} to {HI:g} sky sigma on dark sky")
print(f"sky noise {noise:.4g}; {int((lvl >= BRIGHT).sum())} bright blocks, {int((lvl < DARK).sum())} dark blocks")
os.makedirs(a.out, exist_ok=True)

for seed in (int(s) for s in a.seeds.split(",")):
    rng = np.random.default_rng(seed)
    planted = m.copy()
    spots = []

    def free(x, y):
        if not (16 <= x < bw * B - 16 and 16 <= y < bh * B - 16):
            return False
        if any(cat[j, 2] < 17.5 for j in tree.query_ball_point((x, y), 8.0)):
            return False
        return all((x - sx) ** 2 + (y - sy) ** 2 >= 14 ** 2 for sx, sy in spots)

    def place(blocks, n):
        got = []
        for by, bx in blocks:
            k = 0
            for _ in range(200):
                if k == n:
                    break
                x, y = int(bx * B + rng.integers(0, B)), int(by * B + rng.integers(0, B))
                if free(x, y):
                    spots.append((x, y))
                    got.append((x, y))
                    k += 1
        return got

    # the spots first, and from the seed alone, so every --stamp plants at the same places
    bright = place([tuple(v) for v in np.argwhere(lvl >= BRIGHT)], a.per_block)
    dark_blocks = [tuple(v) for v in np.argwhere(lvl < DARK)]
    order = rng.permutation(len(dark_blocks))
    dark = place([dark_blocks[j] for j in order[:len(bright)]], 1)
    n = min(len(bright), len(dark))
    stamp_rng = np.random.default_rng([seed, 1])
    rows, used = [], set()
    want = stamp_rng.uniform(np.log(LO), np.log(HI), n)
    for k in range(n):
        if a.stamp == "psf":
            sy, sx = stamp_rng.uniform(-0.5, 0.5, 2)
            stamp = nd_shift(psf, (sy, sx), order=3, mode="constant")
            light = float(np.exp(want[k]))
            stamp = stamp * (light * noise / row_light(stamp))
            dx, dy, mag = -1, -1, 0.0
        else:
            taken_already = np.where(np.isin(np.arange(len(donors)), list(used)), 1e9, 0.0)
            j = int(np.argmin(np.abs(logl - want[k]) + taken_already))
            used.add(j)
            dx, dy, mag, light, stamp = donors[j]
        for kind, (x, y) in (("bright", bright[k]), ("dark", dark[k])):
            planted[y - R:y + R + 1, x - R:x + R + 1] += stamp
            rows.append((kind, x, y, dx, dy, mag, light, float(lvl[y // B, x // B]), local_noise(x, y)))
    fits.writeto(os.path.join(a.out, f"planted-{seed}.fits"), planted.astype(np.float32), hdr, overwrite=True)
    with open(os.path.join(a.out, f"plants-{seed}.tsv"), "w", encoding="utf-8", newline="\n") as f:
        f.write("kind\tx\ty\tdonor_x\tdonor_y\tmag\tlight\tlevel\tlocal_noise\n")
        for r in rows:
            f.write(f"{r[0]}\t{r[1]}\t{r[2]}\t{r[3]}\t{r[4]}\t{r[5]:.2f}\t{r[6]:.2f}\t{r[7]:.1f}\t{r[8]:.2f}\n")
    print(f"seed {seed}: {n} stars planted twice ({len(bright)} bright spots, {len(dark)} dark spots found)")
