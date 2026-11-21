"""A prototype that did NOT become a product step (2026-11-21 night): the grain rule as a post step, second version.

    python -I crowd_smooth_v2.py <input master fits> <our starless fits> <out fits> [--residue-sigma 200] [--residue-px 19]
                                 [--nebula-sigma 20] [--no-residue] [--clip-noise <sigma>]

plate_grain.py's rule on OUR starless plate (in crowded sky, where the detail between 7 and 31 px is weak: the 31 px glow
plus noise), with the three things the first try as a post step (grain_rule_post.py, 2026-11-20) showed it needs:

  1. the plate's own noise is kept where the rule smooths (no drawn noise, so the step is the same every run);
  2. the bright nebula is held out (grain_rule_nebula_out.py's mask: the input's large-scale light 20 sigma over the sky);
  3. the remover's own residue round a star it took is not read as structure. The first try kept the plate wherever the
     detail was strong, and round a bright star the detail IS strong: what the remover left of the star. Those places
     stood as star-shaped islands in a smooth field. Here the stars layer (input less plate) says where a star was taken:
     about a local peak of it over <residue sigma> pixel sigma (200), in a box of <residue px> (19), the plate is set
     aside before the detail is measured, and the smoothing writes the glow there with noise of the local level, drawn
     from a fixed seed. --clip-noise redraws the plate's own fine residue the same way wherever it stands over that
     many sigma (the core of a faint star the remover left).

Prints the crowded share, the share smoothed, the share set aside as residue and the nebula's share.
"""
import sys
from pathlib import Path

import numpy as np
from astropy.io import fits
from scipy.ndimage import gaussian_filter, maximum_filter, median_filter, uniform_filter

sys.path.insert(0, str(Path(__file__).resolve().parent))   # python -I leaves the script's folder off the path
import plate_grain as G  # noqa: E402

T = G.T


def opt(name, default):
    return float(sys.argv[sys.argv.index(name) + 1]) if name in sys.argv else default


