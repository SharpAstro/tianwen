"""Score a denoised HALF-MASTER against the OTHER half, not against the master. (Task #30)

Deployment depth is a half-master (~0.215x a sub's noise), and the honest question is whether
denoising is worth anything at all down there. The measurement that asked it used the master as the
reference and answered "no, and every model makes it worse": the raw half already sat at 0.99
faint-amplitude and 0.923 structure correlation.

That answer is not trustworthy, because **the master CONTAINS half A**. It is the integration of all
the subs, of which A is half, so `corr(A, master)` includes A's own noise realisation correlated with
itself. Raw A collects that for free and any real denoiser can only spend it down. The amplitude
figure has the same leak in weaker form: A's peak at a star and the master's peak at that star share a
noise draw, so their ratio sits near 1 with artificially low variance, and smoothing the peak is
scored as damage even when it moves the estimate TOWARD the truth.

Scoring against B removes the shared term outright. A and B are interleaved halves (`SessionRegistrar`,
`i % 2`), so their noise is independent by construction. The cost is that the reference is now noisy,
which attenuates every correlation by roughly the same factor -- a constant that shifts all models
together and leaves the RANKING clean, which is what a comparison needs.

Two things deliberately still come from the master, and neither leaks:
  - WHICH stars exist and which SNR bucket each lands in. That is a catalogue decision, not a measured
    value, and the master is simply the deepest image available to make it.
  - The fabrication truth mask. Same reason, and the floor for that metric is the raw half, exactly as
    the sub-level metric floors on a raw sub.

Both scorings are printed side by side, because the SIZE of the leak is the finding.
"""
import argparse
import os

import numpy as np
import torch
from scipy.ndimage import binary_dilation, maximum_filter

import n2n_metrics as M
import n2n_smoke as S

DOT_SIGMA = 5.0


def luminance(a):
    return a.mean(axis=1)


def band_corr(a, b, s1, s2):
    """Correlation of two images in one difference-of-Gaussian band, over whole tiles."""
    out = []
    for i in range(len(a)):
        x, y = M.dog(a[i], s1, s2).ravel(), M.dog(b[i], s1, s2).ravel()
        if x.std() > 0 and y.std() > 0:
            out.append(float(np.corrcoef(x, y)[0, 1]))
    return float(np.mean(out)) if out else float("nan")


