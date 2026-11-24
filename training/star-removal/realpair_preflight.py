"""Preflight for a real-pairs recipe on the rebuilt plates (2026-11-24, the owner's "plate-pixel recipe").

    python -I realpair_preflight.py <out prefix> <session name prefix> [...]

A real pair is a master tile as the input and its starless plate's tile as the target, so the model sees real stars
and the plate builder's subtraction is the truth. This asks what that truth would teach, on the stored plates
(starless) and on the rebuilt ones (starless-2449), per session:
  - at every subtracted, unsaturated catalogue star, by its significance: the plate's core (mean over a disc of one
    FWHM less the median of the annulus 3 to 5 FWHM out) in the annulus's own per-pixel noise. Under -1 the truth
    holds a dig there, over +1 it holds residue; and that core as a share of the star's own in the master;
  - what a loss mask would count: the builder's subtracted footprint, less two FWHM round every source it did not
    subtract (knots, merged, no fit, too narrow) and less 30 px round every saturated star (its halo stays in the
    plate), as a share of the frame.
Writes <prefix>-<n>.png: for the session's median cell by counted stars, master / stored plate / rebuilt plate
/ the mask, 256 px at 3x, captioned. Reads only.

The mask shares are close to, not equal to, what the exporter counts: this takes each star's FWHM from channel 0 by the
Moffat formula and its stamp skips stars near the frame edge, where RealPairMask.Counted uses the luminance FWHM and
cuts zones at the edge. Read the table as a preflight, not as the exporter's numbers; realpair_export_look.py reads
those off an export's own masks.
"""
import csv
import glob
import json
import sys

import numpy as np
from astropy.io import fits
from PIL import Image, ImageDraw
from scipy.ndimage import binary_dilation

ROOT = "D:/Astro-Dataset/2026-10-12-full"
STORES = (("stored", "starless"), ("rebuilt", "starless-2449"))
BANDS = ((20, 100), (100, 1000), (1000, 1e12))
PREFIX = sys.argv[1]


def lum(path):
    a = np.nan_to_num(np.asarray(fits.getdata(path), np.float64))
    return a.mean(axis=0) if a.ndim == 3 else a


def disc(radius):
    r = int(np.ceil(radius))
    yy, xx = np.mgrid[-r:r + 1, -r:r + 1]
    return (xx ** 2 + yy ** 2) <= radius ** 2


def stamp(mask, xs, ys, radius):
    """mask |= discs of the radius at the positions."""
    d = disc(radius)
    r = d.shape[0] // 2
    h, w = mask.shape
    for x, y in zip(xs, ys):
        x, y = int(round(x)), int(round(y))
        if r <= x < w - r and r <= y < h - r:
            mask[y - r:y + r + 1, x - r:x + r + 1] |= d


