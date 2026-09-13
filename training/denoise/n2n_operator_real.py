"""The operator on a REAL seeing split: E2.10b's Statue pair, read the way the gate reads.

Every E3 number so far is on the synthetic cache (a master blurred by a drawn kernel). The plan named
E2.10a/b's real-blur pair as the check on the estimator's kernel before a seed is spent, and the C#
oracle took it in linear (`SeeingSplitPairProbe`: est-c rec/A 1.007 / 1.085 / 1.139 per channel at
B/A 1.06 / 1.21 / 1.33). This runs the torch operator, with and without a trained prior, on the
soft master's crop and reads it against the sharp master with the gate's own statistic (stretched
luminance, `n2n_deconv_gate.star_fwhm` on the truth's 12 MAD stars, stars kept, ring excess over
the input's null), so the real-frame reading is in the same units as every seed's row.

The soft master's own full-frame stretch parameters (unit divisor, per-channel minimum and balance)
are used for both masters, which is the exporter's rule for a pair and what inference does to a
frame it is handed. The kernel per channel is the pair probe's estimate (the difference width by
Moffat composition on each crop's own stars), at the synthetic convention's beta of 4; the operator
takes one kernel per tile, so each channel is deconvolved in its own pass and its plane kept.
Alignment is through the CANVASX0/CANVASY0 cards, both masters being on the same reference frame.

    python n2n_operator_real.py --cache C:/temp/tianwen-scratch/n2n-p2-blur-clamped --checkpoint e31_s0.pt
"""
import argparse
import os
import time

import numpy as np
import torch
from astropy.io import fits

import n2n_deconv_gate as DG
import n2n_metrics as M
import n2n_operator as OP
import n2n_smoke as S


def read_master(path):
    with fits.open(path, memmap=False) as h:
        hdu = next(x for x in h if x.data is not None)
        data = np.asarray(hdu.data, dtype=np.float32)
        return data, int(hdu.header["CANVASX0"]), int(hdu.header["CANVASY0"])


def stretch(unit_crop, mins, betas):
    out = np.empty_like(unit_crop)
    for c in range(unit_crop.shape[0]):
        shifted = np.clip(unit_crop[c] - mins[c], 0.0, None).astype(np.float64)
        out[c] = OP.mtf(betas[c], shifted).astype(np.float32)
    return out


