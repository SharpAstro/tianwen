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
--fade T U with the caps (the fourth version, 'floor4', #2243): the correction is whole where the sky it fills to scatters
by under T pixel-noise sigmas, nothing over U, in proportion between (registered: --fade 2 4 --tag floor4).
--level N F with --fade (the fifth version, 'floor5', #2252): the lowering is whole where the mask as a whole stands
under N pixel-noise sigmas over its sky and nothing from F; the fill the same where it lies under it and the input's own
floor does too (--fade 2 4 --level 2 4 --tag floor5).

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
    trust = 1.0
    if FADE:
        # floor4: how far the sky under the mask can be trusted. The scatter of the output OUTSIDE the mask under the very
        # Gaussian that makes bg, in pixel noise, the largest of the channels; in float64, since it is a small difference of
        # two large sums. Inside bright, structured nebula the mask is one large blob and the sky comes from far away.
        w64 = w.astype(np.float64); dn = np.maximum(gaussian_filter(w64, s_bg), 1e-6); df = np.maximum(gaussian_filter(w64, 3 * s_bg), 1e-6)
        far_only = gaussian_filter(w, s_bg) < 0.02; spread = np.zeros(out.shape[1:], np.float64)
        for c in range(CH):
            o64 = out[c].astype(np.float64); near = gaussian_filter(o64 * w64, s_bg) / dn; far = gaussian_filter(o64 * w64, 3 * s_bg) / df
            v = np.where(far_only, gaussian_filter(o64 * o64 * w64, 3 * s_bg) / df - far * far, gaussian_filter(o64 * o64 * w64, s_bg) / dn - near * near)
            spread = np.maximum(spread, np.sqrt(np.maximum(v, 0)) / sig[c, 0, 0])
        trust = np.clip((FADE[1] - spread) / (FADE[1] - FADE[0]), 0, 1).astype(np.float32)
    def sky(c):
        den = gaussian_filter(w, s_bg); bg = gaussian_filter(out[c] * w, s_bg) / np.maximum(den, 1e-6)
        thin = den < 0.02                      # deep inside a large mask: reach further
        if thin.any():
            den2 = gaussian_filter(w, 3 * s_bg)
            bg = np.where(thin, gaussian_filter(out[c] * w, 3 * s_bg) / np.maximum(den2, 1e-6), bg)
        return bg
    stands = lies = 1.0
    if LEVEL:
        # floor5 (#2252): how far the mask as a whole stands from the sky it is corrected to, per channel in pixel noise,
        # under the Gaussian of the near sky. Where the highest channel stands far OVER it the light is the scene's (a
        # nebula knot between the star cores of one large blob) and the lowering fades; where the lowest lies far UNDER
        # it the darkness is the scene's (a globule or lane) and the fill fades.
        dk = np.maximum(gaussian_filter(mf, s_bg), 1e-6)
        high = np.full(out.shape[1:], -np.inf, np.float32); low = np.full(out.shape[1:], np.inf, np.float32)
        for c in range(CH):
            level = gaussian_filter((out[c] - sky(c)) * mf, s_bg) / dk / sig[c, 0, 0]
            high = np.maximum(high, level); low = np.minimum(low, level)
        stands = np.clip((LEVEL[1] - high) / (LEVEL[1] - LEVEL[0]), 0, 1).astype(np.float32)
        lies = np.clip((LEVEL[1] + low) / (LEVEL[1] - LEVEL[0]), 0, 1).astype(np.float32)
        # A dig lies under its sky too; a globule is dark in the INPUT as well. Where the output says far under, the
        # input's own floor about the pixel (the fifth from the bottom of its 1 px-smoothed values in a 15 px window, so a
        # star may cover most of the window) is read against the same sky, and the fill keeps the larger weight.
        ys, xs = np.nonzero(m & (lies < 1)); floor_in = np.full(len(ys), np.inf, np.float32)
        for c in range(CH):
            smooth = gaussian_filter(inp[c], 1.0); bg = sky(c)
            for i, (py, px) in enumerate(zip(ys, xs)):
                win = smooth[max(0, py - 7):py + 8, max(0, px - 7):px + 8].ravel(); k = int(0.2 * (win.size - 1))
                floor_in[i] = min(floor_in[i], (np.partition(win, k)[k] - bg[py, px]) / sig[c, 0, 0])
        lies[ys, xs] = np.maximum(lies[ys, xs], np.clip((LEVEL[1] + floor_in) / (LEVEL[1] - LEVEL[0]), 0, 1).astype(np.float32))
    for c in range(CH):
        bg = sky(c)
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
                res[c] = np.where(m, out[c] + trust * (lies * up - stands * down), out[c]) if FADE else np.where(m, out[c] + up - down, out[c])
        else:
            res[c] = np.where(m, out[c] - s, out[c])
    return res, float(m.mean())

PIN = arg('--pin', 0.0); CAP_UP = arg('--cap-up', 0.0); CAP_DOWN = arg('--cap-down', 0.0)
FADE = (float(sys.argv[sys.argv.index('--fade') + 1]), float(sys.argv[sys.argv.index('--fade') + 2])) if '--fade' in sys.argv else None
LEVEL = (float(sys.argv[sys.argv.index('--level') + 1]), float(sys.argv[sys.argv.index('--level') + 2])) if '--level' in sys.argv else None

def main():
    model, mode = sys.argv[1], sys.argv[2]
    if mode not in ("floor", "fill"):
        sys.exit(f"mode must be floor or fill, got '{mode}' (the plan's 'floor2' is mode floor with --pin 2.5 --tag floor2; 'floor3' adds --cap-up 2 --cap-down 0.05 --tag floor3; 'floor4' adds --fade 2 4 --tag floor4; 'floor5' adds --level 2 4 --tag floor5)")
    if LEVEL and not (FADE and LEVEL[1] > LEVEL[0] > 0):
        sys.exit("--level is floor5: give it with --fade, as two rising sigmas "
                 "(<model> floor --pin 2.5 --cap-up 2 --cap-down 0.05 --fade 2 4 --level 2 4 --tag floor5)")
    if FADE and not (CAP_UP > 0 and FADE[1] > FADE[0] > 0):
        sys.exit("--fade is floor4: give it with mode floor, --pin and the caps, as two rising sigmas "
                 "(registered: <model> floor --pin 2.5 --cap-up 2 --cap-down 0.05 --fade 2 4 --tag floor4)")
    if (CAP_UP > 0 or CAP_DOWN > 0) and not (PIN > 0 and CAP_UP > 0 and CAP_DOWN > 0 and mode == "floor"):
        sys.exit("the caps are floor3: give mode floor with --pin, --cap-up and --cap-down together "
                 "(registered: <model> floor --pin 2.5 --cap-up 2 --cap-down 0.05 --tag floor3)")
    thr, grow, s_bg, s_lo = arg("--thr", 1.5), arg("--grow", 2), arg("--bg", 4.0), arg("--lo", 1.5)
    if (PIN > 0 or CAP_UP > 0) and "--tag" not in sys.argv:
        sys.exit("--pin or the caps need --tag (floor2, floor3, floor4 or floor5): the default tag is the mode, and would overwrite the first version's tiles")
    tag = arg("--tag", mode)
    if not any(os.path.exists(f"{EVAL}/{model}/{sub}/outputs.jsonl") for sub in SETS):
        sys.exit(f"no outputs.jsonl under {EVAL}/{model}/on-real-val or on-random: nothing to do")
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

if __name__ == "__main__":
    main()