for n, want in enumerate(sys.argv[2:]):
    names = sorted(glob.glob(f"{ROOT}/starless-2449/plates/{want}*_plate.fits"))
    if len(names) != 1:
        print(f"{want}: {len(names)} plates match; skipped")
        continue
    base = names[0].replace("\\", "/").split("/plates/")[1][:-len("_plate.fits")]
    prof = json.load(open(f"{ROOT}/starless-2449/plates/{base}_plate.profile.json", encoding="utf-8"))
    ch = prof["Channels"][0]
    fwhm = 2.0 * ch["Alpha"] * np.sqrt(2.0 ** (1.0 / ch["Beta"]) - 1.0)
    rd, r0, r1 = max(2.0, fwhm), 3.0 * max(2.0, fwhm), 5.0 * max(2.0, fwhm)
    R = int(np.ceil(r1))
    yy, xx = np.mgrid[-R:R + 1, -R:R + 1]
    rr2 = xx ** 2 + yy ** 2
    dm, am = rr2 <= rd ** 2, (rr2 >= r0 ** 2) & (rr2 <= r1 ** 2)
    print(f"\n=== {base}  (FWHM {fwhm:.2f} px; core disc {rd:.1f} px, annulus {r0:.1f} to {r1:.1f} px)")
    keep = {}
    for label, store in STORES:
        p = f"{ROOT}/{store}/plates/{base}_plate"
        plate, master = lum(p + ".fits"), None
        master = plate + lum(p.replace("_plate", "_stars") + ".fits")
        rows = list(csv.DictReader(open(p + ".stars.csv", encoding="utf-8")))
        x = np.array([float(r["x"]) for r in rows])
        y = np.array([float(r["y"]) for r in rows])
        sig = np.array([float(r["significance"]) for r in rows])
        sub = np.array([r["outcome"] == "Subtracted" for r in rows])
        sat = np.array([r["saturated"] == "1" for r in rows])
        H, W = plate.shape
        inside = (x >= R + 1) & (x < W - R - 1) & (y >= R + 1) & (y < H - R - 1)
        print(f"  {label}: {len(rows)} catalogue rows, {int(sub.sum())} subtracted ({int((sub & sat).sum())} saturated), "
              f"{int((~sub).sum())} not subtracted")
        rng = np.random.default_rng(1)
        for lo, hi in BANDS:
            pick = np.where(sub & ~sat & inside & (sig >= lo) & (sig < hi))[0]
            if len(pick) > 4000:
                pick = rng.choice(pick, 4000, replace=False)
            core, share = [], []
            for k in pick:
                cx, cy = int(round(x[k])), int(round(y[k]))
                q = plate[cy - R:cy + R + 1, cx - R:cx + R + 1]
                qm = master[cy - R:cy + R + 1, cx - R:cx + R + 1]
                ring = q[am]
                noise = 1.4826 * np.median(np.abs(ring - np.median(ring)))
                if noise <= 0:
                    continue
                c = float(q[dm].mean() - np.median(ring))
                cm = float(qm[dm].mean() - np.median(qm[am]))
                core.append(c / noise)
                if cm > 0:
                    share.append(c / cm)
            core, share = np.array(core), np.array(share)
            name = f"{lo:g} to {hi:g}" if hi < 1e11 else f"{lo:g} and over"
            if len(core) < 20:
                print(f"    {name:>14s} sigma: {len(core)} stars")
                continue
            print(f"    {name:>14s} sigma: {len(core):5d} stars | core dug (under -1 noise) {100 * np.mean(core < -1):5.1f} %"
                  f" | residue (over +1) {100 * np.mean(core > 1):5.1f} % | median core {np.median(core):+.2f} noise"
                  f" | left of the star, median {100 * np.median(share):+.1f} %")
        foot = fits.getdata(p + ".subtracted.fits.gz").astype(bool)
        if foot.ndim == 3:
            foot = foot.any(axis=0)
        block = np.zeros_like(foot)
        stamp(block, x[~sub], y[~sub], 2.0 * fwhm)
        stamp(block, x[sub & sat], y[sub & sat], 30.0)
        counted = foot & ~block
        print(f"    mask: the subtracted footprint is {100 * foot.mean():.1f} % of the frame; less the kept sources and the "
              f"saturated stars' 30 px, {100 * counted.mean():.1f} % is counted")
        keep[label] = (plate, master, counted, x, y, sub & ~sat & (sig >= 20))
    # the cell of the rebuilt store with the most counted stars
    plate, master, counted, x, y, good = keep["rebuilt"]
    H, W = plate.shape
    cells = {}
    for k in np.where(good)[0]:
        cells[(int(y[k]) // 256, int(x[k]) // 256)] = cells.get((int(y[k]) // 256, int(x[k]) // 256), 0) + 1
    order = sorted(cells.items(), key=lambda kv: kv[1])
    (cy, cx), cnt = order[len(order) // 2]                 # the median cell, not the densest: an ordinary one
    sl = (slice(cy * 256, cy * 256 + 256), slice(cx * 256, cx * 256 + 256))
    lo, hi = np.percentile(master[sl], 1), np.percentile(master[sl], 99.7)

    def grey(a):
        v = np.clip((a - lo) / (hi - lo), 0, 1) ** 0.5
        return (v * 255).astype(np.uint8)

    panels = [(f"master (the input), cell x {cx * 256} y {cy * 256}, 3x", grey(master[sl])),
              ("stored plate (starless)", grey(keep["stored"][0][sl])),
              ("rebuilt plate (starless-2449): the target", grey(plate[sl])),
              (f"loss mask: white counts ({100 * counted[sl].mean():.0f} % of the cell)", (counted[sl] * 255).astype(np.uint8))]
    side, bar = 768, 18
    sh = Image.new("RGB", (2 * side + 6, 2 * (side + bar) + 6), (40, 40, 40))
    for i, (cap, g) in enumerate(panels):
        tile = Image.new("RGB", (side, side + bar), (0, 0, 0))
        ImageDraw.Draw(tile).text((4, 3), cap, fill=(255, 255, 0))
        tile.paste(Image.fromarray(g).resize((side, side), Image.NEAREST).convert("RGB"), (0, bar))
        sh.paste(tile, ((i % 2) * (side + 6), (i // 2) * (side + bar + 6)))
    out = f"{PREFIX}-{n + 1}.png"
    sh.save(out)
    print(f"  sheet {out}: the median cell by counted stars ({cnt} stars of 20 sigma and over)")