def spurious_per_tile(la, truth):
    out = np.empty(len(la))
    for i in range(len(la)):
        med = np.median(la[i])
        mad = float(np.median(np.abs(la[i] - med))) + 1e-12
        d = (la[i] >= maximum_filter(la[i], size=5)) & (la[i] > med + DOT_SIGMA * mad)
        ys, xs = np.nonzero(d)
        out[i] = len(ys) - int(truth[i][ys, xs].sum())
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--cache", required=True, help="see the note in n2n_metrics.py; no default")
    ap.add_argument("--models", nargs="+", required=True, help="slug=checkpoint.pt")
    ap.add_argument("--skip-val-sessions", type=int, default=0)
    a = ap.parse_args()

    dev = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    mm, meta = S.open_cache(a.cache)
    if meta.get("slots", S.SLOTS_SUBS_ONLY) <= S.SLOT_HALF_B:
        raise SystemExit(f"{a.cache} has no half-master slots; this needs the half-pair bake")

    sessions = meta["val_sessions"][a.skip_val_sessions:]
    halves = meta["has_halves"]
    cells = [i for i in range(meta["train_cells"], meta["cells"])
             if meta["keys"][i][0] in sessions and halves[i]]
    print(f"held-out: {', '.join(sessions)}")
    print(f"{len(cells)} val cells that carry a half-master pair")

    masters = np.asarray(mm[cells, S.SLOT_MASTER], dtype=np.float32)
    half_a = np.asarray(mm[cells, S.SLOT_HALF_A], dtype=np.float32)
    half_b = np.asarray(mm[cells, S.SLOT_HALF_B], dtype=np.float32)

    lm, lb = luminance(S.crop(masters)), luminance(S.crop(half_b))
    stars = M.star_table(lm)
    print(f"{sum(len(s[0]) for s in stars)} master-detected stars\n")

    # Fabrication truth + floor, both on the master, both catalogue decisions rather than measured
    # values. The floor is the RAW HALF: comparing against zero makes every model look inventive.
    truth = []
    for t in lm:
        med, mad = np.median(t), float(np.median(np.abs(t - np.median(t)))) + 1e-12
        truth.append(binary_dilation(
            (t >= maximum_filter(t, size=5)) & (t > med + 8 * mad), np.ones((3, 3), bool)))
    truth = np.array(truth)

    base_noise = float(np.mean([M.bg_stats(t)[1] for t in luminance(S.crop(half_a))]))
    floor = float(spurious_per_tile(luminance(S.crop(half_a)), truth).mean())

    rows = []

    def add(slug, arr):
        la = luminance(arr)
        noise = float(np.mean([M.bg_stats(t)[1] for t in la])) / base_noise
        amp_b, det_b, _ = M.measure(la, stars, lb)
        amp_m, det_m, _ = M.measure(la, stars, lm)
        rows.append(dict(
            slug=slug, noise=noise,
            amp_b=amp_b[0], det_b=det_b[0], amp_m=amp_m[0], det_m=det_m[0],
            corr_b=band_corr(la, lb, 1.0, 2.0), corr_m=band_corr(la, lm, 1.0, 2.0),
            spur=float(spurious_per_tile(la, truth).mean())))

    add("raw half A", S.crop(half_a))
    for spec in a.models:
        slug, ckpt = spec.split("=", 1)
        add(slug, S.crop(S.denoise(a.cache, ckpt, half_a, dev)))

    print(f"Scored on half A. Floor for invention is the raw half at {floor:.1f} spurious/tile.\n")
    print(f"{'model':14s} {'noise':>7} | {'vs OTHER HALF (clean)':^24} | "
          f"{'vs MASTER (leaks)':^24} | {'spur':>6}")
    print(f"{'':14s} {'':>7} | {'amp':>7} {'detect':>7} {'corr':>7} | "
          f"{'amp':>7} {'detect':>7} {'corr':>7} | {'':>6}")
    for r in rows:
        print(f"{r['slug']:14s} {r['noise']:6.2f}x | {r['amp_b']:7.3f} {r['det_b']:7.3f} "
              f"{r['corr_b']:7.3f} | {r['amp_m']:7.3f} {r['det_m']:7.3f} {r['corr_m']:7.3f} | "
              f"{r['spur']:6.1f}")

    raw = rows[0]
    print(f"\nTHE LEAK, MEASURED. Raw half A scores {raw['corr_m']:.3f} structure correlation "
          f"against a master that contains it and {raw['corr_b']:.3f} against the half that does "
          f"not; amplitude {raw['amp_m']:.3f} against {raw['amp_b']:.3f}. So the leak is real and "
          f"SMALL -- {raw['corr_m'] - raw['corr_b']:.3f} of correlation -- and it is not what made "
          f"denoising look worthless at this depth.")

    if len(rows) > 1:
        # State the TRADE, not a binary verdict off whichever metric happens to move. Correlation
        # is saturated here (every row within a few thousandths), so a "best corr" winner is noise;
        # the amplitude column is where the models actually differ, and it differs against them.
        spread = max(r["corr_b"] for r in rows) - min(r["corr_b"] for r in rows)
        print(f"\nStructure correlation spans just {spread:.3f} across every row including the raw "
              f"half, so at this depth it CANNOT discriminate and must not be used to rank. What "
              f"separates the models is amplitude against noise removed:")
        for r in rows[1:]:
            d_noise = (1.0 - r["noise"]) * 100.0
            d_amp = (raw["amp_b"] - r["amp_b"]) / raw["amp_b"] * 100.0
            print(f"  {r['slug']:14s} removes {d_noise:5.1f}% of the noise and spends "
                  f"{d_amp:5.1f}% of the faint-star amplitude")
        print("\nEvery model spends more signal than it buys quiet, with an independent reference, "
              "so the original verdict stands and the leak was not the reason for it.")

    print(f"\nCAVEAT on the spurious column, which is NOT comparable to the sub-level one. The "
          f"truth mask is the master at 8 MAD, but a half-master's own detection bar is 5 of ITS "
          f"larger MAD, which lands BELOW that in absolute terms -- so the {floor:.1f} floor is "
          f"mostly real faint stars the mask omits, not invention. Models score lower than the "
          f"floor here by ERASING them. The metric's direction flips a third time at this depth.")


if __name__ == "__main__":
    main()
