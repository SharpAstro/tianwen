"""#1244 (3), the zero-flux case: a starless plate through the remover must come back as the plate (2026-11-19).

    python -I zero_flux.py <store> <out dir> <model.onnx> <session substring> ...

For each session: its starless plate goes through `tianwen image remove-stars` as trained (dig-fill and core protection
off where the build has them), and the output less the plate is read per channel over the middle three quarters of the
frame, in units of the master's recorded sigma times the plate's noise ratio is NOT applied (the record is the unit):
the mean, the median, the rms, the share of pixels moved by over 1 and over 3 sigma down and up, and the rms of the
change after a 2 px Gaussian (what is left of it at the scale of a star). TIANWEN_CLI overrides the CLI.
"""
import glob, json, os, subprocess, sys
import numpy as np
from astropy.io import fits
from scipy.ndimage import gaussian_filter

S, OUT, ONNX = sys.argv[1], sys.argv[2], sys.argv[3]
CLI = os.environ.get("TIANWEN_CLI", "C:/temp/e2/sr-cli/tianwen.exe")
name = os.path.splitext(os.path.basename(ONNX))[0]
os.makedirs(OUT, exist_ok=True)
rows = {}
for line in open(f"{S}/tiles-manifest.jsonl", encoding="utf-8"):
    if '"Frame":"master"' in line and '"NoiseSigma":[' in line:
        r = json.loads(line); rows.setdefault(r["Tile"].split("/")[1], r)
help_text = subprocess.run([CLI, "image", "remove-stars", "--help"], capture_output=True, text=True, creationflags=subprocess.CREATE_NO_WINDOW).stdout
flags = [f for f in ("--no-fill-digs", "--no-protect-cores") if f in help_text]

for want in sys.argv[4:]:
    for sess in sorted(rows):
        if want.lower() not in sess.lower() or "flip" in sess:
            continue
        src = f"{S}/starless/plates/{sess}_plate.fits"; dst = f"{OUT}/{sess[:60].rstrip()}-{name}-out.fits"
        if not os.path.exists(dst):
            r = subprocess.run([CLI, "image", "remove-stars", src, "-o", dst, "--no-stars", "--views", "1", "--model", ONNX] + flags,
                               capture_output=True, text=True, creationflags=subprocess.CREATE_NO_WINDOW)
            if r.returncode != 0 or not os.path.exists(dst):
                print(f"remove-stars failed on {sess}:\n{r.stdout[-1500:]}\n{r.stderr[-1500:]}"); continue
        p = np.asarray(fits.getdata(src), np.float32); o = np.asarray(fits.getdata(dst), np.float32)
        d = max(1.0, float(fits.getheader(f"{S}/session-masters/{sess}.fits").get("DATAMAX", 1.0)))
        # the output may be on the unit scale where the plate is on the master's: put it on the plate's by the medians
        p = p[None] if p.ndim == 2 else p; o = o[None] if o.ndim == 2 else o
        C, H, W = p.shape
        print(f"{name}  {sess[:80]}")
        for c in range(C):
            a = p[c, H // 8:7 * H // 8, W // 8:7 * W // 8]; b = o[c, H // 8:7 * H // 8, W // 8:7 * W // 8]
            ok = np.isfinite(a) & np.isfinite(b)
            scale = np.median(a[ok]) / np.median(b[ok])
            s = rows[sess]["NoiseSigma"][c] * d
            v = np.where(ok, (b * (scale if abs(scale - 1) > 0.01 else 1.0) - a) / s, 0.0)
            sm = gaussian_filter(v, 2)
            print(f"   ch{c} scale {scale:.4f} mean {v[ok].mean():+.3f} median {np.median(v[ok]):+.3f} rms {np.sqrt((v[ok] ** 2).mean()):.3f}"
                  f" | down over 1: {100 * (v[ok] < -1).mean():.2f}% over 3: {100 * (v[ok] < -3).mean():.3f}% | up over 1: {100 * (v[ok] > 1).mean():.2f}% over 3: {100 * (v[ok] > 3).mean():.3f}%"
                  f" | smoothed rms {np.sqrt((sm[ok] ** 2).mean()):.3f}", flush=True)
