"""The operator on a NOISE-FREE blur of the truth: does the kill come from the kernel or the noise?

E3.0's pre-registration (docs/plans/deconvolver-training.md) kills the operator when an observer
session's star count runs over twice its input null, and reads that kill as "the tile-wise kernel or
the unrolling is wrong". The first reading (2026-09-13) tripped exactly that line while the width
recovered as the oracle ceiling promised. Two mechanisms produce that pair, and the gate cannot tell
them apart: a kernel wrong enough to sharpen background structure into peaks, or Richardson-Lucy's
own amplification of the injected noise into point sources. This control removes the noise and keeps
everything else: each gate cell's clean master is unstretched, blurred with the ROW's kernel (the one
the operator will be handed), restretched, and run through the same operator and the same gate. A
kernel or unrolling fault shows up here too; noise amplification cannot, because there is no noise.

The row's kernel is the ESTIMATED one, read off the noisy degraded tile, so this also measures the
estimate's cost by itself: the noise-free blur is by the estimated kernel and the deconvolution is by
the same kernel, so any residual width here is the iteration count's, and a run with the DRAWN
kernel (--drawn) is the exact-kernel twin.

Usage:
    python n2n_operator_control.py --cache C:/temp/tianwen-scratch/n2n-p2-blur-clamped --rl-k 20
"""
import argparse
import json
import os

import numpy as np
import torch

import n2n_deconv_gate as DG
import n2n_operator as OP
import n2n_smoke as S


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--cache", required=True)
    p.add_argument("--rl-k", type=int, default=20)
    p.add_argument("--gate-cells", type=int, default=64)
    p.add_argument("--gate-sessions", type=int, default=1)
    p.add_argument("--drawn", action="store_true",
                   help="blur and deconvolve with the DRAWN kernel's effective width instead of the estimate")
    p.add_argument("--slot", type=int, default=1, help="which degraded slot's kernel row to use")
    args = p.parse_args()

    mm, meta = S.open_cache(args.cache)
    labels, kernels = S.load_operator_labels(args.cache, meta)
    dev = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    model = OP.RLOperator(args.rl_k).to(dev)

    sessions = [("gate", S.gate_cells(meta, args.gate_sessions, args.gate_cells))]
    sessions += [(s[:44], c) for s, c in S.observer_cells(meta, args.gate_sessions, args.gate_cells)]
    out = {}
    print(f"noise-free control: operator K={args.rl_k}, kernel = {'DRAWN effective width' if args.drawn else 'the row ESTIMATE'}")
    print(f"           {DG.DeconvGate.header()}   {'inMAD/truthMAD':>14}")
    for name, cells in sessions:
        lab = labels[cells, args.slot - 1].copy()
        if args.drawn:
            lab[:, OP.LABEL_KERNEL_FWHM] = kernels[cells, args.slot - 1, 2]   # EffectiveKernelFwhmPx
        masters = np.asarray(mm[cells, S.SLOT_MASTER], dtype=np.float32)
        # The noise-free observed tile: unstretch, blur with the row's kernel, restretch. Same
        # padding, same kernel builder, same MTF bridge as the operator's own forward.
        x = torch.from_numpy(masters).to(dev)
        lt = torch.from_numpy(lab).to(dev)
        mins, betas = lt[:, OP.LABEL_MIN], lt[:, OP.LABEL_BETA]
        with torch.no_grad():
            lin = OP.unstretch(x, mins, betas)
            ker = torch.from_numpy(OP.kernel_batch(lab[:, OP.LABEL_KERNEL_FWHM], lab[:, OP.LABEL_KERNEL_BETA])).to(dev)
            B, C, H, W = lin.shape
            k = ker.shape[-1]
            r = k // 2
            w = ker[:, None].expand(B, C, k, k).reshape(B * C, 1, k, k)
            blurred = torch.nn.functional.conv2d(
                torch.nn.functional.pad(lin.reshape(1, B * C, H, W), (r, r, r, r), mode="replicate"),
                w, groups=B * C).reshape(B, C, H, W)
            observed = OP.restretch(blurred, mins, betas).cpu().numpy()
        # A cache-shaped array the gate can index: slot 0 the truth, slot 1 the noise-free blur.
        fake = np.zeros((len(cells), 2, C, H, W), dtype=np.float32)
        fake[:, 0] = masters
        fake[:, 1] = observed
        gate = DG.DeconvGate(fake, list(range(len(cells))), dev, psf01=lab, input_slot=1)
        m = gate.evaluate(model)
        # How noisy the REAL degraded input of these cells is, against its truth, for the reader.
        real = S.crop(np.asarray(mm[cells, args.slot], dtype=np.float32)).mean(axis=1)
        truth = S.crop(masters).mean(axis=1)
        mad_ratio = float(np.median([DG.M.bg_stats(real[i])[1] / DG.M.bg_stats(truth[i])[1] for i in range(len(cells))]))
        print(f"  {name[:8]:8s} {DG.DeconvGate.format(m)}   {mad_ratio:14.2f}   "
              f"(noise-free input null: stars {gate.stars_null:.3f}, in/truth {np.nanmean(gate.input_fwhm / gate.truth_fwhm):.3f})")
        out[name] = {"metrics": m, "cells": len(cells), "stars_null_noisefree": gate.stars_null,
                     "real_input_mad_over_truth": mad_ratio}
    path = os.path.join(args.cache, f"e30_control_k{args.rl_k}{'_drawn' if args.drawn else ''}.json")
    with open(path, "w", encoding="utf-8") as fh:
        json.dump(out, fh, indent=1)
    print(f"  written -> {path}")


if __name__ == "__main__":
    main()
