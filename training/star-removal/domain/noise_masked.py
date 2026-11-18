"""#1786: the master's block noise read only away from its catalogued stars (2026-11-19).

    python -I noise_masked.py <store> <session substring> ...

Per channel, the block estimate as StretchedNoise reads it but in linear (32 px blocks, the frame less its 4 px Gaussian
low-pass, 1.4826 MAD per block, the lower quartile over blocks), three ways: over every pixel of the master (what the
record holds), over the master's pixels outside every catalogued star's footprint (a block needs 300 such pixels), and
over every pixel of the plate. Each over the first. A star's footprint is a disc of radius
1.5 x fwhm x width x sqrt(log2(4 x significance)) px, at least 3 and at most 40, where fwhm is the widest channel's of the
field profile and width the star's own catalogued factor. A Gaussian of that FWHM falls under a quarter sigma at a third of
that radius, so the disc is three times as wide: the wings of a real star reach further than a Gaussian's.
"""
import csv, json, sys
import numpy as np
from astropy.io import fits
from scipy.ndimage import gaussian_filter

S = sys.argv[1]
rows = {}
for line in open(f"{S}/tiles-manifest.jsonl", encoding="utf-8"):
    if '"Frame":"master"' in line and '"NoiseSigma":[' in line:
        r = json.loads(line); rows.setdefault(r["Tile"].split("/")[1], r)


def blocks(hp, keep, need):
    H, W = hp.shape; out = []
    for y in range(0, H - 31, 32):
        for x in range(0, W - 31, 32):
            k = keep[y:y + 32, x:x + 32]
            if k.sum() >= need:
                v = hp[y:y + 32, x:x + 32][k]
                out.append(1.4826 * np.median(np.abs(v - np.median(v))))
    return np.percentile(out, 25), len(out)


for want in sys.argv[2:]:
    for sess in sorted(rows):
        if want.lower() not in sess.lower() or "flip" in sess:
            continue
        m = np.asarray(fits.getdata(f"{S}/session-masters/{sess}.fits"), np.float32)
        p = np.asarray(fits.getdata(f"{S}/starless/plates/{sess}_plate.fits"), np.float32)
        prof = json.load(open(f"{S}/starless/plates/{sess}_plate.profile.json"))
        m = m[None] if m.ndim == 2 else m; p = p[None] if p.ndim == 2 else p
        C, H, W = m.shape
        fwhm = max(2 * c["Alpha"] * np.sqrt(2 ** (1 / c["Beta"]) - 1) for c in prof["Channels"])
        star = np.zeros((H, W), bool); n = 0
        yy, xx = np.mgrid[-40:41, -40:41]; d2 = yy * yy + xx * xx
        for s in csv.DictReader(open(f"{S}/starless/plates/{sess}_plate.stars.csv", encoding="utf-8")):
            x, y, sg = int(round(float(s["x"]))), int(round(float(s["y"]))), max(1.0, float(s["significance"]))
            rad = min(40.0, max(3.0, 1.5 * fwhm * float(s["width"]) * np.sqrt(np.log2(4 * sg))))
            y0, y1, x0, x1 = max(0, y - 40), min(H, y + 41), max(0, x - 40), min(W, x + 41)
            if y0 < y1 and x0 < x1:
                star[y0:y1, x0:x1] |= d2[y0 - y + 40:y1 - y + 40, x0 - x + 40:x1 - x + 40] <= rad * rad; n += 1
        print(f"{sess[:90]}  stars {n}, fwhm {fwhm:.2f}, footprints cover {100 * star.mean():.1f}%")
        for c in range(C):
            ok = np.isfinite(m[c]) & np.isfinite(p[c]) & (m[c] != 0)
            fm = np.where(ok, m[c], np.nanmedian(m[c])); fp = np.where(ok, p[c], np.nanmedian(p[c]))
            hm = fm - gaussian_filter(fm, 4); hpl = fp - gaussian_filter(fp, 4)
            a, na = blocks(hm, ok, 1024); b, nb = blocks(hm, ok & ~star, 300); q, nq = blocks(hpl, ok, 1024)
            print(f"   ch{c} master, away from its stars: {b / a:.3f} ({nb} of {na} blocks)   plate: {q / a:.3f}", flush=True)
