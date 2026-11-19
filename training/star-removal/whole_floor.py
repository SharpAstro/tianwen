"""The dig_floor post-step on whole linear masters. python -I whole_floor.py <w-tag> <floor|fill|floor2|floor3|floor4> [scorecard.json]
floor4 (#2243) is floor3 that fades where the sky it fills to cannot be trusted: inside bright, structured nebula floor3
filled real dark structure flat and lowered the nebula in squares, because the 'removed star' set is one large blob there
and the sky under it is interpolated from far away. The C# step (DigFill) is floor4.
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
if mode not in ("floor", "fill", "floor2", "floor3", "floor4"):
    sys.exit(f"mode must be floor, fill, floor2, floor3 or floor4, got '{mode}'")
THR, GROW, SBG, SLO, B = 1.5, 2, 4.0, 1.5, 64
# floor4: the correction is whole where the sky it fills to scatters by under TRUSTED pixel-noise sigmas, nothing over UNTRUSTED.
TRUSTED, UNTRUSTED = 2.0, 4.0

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
    trust = 1.0
    if mode == "floor4":
        # How far the sky under the set can be trusted: the scatter of the plate OUTSIDE the set under the very Gaussian that
        # makes bg, in pixel noise, the largest of the channels. About 1 on flat sky (99 % of a dark field's set under 1.7 to
        # 1.9); 3.5 to 5 at the median where floor3 drew its patches in 30 Doradus and the Lagoon. In float64: the scatter
        # is a small difference of two large sums, and in float32 a level of a few thousand sigmas reads as no scatter.
        w64 = w.astype(np.float64); dn = np.maximum(gaussian_filter(w64, SBG), 1e-6); df = np.maximum(gaussian_filter(w64, 3 * SBG), 1e-6)
        spread = np.zeros(o.shape[1:], np.float64)
        for c in range(o.shape[0]):
            o64 = o[c].astype(np.float64); near = gaussian_filter(o64 * w64, SBG) / dn; far = gaussian_filter(o64 * w64, 3 * SBG) / df
            v = np.where(thin, gaussian_filter(o64 * o64 * w64, 3 * SBG) / df - far * far, gaussian_filter(o64 * o64 * w64, SBG) / dn - near * near)
            spread = np.maximum(spread, np.sqrt(np.maximum(v, 0)) / sig[c])
        trust = np.clip((UNTRUSTED - spread) / (UNTRUSTED - TRUSTED), 0, 1).astype(np.float32)
    for c in range(o.shape[0]):
        bg = gaussian_filter(o[c] * w, SBG) / np.maximum(den, 1e-6)
        bg = np.where(thin, gaussian_filter(o[c] * w, 3 * SBG) / np.maximum(den2, 1e-6), bg)
        s = gaussian_filter((o[c] - bg) * mf, SLO) / dm
        if mode == "floor": s = np.minimum(s, 0)
        if mode in ("floor3", "floor4"):   # floor2, each correction capped by what the model took there (dig_floor.py --cap-up 2 --cap-down 0.05)
            oc = np.where(m & ((o[c] - bg) < -2.5 * sig[c]), bg, o[c]); taper = np.clip(2 * gaussian_filter(mf, 1.0) - 1, 0, 1)
            s2 = gaussian_filter((oc - bg) * mf, SLO) / dm
            took = maximum_filter(np.maximum(gaussian_filter(x[c] - o[c], 1.0), 0), 5)
            up = np.clip(oc - np.minimum(s2, 0) * taper - o[c], 0, 2.0 * took)
            down = np.minimum(np.maximum(s2, 0) * taper, 0.05 * took)
            res[c] = np.where(m, o[c] + up - down, o[c]) if mode == "floor3" else np.where(m, o[c] + trust * (up - down), o[c])
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
