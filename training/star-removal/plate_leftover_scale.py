"""Why the builder's leftover count moves between two plate stores: the sources, or the noise they are counted in?
(2026-11-23, #2449.)

    python -I plate_leftover_scale.py <stored root> <rebuilt root> <out.tsv> [processes]

The builder's finder divides its filtered plane by a noise that, in a crowded field, is capped by what the plate's own
pixel differences predict (`PointSourceFinder.PredictedFilteredNoise`): white noise through a Gaussian of width s, with
s and the pixel sigma fixed by the spreads of the lag-1 and lag-2 differences. A dug core is a dark pixel beside a
bright one, so it widens those differences; refilling it narrows them, and every source's significance rises by the
same factor. Here the differences are read along rows and columns, where the builder reads every second row.

Per master: that predicted noise, stored and rebuilt, and the sources one plain finder counts on each plate (the
luminance smoothed by the PSF, less the same smoothed six times wider, local maxima) over 5 times the stored plate's
capped noise (one fixed level for both) and, on the rebuilt plate, over 5 times its own. The builder's own 5 to 10
sigma leftover counts from both report lines ride along. The plain finder is not the builder's: it says whether the
plates hold more point sources, not what the builder would count.
"""
import json
import os
import sys
from multiprocessing import Pool
import numpy as np
from astropy.io import fits
from scipy.ndimage import gaussian_filter, maximum_filter


def lum_of(path):
    data = fits.getdata(path).astype(np.float32)
    return data.mean(axis=0) if data.ndim == 3 else data


def spread(lum, present, lag):
    out = []
    for d, ok in ((lum[:, lag:] - lum[:, :-lag], present[:, lag:] & present[:, :-lag]),
                  (lum[lag:, :] - lum[:-lag, :], present[lag:, :] & present[:-lag, :])):
        v = d[ok]
        out.append(1.4826 * np.median(np.abs(v - np.median(v))))
    return float(np.mean(out))


def filtered(lum, f):
    present = np.isfinite(lum) & (lum != 0)
    d1, d2 = spread(lum, present, 1), spread(lum, present, 2)
    ratio = (d2 / d1) ** 2
    a = 0.0
    if ratio > 1:
        roots = np.roots([1, 1, 1, 1 - ratio])
        real = [r.real for r in roots if abs(r.imag) < 1e-9 and 0 < r.real < 1]
        a = float(real[0]) if real else 0.0
    sigma = d1 / np.sqrt(2 * (1 - a))
    s = np.sqrt(-1 / (4 * np.log(a))) if a > 0 else 0.0
    pred = sigma * s / np.sqrt(f * f + s * s) if s > 0 else sigma
    filled = np.where(present, lum, np.float32(np.nanmedian(lum[present])))
    g = gaussian_filter(filled, f) - gaussian_filter(filled, 6 * f)
    peaks = g[(g == maximum_filter(g, size=5)) & present]
    return d1, pred, peaks


def one(job):
    name, fwhm, old_path, new_path = job
    f = fwhm / 2.355
    d1o, po, peaks_o = filtered(lum_of(old_path), f)
    d1n, pn, peaks_n = filtered(lum_of(new_path), f)
    level = 5 * 1.15 * po
    return (name, d1o, d1n, po, pn, int((peaks_o > level).sum()), int((peaks_n > level).sum()),
            int((peaks_n > level * pn / po).sum()))


if __name__ == "__main__":
    old_root, new_root, out = sys.argv[1:4]
    procs = int(sys.argv[4]) if len(sys.argv) > 4 else 4

    def load(root):
        with open(os.path.join(root, "stats", "starless-plates.jsonl"), encoding="utf-8") as fh:
            return {r["Master"]: r for r in map(json.loads, fh) if r.get("Statistics")}

    old, new = load(old_root), load(new_root)
    jobs = []
    for name, r in new.items():
        stem = name[:-5] if name.endswith(".fits") else name
        a = os.path.join(old_root, "plates", stem + "_plate.fits")
        b = os.path.join(new_root, "plates", stem + "_plate.fits")
        if name in old and os.path.exists(a) and os.path.exists(b):
            jobs.append((name, float(r["Statistics"]["FwhmPx"][-1]), a, b))
    print(f"{len(jobs)} masters", flush=True)
    rows = []
    with Pool(procs) as pool, open(out, "w", encoding="utf-8", newline="\n") as fh:
        fh.write("master\tlag1_stored\tlag1_rebuilt\tpredicted_stored\tpredicted_rebuilt\tsources_stored\tsources_rebuilt_same_level\t"
                 "sources_rebuilt_own_level\tbuilder_leftover_5_10_stored\tbuilder_leftover_5_10_rebuilt\tdug_cores\tsubtracted\n")
        for k, row in enumerate(pool.imap_unordered(one, jobs), 1):
            name = row[0]
            bo = old[name]["Statistics"]["Bands"][0]["Leftover"]
            bn = new[name]["Statistics"]["Bands"][0]["Leftover"]
            full = row + (bo, bn, new[name]["Statistics"].get("DugCores", 0), new[name]["Subtracted"])
            rows.append(full)
            fh.write("\t".join(str(v) for v in full) + "\n")
            fh.flush()
            if k % 10 == 0:
                print(f"{k} of {len(jobs)}", flush=True)
    r = np.array([row[1:] for row in rows], dtype=float)
    scale = r[:, 3] / r[:, 2]
    same = r[:, 5] / r[:, 4] - 1
    own = r[:, 6] / r[:, 4] - 1
    builder = r[:, 8] / np.maximum(r[:, 7], 1) - 1
    q = lambda v: " / ".join(f"{x * 100:+.1f}" for x in np.percentile(v, [50, 10, 90]))
    print(f"predicted noise, rebuilt over stored, less one, % (median / 10th / 90th): {q(scale - 1)}")
    print(f"sources at one fixed level, rebuilt against stored, %: {q(same)}; masters with more on the rebuilt plate: {(same > 0).sum()} of {len(rows)}, "
          f"with over 1 % more: {(same > 0.01).sum()}")
    print(f"sources at each plate's own level, %: {q(own)}")
    print(f"the builder's 5 to 10 sigma leftover count, %: {q(builder)}")
    print(f"totals: stored {int(r[:, 4].sum()):,}, rebuilt at the same level {int(r[:, 5].sum()):,} ({(r[:, 5].sum() / r[:, 4].sum() - 1) * 100:+.2f} %), "
          f"rebuilt at its own level {int(r[:, 6].sum()):,} ({(r[:, 6].sum() / r[:, 4].sum() - 1) * 100:+.2f} %); "
          f"builder's count {int(r[:, 7].sum()):,} -> {int(r[:, 8].sum()):,} ({(r[:, 8].sum() / r[:, 7].sum() - 1) * 100:+.2f} %)")
    c = np.corrcoef(1 / scale - 1, builder)[0, 1]
    print(f"correlation over masters of the builder's count change with the rise in significance (stored over rebuilt predicted noise): {c:.2f}")
    worst = np.argsort(-same)[:6]
    print("masters with the most added sources at the fixed level:")
    for i in worst:
        print(f"  {same[i] * 100:+.1f} % ({int(r[i, 4]):,} -> {int(r[i, 5]):,}), predicted noise {(scale[i] - 1) * 100:+.1f} %, builder {builder[i] * 100:+.1f} %  {rows[i][0][:80]}")
