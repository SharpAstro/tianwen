"""The transplant test (2026-11-16): is a real star dug because of its own light, or because of where it sits?

The master is the starless plate plus a star layer (master less plate). Run the remover on the master as it is (shift 0)
and on the plate with the SAME star layer moved by (37, 53) px, so every real star's light sits on background that never
held a star. The plate is the truth both times. Per catalogued star, by significance band: the output less the plate in
rings round the star, in the star's own catalogued noise sigma.

    TIANWEN_DATASET, TIANWEN_TRANSPLANT (output folder) and TIANWEN_CLI (the tianwen.exe) override the DE1 defaults.

    python -I transplant.py <onnx> <session stem> [<session stem> ...]
"""
import csv, hashlib, os, subprocess, sys
import numpy as np
from astropy.io import fits

STORE = os.environ.get("TIANWEN_DATASET", "D:/Astro-Dataset/2026-10-12-full")
OUT = os.environ.get("TIANWEN_TRANSPLANT", "D:/temp/tianwen-scratch/transplant")
CLI = os.environ.get("TIANWEN_CLI", "C:/temp/e2/sr-cli/tianwen.exe")
DY, DX = 37, 53
RINGS = [(0, 2), (2, 4), (4, 8), (8, 16)]
BANDS = [("20-100", 20, 100), ("100-1000", 100, 1000), (">=1000", 1000, 1e9)]
onnx, stems = sys.argv[1], sys.argv[2:]
os.makedirs(OUT, exist_ok=True)


def as_trained_flags():
    """The options that switch off the dig-fill and core protection, where this build has them: every row these scripts
    give is the model AS TRAINED. A build from before those steps has neither option and needs neither."""
    text = subprocess.run([CLI, "image", "remove-stars", "--help"], capture_output=True, text=True, creationflags=subprocess.CREATE_NO_WINDOW).stdout
    return [f for f in ("--no-fill-digs", "--no-protect-cores", "--keep-overshoot") if f in text]


MODEL = os.path.splitext(os.path.basename(onnx))[0]


def remove(src, dst):
    if not os.path.exists(dst):
        r = subprocess.run([CLI, "image", "remove-stars", src, "-o", dst, "--no-stars", "--views", "1", "--model", onnx] + as_trained_flags(),
                           capture_output=True, text=True, creationflags=subprocess.CREATE_NO_WINDOW)
        if r.returncode != 0:
            raise SystemExit(f"remove-stars failed on {src}:\n{r.stdout[-2000:]}\n{r.stderr[-2000:]}")
    return np.asarray(fits.getdata(dst), dtype=np.float32)


yy, xx = np.mgrid[-16:17, -16:17]
rr = np.hypot(yy, xx)
masks = [(rr >= a) & (rr < b) for a, b in RINGS]
for stem in stems:
    tag = stem.split("_")[0] + "-" + hashlib.sha1(stem.encode("utf-8")).hexdigest()[:6]  # stable across runs
    print(f"tag {tag}  (the tag argument of site_core.py / site_core_ch.py)")
    with fits.open(f"{STORE}/session-masters/{stem}.fits") as h:
        master = np.asarray(h[0].data, dtype=np.float32); header = h[0].header
    plate = np.asarray(fits.getdata(f"{STORE}/starless/plates/{stem}_plate.fits"), dtype=np.float32)
    layer = np.nan_to_num(master - plate, nan=0.0)
    moved = plate + np.roll(layer, (DY, DX), axis=(1, 2))
    src_b = f"{OUT}/{tag}-moved.fits"
    if not os.path.exists(src_b):
        fits.PrimaryHDU(moved, header).writeto(src_b)
    out_a = remove(f"{STORE}/session-masters/{stem}.fits", f"{OUT}/{tag}-real-{MODEL}-out.fits")
    out_b = remove(src_b, f"{OUT}/{tag}-moved-{MODEL}-out.fits")
    stars = [r for r in csv.DictReader(open(f"{STORE}/starless/plates/{stem}_plate.stars.csv", encoding="utf-8"))]
    print(f"\n{stem}\n  master {master.shape}, median {np.nanmedian(master):.5f}, plate median {np.nanmedian(plate):.5f}, "
          f"layer share over 0 {100 * (layer > 0).mean():.1f} %, {len(stars)} catalogued")
    H, W = master.shape[1:]
    for name, out, dy, dx in (("real site (shift 0)", out_a, 0, 0), (f"moved ({DY},{DX})", out_b, DY, DX)):
        err = np.nanmean(out - plate, axis=0)
        print(f"  {name}")
        print(f"    {'band':10s} {'n':>6s}" + "".join(f"   r{a}-{b}: med  dug% left%" for a, b in RINGS))
        for bname, lo, hi in BANDS:
            rows = [[] for _ in RINGS]
            for s in stars:
                sg = float(s["significance"])
                if not (lo <= sg < hi) or s["saturated"] == "1" or s["second_pass"] != "0":
                    continue
                x, y = int(round(float(s["x"]))) + dx, int(round(float(s["y"]))) + dy
                if not (80 <= x < W - 80 and 80 <= y < H - 80):
                    continue
                st = err[y - 16:y + 17, x - 16:x + 17]
                if not np.isfinite(st).all():
                    continue
                sig = float(s["sigma"]) / np.sqrt(3.0)
                for i, m in enumerate(masks):
                    rows[i].append(st[m].mean() / sig)
            n = len(rows[0])
            if not n:
                continue
            line = f"    {bname:10s} {n:6d}"
            for v in rows:
                v = np.array(v)
                line += f"   {np.median(v):12.2f} {100 * (v < -1).mean():5.1f} {100 * (v > 1).mean():5.1f}"
            print(line, flush=True)