def nebula_weight(inp, ns, feather=8.0, block=8, filt=7):
    h, w = inp.shape
    sig = 1.4826 * float(np.median(np.abs(np.diff(inp[::4], axis=1)))) / np.sqrt(2)
    bh, bw = h // block, w // block
    blocks = np.median(inp[:bh * block, :bw * block].reshape(bh, block, bw, block).transpose(0, 2, 1, 3).reshape(bh, bw, -1), axis=2)
    small = median_filter(blocks, filt)
    big = np.zeros_like(inp); big[:bh * block, :bw * block] = np.repeat(np.repeat(small, block, 0), block, 1)
    neb = big - float(np.percentile(small, 5)) > ns * sig
    wt = np.clip(gaussian_filter(neb.astype(np.float32), feather) * 2, 0, 1)
    return np.maximum(wt, neb), float(neb.mean())


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    master_path, ours_path, out_path = args[0], args[1], args[2]
    rs, ns, use_residue = opt("--residue-sigma", 200.0), opt("--nebula-sigma", 20.0), "--no-residue" not in sys.argv
    clip = opt("--clip-noise", 0.0)
    m = np.asarray(fits.getdata(master_path), np.float32)
    m3 = m[None] if m.ndim == 2 else m[:3]
    with fits.open(ours_path) as h:
        o = np.asarray(h[0].data, np.float32); header = h[0].header
    mono = o.ndim == 2
    o3 = o[None] if mono else o
    lum = np.nanmean(m3, axis=0)
    ok = np.isfinite(lum) & (lum != 0)
    L = np.where(ok, lum, np.nanmedian(lum[ok]))
    sigL = G.noise_map(L)
    peaks = (L == maximum_filter(L, 5)) & (L - median_filter(L, 15) > 6 * np.maximum(sigL, 1e-12)) & ok
    dens = uniform_filter(peaks.astype(np.float32), 61)
    w = np.clip((dens - 0.003) / 0.005, 0, 1); sq = np.sqrt(np.maximum(dens, 0))
    neb, neb_share = nebula_weight(np.nan_to_num(L), ns)
    # Where a BRIGHT star was taken: a local peak of the stars layer over <residue sigma> pixel sigma, and <residue px>
    # about it. In crowded sky some star's light was taken almost everywhere (a first try on the mean over 9 px set 79 %
    # of the Tarantula aside), so only the bright ones count: at 200 sigma they are 2,248 on the Tarantula, 8 % of the frame.
    taken = np.nan_to_num(L - np.nanmean(o3, axis=0))
    box = int(opt("--residue-px", 19))
    bright = (taken == maximum_filter(taken, 5)) & (taken > rs * sigL)
    residue = maximum_filter(bright, box) if use_residue else np.zeros(L.shape, bool)
    C, H, W = o3.shape
    out = o3.copy(); note = []
    ring = G.zero_ring(o3)
    rng = np.random.default_rng(20261121)
    for c in range(C):
        nan = ring | ~np.isfinite(o3[c])
        P = np.where(nan, np.nanmedian(np.where(ring, np.nan, o3[c])), o3[c])
        sig = G.noise_map(P)
        base0 = G.base_of(median_filter(P, 7))
        Pm = np.where(residue, base0, P)
        p7 = median_filter(Pm, 7); base = G.base_of(p7); d = p7 - base
        r, dd = [], []
        for y in range(0, H - 100, 100):
            for x in range(0, W - 100, 100):
                if w[y:y + 100, x:x + 100].mean() > 0.5 and not nan[y:y + 100, x:x + 100].any():
                    blk = d[y:y + 100, x:x + 100]
                    r.append(1.4826 * np.median(np.abs(blk - np.median(blk))) / max(float(sig[y + 50, x + 50]), 1e-12)); dd.append(dens[y:y + 100, x:x + 100].mean())
        if len(r) < 8:
            note.append("few"); continue
        r, dd = np.array(r), np.array(dd); q = np.quantile(dd, np.linspace(0, 1, 6)); xs, ys = [], []
        for k in range(5):
            mk = (dd >= q[k]) & (dd <= q[k + 1])
            if mk.sum() >= 2: xs.append(np.sqrt(np.median(dd[mk]))); ys.append(np.percentile(r[mk], 25))
        if len(set(np.round(xs, 6))) >= 3:
            fit = np.polyfit(xs, ys, 1); g = np.maximum(np.polyval(fit, sq), min(ys))
        else:
            g = np.full((H, W), np.percentile(r, 25), np.float32)
        t = T * g * sig
        a = np.sqrt(uniform_filter(d * d, 21)); keep = np.clip((a - t) / np.maximum(t, 1e-12), 0, 1)
        share = w * (1 - keep) * (1 - neb)
        drawn = rng.normal(0, 1, P.shape).astype(np.float32) * sig
        own = P - p7
        if clip > 0:
            # What stands over <clip> sigma in the plate's own fine residue is no noise (the core of a faint star the
            # remover left): there, as on the residue, noise of the local level is drawn.
            own = np.where(np.abs(own) > clip * sig, drawn, own)
        own = np.where(residue, drawn, own)
        res = share * (base + own) + (1 - share) * P
        out[c] = np.where(nan, o3[c], res).astype(np.float32)
        note.append(f"grain {np.percentile(r, 25):.3f} smoothed {100 * share.mean():.1f}%")
    print(f"{int(peaks.sum())} compact peaks; crowded (weight 1) on {100 * (w >= 1).mean():.1f} % of the frame; nebula over {ns:g} sigma {100 * neb_share:.1f} %; "
          f"set aside as residue {100 * residue.mean():.1f} % of the frame, {100 * (residue & (w > 0.5)).sum() / max((w > 0.5).sum(), 1):.1f} % of the crowded part")
    print(" | ".join(note) or "untouched")
    fits.PrimaryHDU(out[0] if mono else out, header).writeto(out_path, overwrite=True)
    print(out_path)


main()
