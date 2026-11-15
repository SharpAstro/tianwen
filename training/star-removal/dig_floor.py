"""Exploration (2026-11-15): a classical post-step on a star remover's output, no training.

Where the model took a star (input minus output above a threshold, per tile), the output's LOW-frequency departure from
the sky around it (the output outside the mask, spread in by a normalised Gaussian) is either lifted where it is negative
('floor': a dig is filled, nothing else moves) or removed on both sides ('fill': what SXT's smooth fill does). The pixel
noise under the star is kept: only the smooth part moves.

    python -I dig_floor.py <model> <floor|fill> [--pin 2.5] [--thr 1.5] [--grow 2] [--bg 4] [--lo 1.5] [--tag name]

--pin N (the second version, 'floor2' in the plan): a pixel more than N sigma under the sky around it is set to that sky
first (a pinhole at a star's core), and the smooth lift is tapered to nothing at the mask's edge.
--cap-up U --cap-down D with --pin (the third version, 'floor3'): a lift is at most U of what the model took at that
pixel, and what stands above the sky is lowered by at most D of it.

Reads f2-eval-master/<model>/on-real-val and on-random, writes f2-eval-master/<model>+<tag>/ the same way, so
real_star_check3.py (real and --twin) scores it as a model. Glue only.
"""
import json, os, sys
import numpy as np
from scipy.ndimage import gaussian_filter, binary_dilation, median_filter, maximum_filter

SCRATCH = os.environ.get("TIANWEN_SR_SCRATCH", "C:/temp/tianwen-scratch")
EVAL = f"{SCRATCH}/f2-eval-master"
SETS = {"on-real-val": f"{SCRATCH}/r2-real-val", "on-random": f"{SCRATCH}/f2-random-master"}
SIZE, CH = 256, 3

def arg(name, default):
    return type(default)(sys.argv[sys.argv.index(name) + 1]) if name in sys.argv else default

def read(p): return np.fromfile(p, "<f2").reshape(CH, SIZE, SIZE).astype(np.float32)
def noise(img): return np.array([1.4826 * np.median(np.abs((h := c - median_filter(c, 5)) - np.median(h))) for c in img])

def post(inp, out, mode, thr, grow, s_bg, s_lo):
    sig = np.maximum(noise(inp), 1e-9)[:, None, None]
    removed = np.stack([gaussian_filter(c, 1.0) for c in (inp - out) / sig])
    m = removed.max(0) > thr
    if not m.any(): return out, 0.0
    m = binary_dilation(m, iterations=grow)
    w = (~m).astype(np.float32); mf = m.astype(np.float32)
    res = out.copy()
    for c in range(CH):
        den = gaussian_filter(w, s_bg); bg = gaussian_filter(out[c] * w, s_bg) / np.maximum(den, 1e-6)
        thin = den < 0.02                      # deep inside a large mask: reach further
        if thin.any():
            den2 = gaussian_filter(w, 3 * s_bg)
            bg = np.where(thin, gaussian_filter(out[c] * w, 3 * s_bg) / np.maximum(den2, 1e-6), bg)
        s = gaussian_filter((out[c] - bg) * mf, s_lo) / np.maximum(gaussian_filter(mf, s_lo), 1e-6)
        if mode == "floor": s = np.minimum(s, 0)
        if PIN > 0:
            # v2: a pixel more than PIN sigma under the sky around it is a pinhole at a star's core, not noise: set to the sky.
            # Then the smooth lift, tapered to nothing at the mask's edge so no step is drawn there.
            o = np.where(m & ((out[c] - bg) < -PIN * sig[c]), bg, out[c])
            s = gaussian_filter((o - bg) * mf, s_lo) / np.maximum(gaussian_filter(mf, s_lo), 1e-6)
            if mode == "floor": s = np.minimum(s, 0)
            taper = np.clip(2 * gaussian_filter(mf, 1.0) - 1, 0, 1)
            res[c] = np.where(m, o - s * taper, out[c])
            if CAP_UP > 0:
                # v3: no correction exceeds a share of what the MODEL took at that pixel (its removal, smoothed 1 px, the
                # largest within 2 px). A lift is at most CAP_UP of it, so smooth bright nebula the model barely touched is
                # left alone; what still stands ABOVE the sky is lowered by at most CAP_DOWN of it, which reaches the
                # residue at a saturated core (the removal there is hundreds of sigma) and not a galaxy's nucleus.
                took = maximum_filter(np.maximum(gaussian_filter(inp[c] - out[c], 1.0), 0), 5)
                up = np.clip(res[c] - out[c], 0, CAP_UP * took)
                s2 = gaussian_filter((o - bg) * mf, s_lo) / np.maximum(gaussian_filter(mf, s_lo), 1e-6)
                down = np.minimum(np.maximum(s2, 0) * taper, CAP_DOWN * took)
                res[c] = np.where(m, out[c] + up - down, out[c])
        else:
            res[c] = np.where(m, out[c] - s, out[c])
    return res, float(m.mean())

PIN = arg('--pin', 0.0); CAP_UP = arg('--cap-up', 0.0); CAP_DOWN = arg('--cap-down', 0.0)

def main():
    model, mode = sys.argv[1], sys.argv[2]
    thr, grow, s_bg, s_lo = arg("--thr", 1.5), arg("--grow", 2), arg("--bg", 4.0), arg("--lo", 1.5)
    tag = arg("--tag", mode)
    for sub, src in SETS.items():
        d = f"{EVAL}/{model}/{sub}"; o = f"{EVAL}/{model}+{tag}/{sub}"
        if not os.path.exists(f"{d}/outputs.jsonl"): continue
        os.makedirs(f"{o}/tiles", exist_ok=True)
        rows = [json.loads(l) for l in open(f"{d}/outputs.jsonl", encoding="utf-8") if l.strip()]
        frac = []
        for r in rows:
            res, f = post(read(f"{src}/{r['Tile']}"), read(f"{d}/{r['Output']}"), mode, thr, grow, s_bg, s_lo)
            res.astype("<f2").tofile(f"{o}/{r['Output']}"); frac.append(f)
        with open(f"{o}/outputs.jsonl", "w", encoding="utf-8", newline="\n") as fh:
            fh.writelines(json.dumps(r) + "\n" for r in rows)
        print(f"{sub}: {len(rows)} tiles, masked {100 * np.mean(frac):.1f} % of pixels", flush=True)

main()
