"""The three-frame matched-noise measurement (H8's fourth precondition).

Two frames of one sky cannot say what a night's deviation is MADE of. Their difference drops
everything the two share and keeps everything they do not, so a per-night calibration residual and a
per-night photon realisation arrive added together and there is no second equation. Three frames give
one:

    A = S + a,  B = S + b,  C = S + c      (a, b, c independent between nights)
    cov(A - B, A - C) = var(a)

because every term carrying b or c drops. So each night's own deviation from the common scene is
readable on its own, without ever seeing the scene.

What that buys for H8: a cross-night N2N target is night B, and what it hands the model is var(b) --
photon noise (which is what N2N is for) plus a calibration residual and a scene disagreement (which
are not). Splitting var(a) into a pixel-scale part and a structured part therefore bounds what a
cross-night pair can ever buy. The split is by spatial scale: photon noise in an integrated master is
correlated only over the resampling kernel, a calibration residual and a registration error are
smooth over many pixels. `--highpass` subtracts a Gaussian of that sigma and the white attenuation it
implies is MEASURED on a synthetic white field rather than assumed, because a Gaussian high-pass keeps
somewhat less than all of white variance.

Two things this cannot see, both worth stating before a number is read off it.

A component shared by ALL THREE nights -- the same master dark and flat calibrated every one of them --
cancels in every difference, so var(a) is the night-SPECIFIC deviation and nothing else. That is the
right quantity here (it is what a cross-night target adds and a same-session one does not), but it is
not "the calibration residual", which may be larger and common.

And the scale split is a split by SCALE, not by cause: a fixed-pattern residual is pixel-scale and
lands in the same bin as photon noise, so the structured share below is a LOWER bound on the part of a
cross-night target that N2N is not unbiased against.

Reads a triple export (`tianwen dataset pair --pair "A::B::C"`), whose three nights sit on one grid in
one set of units with one MTF, which is the only reason these covariances mean anything.

Usage:
    python n2n_threeframe.py --root D:/Astro-Dataset/pairs-triple [--highpass 3.0] [--json out.json]
"""

import argparse
import json
import os
from collections import defaultdict

import numpy as np
from scipy.ndimage import gaussian_filter, uniform_filter

TILE = 256
CH = 3
BYTES = CH * TILE * TILE * 2

FRAME_A = "halfmaster_a"
FRAME_B = "halfmaster_b"
FRAME_C = "nightc"
FRAME_MEAN = "master"


def read_tile(root, rel):
    with open(os.path.join(root, rel.replace("/", os.sep)), "rb") as fh:
        raw = fh.read()
    if len(raw) != BYTES:
        raise SystemExit(f"tile {rel} is {len(raw)} bytes, expected {BYTES}")
    return np.frombuffer(raw, "<f2").reshape(CH, TILE, TILE).astype(np.float32)


def load_cells(root):
    """(pair, cx, cy) -> {frame: relpath}, keeping only cells carrying all three nights."""
    cells = defaultdict(dict)
    with open(os.path.join(root, "tiles-manifest.jsonl"), encoding="utf-8") as fh:
        for line in fh:
            line = line.strip()
            if not line:
                continue
            d = json.loads(line)
            cells[(d["SessionId"], d["CellX"], d["CellY"])][d["Frame"]] = d["Tile"]
    keep = {k: v for k, v in cells.items() if all(f in v for f in (FRAME_A, FRAME_B, FRAME_C))}
    return keep


def faint_star_free_mask(mean_tile):
    """The pair exporter's own convention: the faintest half of a BOX-MEANED scene, with the pixels
    beyond five MADs clipped so stars stay out.

    Judging faintness on the pixel itself would condition on the three nights' noise summing low,
    which anticorrelates them (measured at -0.23 on independent synthetic noise); a 15x15 box mean
    carries 1/225 of the centre pixel's noise and the bias goes with it.
    """
    lum = mean_tile.mean(axis=0)
    scene = uniform_filter(lum, size=15, mode="nearest")
    faint = scene <= np.median(scene)
    resid = lum - uniform_filter(lum, size=5, mode="nearest")
    mad = np.median(np.abs(resid - np.median(resid))) * 1.4826 + 1e-12
    return faint & (np.abs(resid) < 5 * mad)


