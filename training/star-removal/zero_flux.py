"""#1244 (3), the zero-flux case: a starless plate through the remover must come back as the plate (2026-11-19).

    python -I zero_flux.py <out dir> <model.onnx> <session substring> ...

For each session of the store: its starless plate goes through `tianwen image remove-stars` as trained (dig-fill and
core protection off where the build has them), and the output less the plate is read per channel over the middle three
quarters of the frame. The unit is the master's recorded sigma (the manifest's NoiseSigma, on the unit scale) times the
exporter's unit divisor, max(1, the master's DATAMAX) (`DatasetTileExporter.UnitDivisor`), which puts it on the plate's
scale. Printed: the ratio of the plate's median to the output's (the model's own level shift; it is NOT applied), the
mean, the median and the rms of the change, the share of pixels moved by over 1 and over 3 sigma down and up, and the
rms after a 2 px Gaussian (what is left of the change at the scale of a star).

An output is kept per session, model file name and flag set under <out dir> and reused; it is NOT keyed by the CLI
build, so give a new build a new <out dir>. Keep <out dir> short: the longest session stem leaves about 40 characters
of it under Windows' 260. TIANWEN_SR_STORE and TIANWEN_SR_CLI
override the store and the CLI, as in crowd_texture.py.
"""
import json, os, subprocess, sys
import numpy as np
from astropy.io import fits
from scipy.ndimage import gaussian_filter

OUT, ONNX = sys.argv[1], sys.argv[2]
S = os.environ.get("TIANWEN_SR_STORE", "D:/Astro-Dataset/2026-10-12-full")
CLI = os.environ.get("TIANWEN_SR_CLI", "tianwen")
name = os.path.splitext(os.path.basename(ONNX))[0]
os.makedirs(OUT, exist_ok=True)
rows = {}
for line in open(f"{S}/tiles-manifest.jsonl", encoding="utf-8"):
    if '"Frame":"master"' in line and '"NoiseSigma":[' in line:
        r = json.loads(line); rows.setdefault(r["Tile"].split("/")[1], r)
help_text = subprocess.run([CLI, "image", "remove-stars", "--help"], capture_output=True, text=True, creationflags=subprocess.CREATE_NO_WINDOW).stdout
flags = [f for f in ("--no-fill-digs", "--no-protect-cores", "--keep-overshoot") if f in help_text]
tag = "".join(f.replace("--", "-") for f in flags) or "-plain"


def output_path(sess):
    return f"{OUT}/{sess}-{name}{tag}-out.fits"


if __name__ == "__main__":
    for want in sys.argv[3:]:
        for sess in sorted(rows):
            if want.lower() not in sess.lower() or "flip" in sess:
                continue
            src = f"{S}/starless/plates/{sess}_plate.fits"; dst = output_path(sess)
            if not os.path.exists(dst):
                r = subprocess.run([CLI, "image", "remove-stars", src, "-o", dst, "--no-stars", "--views", "1", "--model", ONNX] + flags,
                                   capture_output=True, text=True, creationflags=subprocess.CREATE_NO_WINDOW)
                if r.returncode != 0 or not os.path.exists(dst):
                    print(f"remove-stars failed on {sess}:\n{r.stdout[-1500:]}\n{r.stderr[-1500:]}"); continue
            p = np.asarray(fits.getdata(src), np.float32); o = np.asarray(fits.getdata(dst), np.float32)
            d = max(1.0, float(fits.getheader(f"{S}/session-masters/{sess}.fits").get("DATAMAX", 1.0)))
            p = p[None] if p.ndim == 2 else p; o = o[None] if o.ndim == 2 else o
            C, H, W = p.shape
            print(f"{name}  {sess[:80]}  (unit divisor {d:.4f})")
            for c in range(C):
                a = p[c, H // 8:7 * H // 8, W // 8:7 * W // 8]; b = o[c, H // 8:7 * H // 8, W // 8:7 * W // 8]
                ok = np.isfinite(a) & np.isfinite(b)
                s = rows[sess]["NoiseSigma"][c] * d
                v = np.where(ok, (b - a) / s, 0.0)
                sm = gaussian_filter(v, 2)
                print(f"   ch{c} plate median over output median {np.median(a[ok]) / np.median(b[ok]):.4f} mean {v[ok].mean():+.3f} median {np.median(v[ok]):+.3f} rms {np.sqrt((v[ok] ** 2).mean()):.3f}"
                      f" | down over 1: {100 * (v[ok] < -1).mean():.2f}% over 3: {100 * (v[ok] < -3).mean():.3f}% | up over 1: {100 * (v[ok] > 1).mean():.2f}% over 3: {100 * (v[ok] > 3).mean():.3f}%"
                      f" | smoothed rms {np.sqrt((sm[ok] ** 2).mean()):.3f}", flush=True)