def gate_read(truth_lum, input_lum, output_lum):
    import n2n_star_shape as SH
    med = float(np.median(truth_lum))
    _, mad = M.bg_stats(truth_lum)
    ys, xs = DG.detect(truth_lum, med, mad)
    truth_w = DG.star_fwhm(truth_lum, ys, xs, med)
    n_truth = len(ys)
    ring_null = DG.ring_excess(input_lum, ys, xs, truth_w, med, mad)

    def row(lum):
        w = DG.star_fwhm(lum, ys, xs, med)
        oy, _ = DG.detect(lum, med, mad)
        e = DG.ring_excess(lum, ys, xs, truth_w, med, mad)
        # The skirt ratio (n2n_star_shape): the profile at 1 to 1.5 truth-FWHM against the truth's.
        # On the synthetic cache every prior reads about 1.0 here, since the cache's truth IS the
        # pool's own star profile; against a real sharp half it is where the block shows.
        sk = SH.skirt_ratio(lum, truth_lum, ys, xs, truth_w, float(np.median(lum)), med)
        return w / truth_w, len(oy) / n_truth, e - ring_null, sk

    return truth_w, n_truth, row(input_lum), row(output_lum) if output_lum is not None else None, row


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--cache", required=True)
    p.add_argument("--checkpoint", default="e31_s0.pt")
    p.add_argument("--sharp", default="C:/temp/e2/e210b-statue/sharp/master_StatueofLibertyNebula_light_60s_-5C_g120.fits")
    p.add_argument("--soft", default="C:/temp/e2/e210b-statue/soft/master_StatueofLibertyNebula_light_60s_-5C_g120.fits")
    p.add_argument("--crop", default="0,1024,1024", help="x,y,size in SHARP-master pixels (the pair probe's channel-1 crop)")
    p.add_argument("--kernels", default="0.77,0.91,0.98", help="per-channel kernel FWHM px (the pair probe's est-c)")
    p.add_argument("--beta", type=float, default=4.0)
    p.add_argument("--rl-k", type=int, default=20)
    p.add_argument("--device", default="cpu", help="cpu by default: the GPU belongs to the training run")
    p.add_argument("--zoom", type=float, default=1.0,
                   help="resample both crops by this factor (bicubic) and scale the kernels with it, so the same "
                        "field is read with its stars at a different pixel width: the test of whether a prior "
                        "trained on 2.1 to 2.6 px truths treats a 1.8 px truth as noise")
    p.add_argument("--noise-sigma", type=float, default=0.0,
                   help="Gaussian noise added to the INPUT after the stretch, in stretched units, seeded. With "
                        "--zoom it puts back the per-pixel noise the resampling smoothed away, so the star width "
                        "and the noise can be moved one at a time (E3.2's second coordinate)")
    p.add_argument("--roundtrip", action="store_true",
                   help="with --zoom: bring each arm's output back to the native crop (bicubic, the exact inverse "
                        "factor) and read it against the UNZOOMED truth, i.e. the runtime path of deconvolving a "
                        "frame at a scale inside the prior's width band")
    args = p.parse_args()
    if args.roundtrip and args.zoom == 1.0:
        raise SystemExit("--roundtrip needs --zoom")

    dev = torch.device(args.device)
    sharp, sx0, sy0 = read_master(args.sharp)
    soft, fx0, fy0 = read_master(args.soft)
    cx, cy, size = (int(v) for v in args.crop.split(","))
    dx, dy = sx0 - fx0, sy0 - fy0          # sharp pixel -> soft pixel
    sharp_crop = sharp[:, cy:cy + size, cx:cx + size]
    soft_crop = soft[:, cy + dy:cy + dy + size, cx + dx:cx + dx + size]
    if sharp_crop.shape != soft_crop.shape or sharp_crop.shape[1] != size:
        raise SystemExit(f"crop does not fit both masters: sharp {sharp_crop.shape}, soft {soft_crop.shape}")
    sharp_native, soft_native = sharp_crop, soft_crop
    zoom_eff, side = 1.0, size
    if args.zoom != 1.0:
        from scipy.ndimage import zoom as ndzoom
        # The zoomed side is a multiple of 16 (the prior's two poolings) and the factor is EXACTLY
        # side / size, so a round trip lands the output on the native crop pixel for pixel.
        side = int(round(size * args.zoom / 16)) * 16
        zoom_eff = side / size
        sharp_crop = np.stack([ndzoom(sharp_crop[c], zoom_eff, order=3) for c in range(3)]).astype(np.float32)
        soft_crop = np.stack([ndzoom(soft_crop[c], zoom_eff, order=3) for c in range(3)]).astype(np.float32)
        if sharp_crop.shape[1] != side or soft_crop.shape[1] != side:
            raise SystemExit(f"zoom landed on {sharp_crop.shape}, wanted {side}")
        print(f"zoom {args.zoom} -> {zoom_eff:.5f}: crops resampled to {side} px, kernels scaled by {zoom_eff:.5f}"
              + (", read at NATIVE scale after the round trip" if args.roundtrip else ""))

    # The soft master's own stretch, from its whole frame, applied to both crops.
    data_max = float(np.nanmax(soft))
    divisor = data_max if data_max > 1.0 else 1.0
    mins, betas = OP.session_stretch_params(soft, divisor)
    inv = np.float32(1.0 / divisor) if divisor != 1.0 else np.float32(1.0)
    soft_s = stretch(soft_crop * inv, mins, betas)
    sharp_s = stretch(sharp_crop * inv, mins, betas)
    if args.noise_sigma > 0.0:
        soft_s = (soft_s + np.random.default_rng(0).normal(0.0, args.noise_sigma, soft_s.shape)).astype(np.float32)
        print(f"noise sigma {args.noise_sigma:.5f} added to the input in stretched units")
    print(f"crop {size} px at sharp ({cx}, {cy}) / soft ({cx + dx}, {cy + dy}); soft stretch divisor {divisor:.4g} "
          f"beta ({betas[0]:.4f}, {betas[1]:.4f}, {betas[2]:.4f}); kernels {args.kernels} px at beta {args.beta}, K={args.rl_k}")

    kernels = [float(v) * zoom_eff for v in args.kernels.split(",")]
    labels = np.zeros((1, OP.LABEL_COUNT), dtype=np.float32)
    labels[0, OP.LABEL_MIN] = mins
    labels[0, OP.LABEL_BETA] = betas
    labels[0, OP.LABEL_PSF01] = 0.5
    x = torch.from_numpy(soft_s[None]).to(dev)

    arms = {"E3.0 (no prior)": OP.RLOperator(args.rl_k).to(dev).eval()}
    for name in (args.checkpoint, args.checkpoint.replace(".pt", "_final.pt")):
        if os.path.exists(os.path.join(args.cache, name)):
            model, _ = S.load_model(args.cache, name, dev)
            arms[f"E3.1 {name}"] = model.eval()

    outputs = {}
    for arm, model in arms.items():
        out = np.empty_like(soft_s)
        t0 = time.perf_counter()
        with torch.no_grad():
            for c, fwhm in enumerate(kernels):
                lab = labels.copy()
                lab[0, OP.LABEL_KERNEL_FWHM] = fwhm
                lab[0, OP.LABEL_KERNEL_BETA] = args.beta
                out[c] = model(DG.with_psf01(x, lab))[0, c].cpu().numpy()
        outputs[arm] = out
        print(f"  {arm}: {time.perf_counter() - t0:.0f} s")

    if args.roundtrip:
        from scipy.ndimage import zoom as ndzoom
        back = size / side
        outputs = {arm: np.stack([ndzoom(out[c], back, order=3) for c in range(3)]).astype(np.float32)
                   for arm, out in outputs.items()}
        soft_s = stretch(soft_native * inv, mins, betas)
        sharp_s = stretch(sharp_native * inv, mins, betas)
        for arm, out in outputs.items():
            if out.shape != soft_s.shape:
                raise SystemExit(f"round trip of {arm} landed on {out.shape}, wanted {soft_s.shape}")

    truth_lum = sharp_s.mean(axis=0)
    input_lum = soft_s.mean(axis=0)
    truth_w, n_truth, inp, _, row = gate_read(truth_lum, input_lum, None)
    print(f"\ntruth (sharp) {n_truth} stars at 12 MAD, width {truth_w:.3f} px on the stretched luminance; "
          f"noise MAD of the stretched luminance: truth {M.bg_stats(truth_lum)[1]:.5f}, input {M.bg_stats(input_lum)[1]:.5f}")
    print(f"{'arm':28s} {'out/truth':>9} {'stars':>6} {'ring excess':>11} {'skirt':>6}   per channel out/truth")
    print(f"{'input (soft)':28s} {inp[0]:9.3f} {inp[1]:6.2f} {inp[2]:+11.2f} {inp[3]:6.2f}   "
          + " ".join(f"{gate_read(sharp_s[c], soft_s[c], None)[2][0]:.3f}" for c in range(3)))
    for arm, out in outputs.items():
        r = row(out.mean(axis=0))
        per = " ".join(f"{gate_read(sharp_s[c], soft_s[c], out[c])[3][0]:.3f}" for c in range(3))
        print(f"{arm:28s} {r[0]:9.3f} {r[1]:6.2f} {r[2]:+11.2f} {r[3]:6.2f}   {per}")
    print("\nread: out/truth toward 1.0 from the input's; stars near 1.0 (over 1.10 with a narrower width is fabrication); "
          "ring excess against the input's null; skirt 1.0 is the truth's profile at 1 to 1.5 FWHM, under 0.9 the "
          "skirt is gone (a block and a moat). The C# oracle in linear read rec/A 1.007 / 1.085 / 1.139 per channel on this pair.")


if __name__ == "__main__":
    main()
