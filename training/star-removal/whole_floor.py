"""The dig_floor post-step on whole linear masters. python -I whole_floor.py <w-tag> <floor|fill|floor2|floor3> [scorecard.json]
Reads f2-whole-master/<w-tag>/{andromeda,tarantula,sag}_starless.fits and the input masters of the scorecard config,
writes f2-whole-master/<w-tag>+<mode>/<name>_starless.post.fits, NOT <name>_starless.fits. score_whole.py reads
<name>_starless.fits and copies a --plates file in only when that does not exist yet, so to score a result hand it over as
--plates <name>=<path to the .post.fits>; a stale <name>_starless.fits already in the folder is scored in preference to a
re-run .post.fits."""
import json, os, sys
import numpy as np
from astropy.io import fits
from scipy.ndimage import gaussian_filter, binary_dilation, median_filter, maximum_filter, zoom
S = os.environ.get("TIANWEN_SR_SCRATCH", "C:/temp/tianwen-scratch")
cfg = json.load(open(sys.argv[3] if len(sys.argv) > 3 else f"{S}/whole-blend3-scorecard.json", encoding="utf-8"))
tag, mode = sys.argv[1], sys.argv[2]
if mode not in ("floor", "fill", "floor2", "floor3"):
    sys.exit(f"mode must be floor, fill, floor2 or floor3, got '{mode}'")
THR, GROW, SBG, SLO, B = 1.5, 2, 4.0, 1.5, 64

def sigma_map(c, ok):
    """Local noise: 1.4826 MAD of the 5 px high-pass in B px blocks, smoothed and upsampled."""
    h = c - median_filter(c, 5); H, W = c.shape; g = np.full((H // B + 1, W // B + 1), np.nan, np.float32)
    for i in range(g.shape[0]):
        for j in range(g.shape[1]):
            v = h[i * B:(i + 1) * B, j * B:(j + 1) * B][ok[i * B:(i + 1) * B, j * B:(j + 1) * B]]
            if v.size > 200: g[i, j] = 1.4826 * np.median(np.abs(v - np.median(v)))
    g = np.where(np.isnan(g), np.nanmedian(g), g)
    return np.maximum(zoom(gaussian_filter(g, 1.0), (H / g.shape[0], W / g.shape[1]), order=1)[:H, :W], 1e-9)

for name in ("andromeda", "tarantula", "sag"):
    hd = fits.open(f"{S}/f2-whole-master/{tag}/{name}_starless.fits")[0]; out = np.asarray(hd.data, np.float32)
    inp = np.asarray(fits.getdata(cfg["masters"][name]), np.float32)
    mono = out.ndim == 2
    if mono: out = out[None]
    if inp.ndim == 2: inp = inp[None]
    assert inp.shape == out.shape, (name, inp.shape, out.shape)
    nan = np.isnan(out) | np.isnan(inp); ok = ~nan.any(0)
    o = np.where(nan, 0, out); x = np.where(nan, 0, inp)
    sig = np.stack([sigma_map(c, ok) for c in x])
    removed = np.stack([gaussian_filter(c, 1.0) for c in (x - o) / sig])
    m = binary_dilation(removed.max(0) > THR, iterations=GROW) & ok
    w = (ok & ~m).astype(np.float32); mf = m.astype(np.float32); res = o.copy(); lift = []
    den = gaussian_filter(w, SBG); den2 = gaussian_filter(w, 3 * SBG); thin = den < 0.02; dm = np.maximum(gaussian_filter(mf, SLO), 1e-6)
    for c in range(o.shape[0]):
        bg = gaussian_filter(o[c] * w, SBG) / np.maximum(den, 1e-6)
        bg = np.where(thin, gaussian_filter(o[c] * w, 3 * SBG) / np.maximum(den2, 1e-6), bg)
        s = gaussian_filter((o[c] - bg) * mf, SLO) / dm
        if mode == "floor": s = np.minimum(s, 0)
        if mode == "floor3":   # floor2, each correction capped by what the model took there (dig_floor.py --cap-up 2 --cap-down 0.05)
            oc = np.where(m & ((o[c] - bg) < -2.5 * sig[c]), bg, o[c]); taper = np.clip(2 * gaussian_filter(mf, 1.0) - 1, 0, 1)
            s2 = gaussian_filter((oc - bg) * mf, SLO) / dm
            took = maximum_filter(np.maximum(gaussian_filter(x[c] - o[c], 1.0), 0), 5)
            up = np.clip(oc - np.minimum(s2, 0) * taper - o[c], 0, 2.0 * took)
            res[c] = np.where(m, o[c] + up - np.minimum(np.maximum(s2, 0) * taper, 0.05 * took), o[c])
        elif mode == "floor2":   # pinholes set to the sky, then the smooth lift tapered at the mask's edge (dig_floor.py --pin 2.5)
            oc = np.where(m & ((o[c] - bg) < -2.5 * sig[c]), bg, o[c])
            s = np.minimum(gaussian_filter((oc - bg) * mf, SLO) / dm, 0) * np.clip(2 * gaussian_filter(mf, 1.0) - 1, 0, 1)
            res[c] = np.where(m, oc - s, o[c])
        else:
            res[c] = np.where(m, o[c] - s, o[c])
        lift.append(float(((res[c] - o[c])[m] / sig[c][m]).mean()))
    res = np.where(nan, np.nan, res).astype(np.float32)
    d = f"{S}/f2-whole-master/{tag}+{mode}"; os.makedirs(d, exist_ok=True)
    fits.PrimaryHDU(res[0] if mono else res, hd.header).writeto(f"{d}/{name}_starless.post.fits", overwrite=True)
    print(f"{name}: masked {100 * m.mean():.1f} % of pixels, mean lift in mask {np.round(lift, 3)} sigma", flush=True)
