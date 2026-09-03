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
    ap.add_argument("--blend", default="", help="comma alphas, e.g. 0.25,0.5,0.75,1 -- see the note on matched strength")
    ap.add_argument("--match", default="5,10,20", help="noise-removal percentages to compare the arms AT")
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
        # LEVEL BIAS, per channel, in units of the raw half's noise. A denoiser is supposed to change
        # the noise and leave the level alone, and the 1:1 comparison showed both N2N arms pulling a
        # green sky toward neutral while the synthetic arm kept it -- a colour change, which no
        # noise-or-amplitude column can see. Per CHANNEL because a shift shared by all three is a
        # pedestal the stretch hides, while a differential one is a visible cast.
        bias = (np.median(arr, axis=(2, 3)).mean(axis=0) - raw_med) / base_noise
        noise = float(np.mean([M.bg_stats(t)[1] for t in la])) / base_noise
        amp_b, det_b, _ = M.measure(la, stars, lb)
        amp_m, det_m, _ = M.measure(la, stars, lm)
        rows.append(dict(
            slug=slug, noise=noise,
            amp_b=amp_b[0], det_b=det_b[0], amp_m=amp_m[0], det_m=det_m[0],
            corr_b=band_corr(la, lb, 1.0, 2.0), corr_m=band_corr(la, lm, 1.0, 2.0),
            spur=float(spurious_per_tile(la, truth).mean()), bias=bias))

    raw_a = S.crop(half_a)
    raw_med = np.median(raw_a, axis=(2, 3)).mean(axis=0)
    add("raw half A", raw_a)
    raw_amp = rows[0]["amp_b"]

    def trade(arr):
        """(noise removed %, faint amplitude spent %) for one output, both against the raw half."""
        la = luminance(arr)
        noise = float(np.mean([M.bg_stats(t)[1] for t in la])) / base_noise
        amp = M.measure(la, stars, lb)[0][0]
        return (1.0 - noise) * 100.0, (raw_amp - amp) / raw_amp * 100.0

    # MATCHED STRENGTH. The exchange rate is not strength-invariant: within both N2N families it
    # IMPROVES the harder the model denoises (control 1.93 -> 1.57, v19d 1.67 -> 0.90 as removal goes
    # 22 -> 35% and 18 -> 26%). So an arm that removes 6% of the noise and one that removes 35% cannot
    # be ranked by their rates, and comparing them that way flatters whichever sits on the cheaper part
    # of its own curve. Blending an output back toward its input walks each model DOWN its own curve
    # (out = raw + alpha*(out - raw), which is verbatim the shipped runner's user-facing strength),
    # so every model has a value at the same noise removed and the comparison is the one the
    # hypothesis asked for. One difference from the shipped dial, stated rather than hidden: the
    # runner blends in LINEAR after unstretching and this blends in the stretched domain the tiles
    # are stored in. MtfUnstretch is monotone but not affine, so the two are not identical -- it
    # cannot reorder the models, since every arm is blended the same way, but a number here is a
    # trade curve and not a promise about a specific slider position.
    alphas = [float(x) for x in a.blend.split(",") if x.strip()]
    curves = {}
    for spec in a.models:
        slug, ckpt = spec.split("=", 1)
        arr = S.crop(S.denoise(a.cache, ckpt, half_a, dev))
        add(slug, arr)
        if alphas:
            curves[slug] = [(0.0, 0.0)] + [trade(raw_a + al * (arr - raw_a)) for al in alphas]

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

    if len(rows) > 1 and len(rows[0]["bias"]) == 3:
        print("\nLEVEL BIAS, per channel, in units of the raw half's background noise. A denoiser owes "
              "the level nothing: it should change the noise and leave the sky where it was. CAST is "
              "the differential (max minus min across R, G and B), which is the part a stretch cannot "
              "hide and the part the eye sees as a colour change.")
        print(f"{'model':14s} {'dR':>7} {'dG':>7} {'dB':>7} {'cast':>7}")
        for r in rows[1:]:
            b = r["bias"]
            print(f"{r['slug']:14s} {b[0]:7.2f} {b[1]:7.2f} {b[2]:7.2f} {b.max() - b.min():7.2f}")

    if curves:
        targets = [float(x) for x in a.match.split(",") if x.strip()]
        print(f"\nAT MATCHED NOISE REMOVED, each model blended back toward its own input over "
              f"alpha {alphas}. Lower is better: it is the faint-star amplitude spent to buy that "
              f"much quiet. A dash means the model cannot reach that removal even at alpha 1.")
        print(f"{'model':14s} " + " ".join(f"{t:6.0f}%" for t in targets) + "    max removed")
        for slug, pts in curves.items():
            pts = sorted(pts)
            cells_out = []
            for t in targets:
                hit = "     -"
                for (r0, s0), (r1, s1) in zip(pts, pts[1:]):
                    if r0 <= t <= r1 and r1 > r0:
                        hit = f"{s0 + (s1 - s0) * (t - r0) / (r1 - r0):6.1f}"
                        break
                cells_out.append(hit)
            print(f"{slug:14s} " + " ".join(cells_out) + f"    {max(r for r, _ in pts):6.1f}%")

    print(f"\nCAVEAT on the spurious column, which is NOT comparable to the sub-level one. The "
          f"truth mask is the master at 8 MAD, but a half-master's own detection bar is 5 of ITS "
          f"larger MAD, which lands BELOW that in absolute terms -- so the {floor:.1f} floor is "
          f"mostly real faint stars the mask omits, not invention. Models score lower than the "
          f"floor here by ERASING them. The metric's direction flips a third time at this depth.")


if __name__ == "__main__":
    main()
