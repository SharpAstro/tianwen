"""Cross-arm comparison of v15 / v17b / v17c at MATCHED noise, under BOTH detection bars.

Three things this does that the training logs cannot.

1. It compares the arms on ONE slice of tiles. The logs' gate numbers come from each run's own
   cache, and v15's cache holds 8 sessions against v17's 60, so a per-log number is measured on
   different pixels even though the val SESSIONS are pinned by name. Everything here runs over the
   same n2n-big observer cells.

2. It uses the observer session -- the val session no arm selected a checkpoint on. Selecting on a
   measurement spends its held-out-ness, and v17b/v17c selected on the gate session, so a table
   built there would flatter them relative to v15 for no reason but bookkeeping.

3. It sweeps a deployment STRENGTH knob (a plain blend toward the input) so each checkpoint
   becomes a CURVE rather than a point. Without that, a comparison at the checkpoints' own
   operating points confounds "different model" with "pushed harder": every one of these runs
   stopped at a different noise level, and noise removed trades against structure kept along a
   single model's own curve.

And it reports the fabrication count under both bars. The default bar is `med + 5*MAD` of the
array being counted, so a harder denoiser lowers its own bar; the absolute twin holds the INPUT's
MAD, which is the same physical threshold the floor was measured at.
"""
import numpy as np

import n2n_metrics as M
import n2n_smoke as S
from n2n_gate import Gate, FAINT_BUCKET
from n2n_paths import cache

BIG = cache("n2n-big")
DS = cache("n2n-ds")
C21 = cache("n2n-c21")   # 21 sessions x 45 cells = 945, v15's VOLUME at v17's density
D8 = cache("n2n-d8")     # v15's own 8 sessions x 45 = 360, only the DENSITY moves
CELLS = 48
# 1.0 is the checkpoint as trained; below that the output is mixed back toward the noisy input,
# which is how every shipping denoiser exposes strength.
BLENDS = (1.0, 0.85, 0.7, 0.55, 0.4, 0.25)
LEVELS = (0.90, 0.85, 0.80, 0.75, 0.70)

# v18a/v18b are the pair that answers the question; v15 and v17c are the reference points either
# side of it. v17b is dropped here (it differs from v17c in CAPACITY, which this run is not about)
# so the table stays readable at four arms.
CKPTS = ([("v15 8x120", DS, f"n2n_v15_s{i}_final.pt") for i in range(3)]
         + [("v17c 60x45", BIG, f"n2n_v17c_s{i}_final.pt") for i in range(3)]
         + [("v19c 21x45", C21, f"n2n_v19c_s{i}_final.pt") for i in range(3)]
         + [("v19d 8x45", D8, f"n2n_v19d_s{i}_final.pt") for i in range(3)])


def curve(gate, model, planes):
    """One checkpoint's (noise, amplitude, fabrication) trace across the strength knob."""
    den_m = gate._forward(model, planes, gate.masters)
    den_s = gate._forward(model, planes, gate.subs)
    rows = []
    for a in BLENDS:
        la = S.crop(gate.masters + a * (den_m - gate.masters)).mean(axis=1)
        noise = float(np.mean([M.bg_stats(t)[1] for t in la])) / gate.base_noise
        amp, det, _ = M.measure(la, gate.stars, gate.lm)

        # Fabrication is read on a SUB, where the model extrapolates hardest. On the master the
        # input is the reference, so a low count there would mean erasure, not honesty.
        cs = S.crop(gate.subs + a * (den_s - gate.subs))
        rows.append(dict(
            a=a, noise=noise, amp=float(amp[FAINT_BUCKET]), det=float(det[FAINT_BUCKET]),
            rel=float(gate.spurious_per_tile(cs).mean()) - gate.floor_spurious,
            abs=float(gate.spurious_per_tile(cs, ref_mad=gate.sub_mad).mean()) - gate.floor_spurious))
    return rows


def at(rows, level, key):
    """Interpolate a metric at a noise level, or None when the curve never reaches it.

    None is a finding (this checkpoint cannot be pushed that far), never a value to fill in.
    """
    order = sorted(rows, key=lambda r: r["noise"])
    xs = [r["noise"] for r in order]
    if level < xs[0] or level > xs[-1]:
        return None
    return float(np.interp(level, xs, [r[key] for r in order]))


def main():
    import torch
    dev = "cuda" if torch.cuda.is_available() else "cpu"
    mm, meta = S.open_cache(BIG)
    observers = S.observer_cells(meta, 1, CELLS)
    if not observers:
        raise SystemExit("no observer session: the gate is selecting on every val session")
    session, cells = observers[0]
    print(f"device {dev}, observer session {session}, {len(cells)} cells, "
          f"{len(CKPTS)} checkpoints\n")

    gate = Gate(mm, cells, dev)
    print(f"raw-sub floor {gate.floor_spurious:.1f} spurious/tile "
          f"(identical under both bars by construction)\n")

    curves = {}
    for cfg, cache, name in CKPTS:
        model, planes = S.load_model(cache, name, dev)
        rows = curve(gate, model, planes)
        curves.setdefault(cfg, []).append((name, rows))
        span = f"{rows[0]['noise']:.2f}-{rows[-1]['noise']:.2f}"
        print(f"{name:24s} noise span {span}   as-trained "
              f"amp {rows[0]['amp']:.2f} rel {rows[0]['rel']:+6.1f} abs {rows[0]['abs']:+6.1f}")

    cfgs = list(curves)
    for key, label, prec in (("amp", "faint amplitude kept", 3),
                             ("rel", "fabrication over floor, RELATIVE bar (the old number)", 1),
                             ("abs", "fabrication over floor, ABSOLUTE bar (input's MAD)", 1)):
        print(f"\n=== {label}, at matched noise ===")
        print(f"{'noise':>6}" + "".join(f" | {c:>26}" for c in cfgs))
        for lv in LEVELS:
            cells_out = []
            for c in cfgs:
                vals = [at(rows, lv, key) for _, rows in curves[c]]
                shown = " ".join("  -  " if v is None else f"{v:6.{prec}f}" for v in vals)
                got = [v for v in vals if v is not None]
                cells_out.append(f"{shown}  m={np.mean(got):6.{prec}f}" if got
                                 else f"{shown}      -   ")
            print(f"{lv:6.2f}" + "".join(f" | {c}" for c in cells_out))


if __name__ == "__main__":
    main()
