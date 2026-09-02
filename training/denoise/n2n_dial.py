r"""Which strength knob ships: the conditioning lie, or the blend toward the input?

Two dials exist and only one should be user-facing.

  blend     out = master + a * (den - master). Trivially safe, exactly monotone, and cannot
            invent anything the model did not already produce at a=1. Every comparison in this
            programme since v20 has been made along it.
  strength  with_sigma(x, strength) multiplies the conditioning plane, telling the model its
            input is noisier or cleaner than it is. Free, no retraining, and it changes what
            the network COMPUTES rather than how much of the result is used -- so unlike the
            blend it can walk the model off the conditioning distribution it was trained on.

The question is not which is nicer. It is whether `strength` buys anything the blend does not:

  at the SAME residual noise on the SAME master, which dial leaves more faint-star amplitude?

If they land on the same curve, ship the blend -- it is the one that cannot extrapolate. Ship
`strength` only if it is measurably ahead, and only over the range where it is measured, since
"monotone and safe" is an assumption about a lie told to a network, not a property of a knob.

Fabrication is reported beside amplitude for exactly that reason. The blend cannot invent (it
is a convex combination of two things that already exist); `strength` can, and a dial that wins
on amplitude by hallucinating point sources is not a win. The count uses the Gate's absolute
bar against the input's own MAD, so a model that crushes the background cannot lower its own
threshold and score better.

Usage (from this directory; --cache defaults to n2n-d8 under TIANWEN_SCRATCH, see n2n_paths.py):
  python n2n_dial.py --ckpt n2n_v19d_s2_final.pt
"""
import argparse
import io
import json
import os
import sys

import numpy as np

import n2n_metrics as M
import n2n_smoke as S
from n2n_gate import Gate, FAINT_BUCKET
from n2n_paths import cache

EVAL = cache("n2n-eval4")
CELLS = 48
BLENDS = (1.0, 0.85, 0.7, 0.55, 0.4, 0.25, 0.1)
# Deliberately reaches well below 1.0 as well as above: if the dial is to replace the blend it
# has to cover the GENTLE half of the range, which is where a user actually spends their time.
STRENGTHS = (0.15, 0.25, 0.4, 0.55, 0.7, 0.85, 1.0, 1.5, 2.0, 3.0)
LEVELS = (0.90, 0.85)


def forward_at(gate, model, planes, src, strength):
    import torch
    out = []
    with torch.no_grad():
        for i in range(0, len(src), 8):
            x = torch.from_numpy(src[i:i + 8]).to(gate.dev)
            xc = S.with_sigma(x, strength=strength, planes=planes) if planes else x
            out.append(model(xc).cpu().numpy())
    return np.concatenate(out)


def point(gate, la):
    """One (noise, faint amplitude) sample, measured exactly as the frontier tables measure it."""
    noise = float(np.mean([M.bg_stats(t)[1] for t in la])) / gate.base_noise
    amp, _det, _ = M.measure(la, gate.stars, gate.lm)
    return noise, float(amp[FAINT_BUCKET])


def blend_curve(gate, model, planes):
    den = forward_at(gate, model, planes, gate.masters, 1.0)
    rows = []
    for a in BLENDS:
        la = S.crop(gate.masters + a * (den - gate.masters)).mean(axis=1)
        n, amp = point(gate, la)
        rows.append(dict(knob=a, noise=n, amp=amp, spur=None))
    return rows


def strength_curve(gate, model, planes):
    rows = []
    for st in STRENGTHS:
        la = S.crop(forward_at(gate, model, planes, gate.masters, st)).mean(axis=1)
        n, amp = point(gate, la)
        # Fabrication is measured on a SUB, where the model extrapolates hardest, against the
        # input's own frozen MAD so the bar cannot move with the denoising strength.
        den_s = S.crop(forward_at(gate, model, planes, gate.subs, st))
        spur = float(gate.spurious_per_tile(den_s, ref_mad=gate.sub_mad).mean())
        rows.append(dict(knob=st, noise=n, amp=amp, spur=spur))
    return rows


def at(rows, level, key):
    order = sorted(rows, key=lambda r: r["noise"])
    xs = [r["noise"] for r in order]
    if level < xs[0] or level > xs[-1]:
        return None
    return float(np.interp(level, xs, [r[key] for r in order]))


def short(session):
    parts = session.split("|")
    return f"{parts[1].replace('ZWO ', '').replace('SVBONY ', '')} / {parts[2]}"[:42]


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--cache", default=cache("n2n-d8"))
    p.add_argument("--ckpt", default="n2n_v19d_s2_final.pt")
    args = p.parse_args()

    import torch
    dev = "cuda" if torch.cuda.is_available() else "cpu"
    model, planes = S.load_model(args.cache, args.ckpt, dev)
    mm, meta = S.open_cache(EVAL)
    observers = S.observer_cells(meta, 0, CELLS)
    print(f"{args.ckpt}: cond planes {planes}, device {dev}, "
          f"{len(observers)} observers\n")

    results = {}
    for session, cells in observers:
        gate = Gate(mm, cells, dev)
        bl = blend_curve(gate, model, planes)
        stc = strength_curve(gate, model, planes)
        results[session] = {"blend": bl, "strength": stc,
                            "floor_spurious": gate.floor_spurious}

        print(f"=== {short(session)} ({len(cells)} cells) ===")
        print("  strength dial (spur is per-tile fabrications above the INPUT's own bar; "
              f"raw sub floor {gate.floor_spurious:.2f})")
        for r in stc:
            print(f"    s={r['knob']:<5.2f} noise {r['noise']:.3f}  amp {r['amp']:.3f}  "
                  f"spur {r['spur']:.2f}")
        print("  blend dial")
        for r in bl:
            print(f"    a={r['knob']:<5.2f} noise {r['noise']:.3f}  amp {r['amp']:.3f}")
        for lv in LEVELS:
            b = at(bl, lv, "amp")
            s = at(stc, lv, "amp")
            sp = at(stc, lv, "spur")
            bt = "  -  " if b is None else f"{b:.3f}"
            st_ = "  -  " if s is None else f"{s:.3f}"
            gap = "  -  " if (b is None or s is None) else f"{s - b:+.3f}"
            spt = "  -  " if sp is None else f"{sp:.2f}"
            print(f"  @noise {lv:.2f}: blend {bt}   strength {st_}   "
                  f"strength - blend {gap}   spur {spt}")
        print()

    print("=== faint amplitude at matched noise: strength minus blend ===")
    print(f"{'observer':44s}" + "".join(f"{f'@{lv:.2f}':>12s}" for lv in LEVELS))
    deltas = {lv: [] for lv in LEVELS}
    for session, _cells in observers:
        row = f"{short(session):44s}"
        for lv in LEVELS:
            b = at(results[session]["blend"], lv, "amp")
            s = at(results[session]["strength"], lv, "amp")
            if b is None or s is None:
                row += "       -    "
            else:
                deltas[lv].append(s - b)
                row += f"{s - b:+11.3f} "
        print(row)
    print()
    for lv in LEVELS:
        d = deltas[lv]
        if d:
            print(f"  @{lv:.2f}: mean {np.mean(d):+.3f}, range "
                  f"[{min(d):+.3f}, {max(d):+.3f}] over {len(d)} observers")

    with io.open("dial-results.json", "w", encoding="utf-8") as f:
        json.dump({s: v for s, v in results.items()}, f, indent=1)
    print("\nwritten dial-results.json")
    return 0


if __name__ == "__main__":
    sys.exit(main())
