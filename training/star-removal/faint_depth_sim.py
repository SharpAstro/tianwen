"""How deep would the faint stars have to be drawn for the drawn light to carry the grain a crowded cell holds?

    python -I faint_depth_sim.py <plate stars.csv> [--follow <px>[,<px>...]] <cell i,j> [<cell i,j>...]

--follow asks whether clustering makes up what an even scatter lacks: for each width given, the SAME stars on the same
noise are placed a second time, not evenly but where the catalogue's own stars are dense (their count map over the cell
and its margin, smoothed by a Gaussian of that many pixels), and the band is printed beside the even one.

For each 300 px cell (band.py's grid: i down, j across): the catalogue's stars in the cell, the power law
dN/dlnS ~ S^-alpha fitted over [10, 300) sigma on the whole frame (LuminosityFunction's law and range) and scaled to the
cell's own count there, and then a simulated cell of unit pixel noise with the law's stars drawn from a floor up to the
builder's detection limit of 5 sigma (the stars a stored plate still holds), each a Gaussian of the catalogue's median
width whose significance is the catalogue's own measure. Prints band.py's statistic (through a 7 px median, the 9 px box
mean less the 41 px one, rms in sigma of a 9 px mean) for noise alone and for each floor, three draws each, so the
floor at which the drawn stars carry the band the real plate holds can be read off. A simulation of the law, not a
measurement of the sky: it says what the law would give if it held down to the floor.
"""
import csv
import sys

import numpy as np
from scipy.ndimage import gaussian_filter, median_filter, uniform_filter
from scipy.optimize import brentq

B, PAD, DETECT = 300, 48, 5.0
FLOORS = (5.0, 3.0, 2.0, 1.0, 0.5, 0.25, 0.1)


def band(a):
    p7 = median_filter(a, 7)
    b = (uniform_filter(p7, 9) - uniform_filter(p7, 41))[PAD:-PAD, PAD:-PAD]
    sig = 1.4826 * np.median(np.abs(np.diff(a[PAD:-PAD, PAD:-PAD], axis=1))) / np.sqrt(2)
    return float(np.sqrt(np.mean(b ** 2)) / (sig / 9))


def main():
    cells = sys.argv[2:]
    follow = []
    if "--follow" in cells:
        at = cells.index("--follow")
        follow = [float(v) for v in cells[at + 1].split(",")]
        del cells[at:at + 2]
    rows = list(csv.DictReader(open(sys.argv[1], encoding="utf-8")))
    sig = np.array([float(r["significance"]) for r in rows])
    amp = np.array([float(r["amplitude"]) for r in rows])
    noise = np.array([float(r["sigma"]) for r in rows])
    width = np.array([float(r["width"]) for r in rows])
    x = np.array([float(r["x"]) for r in rows])
    y = np.array([float(r["y"]) for r in rows])
    plain = np.array([r["saturated"] != "1" and r["outcome"] == "Subtracted" for r in rows]) & np.isfinite(amp) & (noise > 0)
    per = (amp[plain] / noise[plain]) / sig[plain]
    peak_per_significance = float(np.median(per))
    w = float(np.median(width[plain]))
    fit = (sig >= 10) & (sig < 300)
    t, big = np.log(sig[fit] / 10), np.log(30.0)
    alpha = brentq(lambda a: 1 / a - big * np.exp(-a * big) / (1 - np.exp(-a * big)) - t.mean(), 0.05, 5)
    print(f"{len(rows)} catalogued stars, the faintest {sig.min():.1f} sigma; alpha {alpha:.3f} over [10, 300) from {fit.sum()} stars")
    print(f"a star's peak over the pixel noise is {peak_per_significance:.2f} times its significance at the median "
          f"(10th to 90th percentile {np.percentile(per, 10):.2f} to {np.percentile(per, 90):.2f}); median width {w:.2f} px")
    side = B + 2 * PAD
    yy, xx = np.mgrid[0:side, 0:side]
    def drawn(noise_image, peaks, px, py):
        img = noise_image.copy()
        r = int(np.ceil(4 * w))
        for a, cx, cy in zip(peaks, px, py):
            x0, y0 = int(round(cx)), int(round(cy))
            xs, ys = slice(max(0, x0 - r), min(side, x0 + r + 1)), slice(max(0, y0 - r), min(side, y0 + r + 1))
            img[ys, xs] += a * np.exp(-((xx[ys, xs] - cx) ** 2 + (yy[ys, xs] - cy) ** 2) / (2 * w * w))
        return img

    for spec in cells:
        i, j = (int(v) for v in spec.split(","))
        inside = (y >= i * B) & (y < i * B + B) & (x >= j * B) & (x < j * B + B)
        # Where the catalogue's stars stand in the cell and its margin: the map the followed draws are placed by.
        top, left = i * B - PAD, j * B - PAD
        near = (y >= top) & (y < top + side) & (x >= left) & (x < left + side)
        count = np.zeros((side, side), np.float64)
        np.add.at(count, ((y[near] - top).astype(int), (x[near] - left).astype(int)), 1)
        places = {}
        for width_px in follow if near.any() else []:
            p = np.maximum(gaussian_filter(count, width_px), 0).ravel()
            places[width_px] = p / p.sum()
        # Its own stream, so the even draws read as they did before --follow existed.
        placer = np.random.default_rng(12)
        n_fit = int((inside & fit).sum())
        k = n_fit * alpha / (10 ** -alpha - 300 ** -alpha)
        expect = lambda lo, hi: k * (lo ** -alpha - hi ** -alpha) / alpha
        print(f"cell {i},{j}: {int(inside.sum())} catalogued stars, {n_fit} in the fit's range, {int((inside & (sig < 10)).sum())} under 10 sigma "
              f"(the law expects {expect(DETECT, 10):.0f} from 5 to 10)")
        rng = np.random.default_rng(11)
        base = [band(rng.standard_normal((side, side)).astype(np.float32)) for _ in range(3)]
        print(f"  noise alone: band {np.mean(base):.2f}")
        scale = (side * side) / (B * B)
        for floor in FLOORS:
            if floor >= DETECT:
                continue
            out, counts, light = [], [], []
            followed = {width_px: [] for width_px in places}
            for _ in range(3):
                n = rng.poisson(expect(floor, DETECT) * scale)
                # ln S distributed as exp(-alpha ln S) between the floor and the detection limit
                u = rng.random(n)
                s = (floor ** -alpha - u * (floor ** -alpha - DETECT ** -alpha)) ** (-1 / alpha)
                noise_image = rng.standard_normal((side, side)).astype(np.float32)
                px, py = rng.random(n) * side, rng.random(n) * side
                peaks = s * peak_per_significance
                out.append(band(drawn(noise_image, peaks, px, py))); counts.append(n / scale)
                light.append(float(np.sum(peaks) * 2 * np.pi * w * w / (side * side)))
                for width_px, p in places.items():
                    fy, fx = np.divmod(placer.choice(p.size, size=n, p=p), side)
                    followed[width_px].append(band(drawn(noise_image, peaks, fx + placer.random(n), fy + placer.random(n))))
            print(f"  stars from {floor:g} to {DETECT:g} sigma: {np.mean(counts):8.0f} a cell, mean light {np.mean(light):.2f} sigma a pixel, "
                  f"band {np.mean(out):.2f} ({min(out):.2f} to {max(out):.2f})")
            if followed:
                print("    the same stars placed where the catalogue's are dense: "
                      + "; ".join(f"at {width_px:g} px band {np.mean(v):.2f} ({min(v):.2f} to {max(v):.2f})" for width_px, v in followed.items()))


main()