def white_attenuation(sigma, trials=8, seed=0):
    """How much of a WHITE field's variance the high-pass keeps. Measured, not assumed: for a Gaussian
    of sigma s the survivor is 1 - 1/(2*sqrt(pi)*s) to first order, and the array's discretisation
    moves that by a few percent at the sigmas used here."""
    rng = np.random.default_rng(seed)
    kept = []
    for _ in range(trials):
        f = rng.standard_normal((TILE, TILE)).astype(np.float32)
        kept.append(np.var(f - gaussian_filter(f, sigma)) / np.var(f))
    return float(np.mean(kept))


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--root", required=True, help="a triple export directory (tiles-manifest.jsonl + tiles/)")
    ap.add_argument("--highpass", type=float, default=3.0,
                    help="Gaussian sigma whose residual counts as pixel-scale. 3 px keeps a stacked "
                         "master's own noise and drops a gradient")
    ap.add_argument("--json", default=None, help="write the per-cell rows here")
    args = ap.parse_args()

    cells = load_cells(args.root)
    if not cells:
        raise SystemExit(f"no cell in {args.root} carries all three nights; export with --pair 'A::B::C'")
    attenuation = white_attenuation(args.highpass)
    print(f"{len(cells)} cells with three nights; high-pass sigma {args.highpass} px keeps "
          f"{attenuation:.3f} of white variance")

    rows = []
    for (pair, cx, cy), frames in sorted(cells.items()):
        a = read_tile(args.root, frames[FRAME_A])
        b = read_tile(args.root, frames[FRAME_B])
        c = read_tile(args.root, frames[FRAME_C])
        mean = read_tile(args.root, frames[FRAME_MEAN]) if FRAME_MEAN in frames else (a + b) / 2
        mask = faint_star_free_mask(mean)
        if mask.sum() < 2000:
            continue
        for ch in range(CH):
            pa, pb, pc = a[ch], b[ch], c[ch]
            dab, dac, dbc = pa - pb, pa - pc, pb - pc
            m = mask
            v_a = float(np.cov(dab[m], dac[m])[0, 1])
            v_b = float(np.cov((-dab)[m], dbc[m])[0, 1])
            v_c = float(np.cov((-dac)[m], (-dbc)[m])[0, 1])

            def hp(p):
                return p - gaussian_filter(p, args.highpass)

            hab, hac, hbc = hp(pa) - hp(pb), hp(pa) - hp(pc), hp(pb) - hp(pc)
            h_a = float(np.cov(hab[m], hac[m])[0, 1])
            h_b = float(np.cov((-hab)[m], hbc[m])[0, 1])
            h_c = float(np.cov((-hac)[m], (-hbc)[m])[0, 1])
            rows.append({
                "pair": pair, "cx": cx, "cy": cy, "channel": ch,
                "v_a": v_a, "v_b": v_b, "v_c": v_c,
                "hp_a": h_a, "hp_b": h_b, "hp_c": h_c,
                "px": int(m.sum()),
            })

    if not rows:
        raise SystemExit("every cell was masked out; loosen the mask or check the export")

    print()
    print("Per NIGHT, pooled over cells and channels. 'own deviation' is var(a) from the three-frame")
    print("covariance, the NIGHT-SPECIFIC part (anything the three share, a common master dark among")
    print("them, cancels); 'pixel-scale' is what survives the high-pass, corrected for its attenuation")
    print("of white noise; 'structured' is the rest -- the smooth scene and sky disagreement a")
    print("cross-night target hands the model along with the photon noise. A fixed-pattern residual is")
    print("pixel-scale too, so the structured share is a LOWER bound on the non-photon part.")
    print()
    print(f"{'night':>6} {'own deviation (sigma)':>22} {'pixel-scale':>12} {'structured':>11} {'structured share':>17}")
    summary = {}
    for key, label in (("a", "A"), ("b", "B"), ("c", "C")):
        v = np.median([r[f"v_{key}"] for r in rows])
        h = np.median([r[f"hp_{key}"] for r in rows]) / attenuation
        structured = max(v - h, 0.0)
        share = structured / v if v > 0 else float("nan")
        summary[label] = {"variance": float(v), "pixel_scale": float(h), "structured": float(structured),
                          "structured_share": float(share)}
        print(f"{label:>6} {np.sqrt(max(v, 0)):>22.6f} {np.sqrt(max(h, 0)):>12.6f} "
              f"{np.sqrt(structured):>11.6f} {share:>16.1%}")

    shares = [summary[k]["structured_share"] for k in summary]
    print()
    print(f"structured share across the three nights: {min(shares):.1%} to {max(shares):.1%}")
    print("Read it as the fraction of what a cross-night TARGET adds that is not photon noise. N2N is")
    print("unbiased against the photon part and against nothing else, so this is the tax the regime")
    print("pays for its independence, before any question of what it buys.")

    if args.json:
        with open(args.json, "w", encoding="utf-8") as fh:
            json.dump({"attenuation": attenuation, "summary": summary, "rows": rows}, fh, indent=1)
        print(f"\nwrote {args.json}")


if __name__ == "__main__":
    main()
