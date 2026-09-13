"""Read E3.0 and saved checkpoints through the deconvolution gate, on the training's own cells.

The gate row grew a skirt column after E3.2 and E3.3 were trained, so their logs do not carry it.
This re-reads any checkpoint on the same gate (selector cells and the observer session), one probe
each, and prints the full row.

    python n2n_gate_checkpoints.py --cache C:/temp/tianwen-scratch/n2n-p2-blur-clamped e31_s0_final.pt e32_s0_final.pt e33_s0_final.pt
"""
import argparse
import time

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
    p.add_argument("checkpoints", nargs="*")
    args = p.parse_args()
    mm, meta = S.open_cache(args.cache)
    labels, kernels = S.load_operator_labels(args.cache, meta)
    dev = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    gate_input = 1
    cells = S.gate_cells(meta, args.gate_sessions, args.gate_cells)
    lab = labels[cells, gate_input - 1]
    gate = DG.DeconvGate(mm, cells, dev, psf01=lab, input_slot=gate_input)
    observers = []
    for s, ocells in S.observer_cells(meta, args.gate_sessions, args.gate_cells):
        og = DG.DeconvGate(mm, ocells, dev, psf01=labels[ocells, gate_input - 1], input_slot=gate_input)
        observers.append((s, og))
    print(f"gate: {len(cells)} cells, input at {np.nanmean(gate.input_fwhm / gate.truth_fwhm):.3f}x truth, "
          f"stars null {gate.stars_null:.3f}; {len(observers)} observer(s)")
    print(f"{'arm':22s} {DG.DeconvGate.header()}")
    arms = [("E3.0 K=%d" % args.rl_k, OP.RLOperator(args.rl_k).to(dev).eval())]
    for name in args.checkpoints:
        model, _ = S.load_model(args.cache, name, dev)
        arms.append((name, model.eval()))
    for name, model in arms:
        t0 = time.perf_counter()
        m = gate.evaluate(model)
        print(f"{name:22s} {DG.DeconvGate.format(m)}   ({time.perf_counter() - t0:.0f} s)")
        for si, (s, og) in enumerate(observers):
            om = og.evaluate(model)
            print(f"{'  obs%d' % si:22s} {DG.DeconvGate.format(om)}   null {og.stars_null:.3f} -> {om['stars_kept'] / og.stars_null:.2f}x")


if __name__ == "__main__":
    main()
