"""The remover's plate of a session's master, as trained, for the ring reads at real sites (site-run.sh), and the
output less the truth plate in rings round each catalogued star.

    SITE_OUT=<a folder of this model's own> python -I site_remove.py <onnx> <session stem> [<session stem> ...]

It writes SITE_OUT/<tag>-real-out.fits, tag being the stem up to its first '_', and skips the remover when that file is
there: models are kept apart by SITE_OUT being one folder per model (site-run.sh sets it), not by the file's name.
site_core.py and site_core_ch.py read it. This is transplant.py's real-site half only; the moved star layer is
transplant.py's.
"""
import csv, functools, os, subprocess, sys
import numpy as np
from astropy.io import fits

STORE = os.environ.get("TIANWEN_DATASET", "D:/Astro-Dataset/2026-10-12-full")
OUT = os.environ["SITE_OUT"]
CLI = os.environ.get("TIANWEN_CLI", "C:/temp/e2/sr-cli/tianwen.exe")
RINGS = [(0, 2), (2, 4), (4, 8), (8, 16)]
BANDS = [("20-100", 20, 100), ("100-1000", 100, 1000), (">=1000", 1000, 1e9)]
onnx, stems = sys.argv[1], sys.argv[2:]
os.makedirs(OUT, exist_ok=True)


@functools.cache  # lazily, once per run: a run whose outputs all exist never needs the CLI
def as_trained_flags():
    """The options that switch off the dig-fill and core protection, where this build has them: every row this script
    gives is the model AS TRAINED. A build from before those steps has neither option and needs neither. The same
    question transplant.py asks; asked of the build once, where it used to be asked once per flag for every plate."""
    text = subprocess.run([CLI, "image", "remove-stars", "--help"], capture_output=True, text=True, creationflags=subprocess.CREATE_NO_WINDOW).stdout
    return [f for f in ("--no-fill-digs", "--no-protect-cores") if f in text]



def remove(src, dst):
    if not os.path.exists(dst):
        r = subprocess.run([CLI, "image", "remove-stars", src, "-o", dst, "--no-stars", "--views", "1", "--model", onnx]
                           + as_trained_flags(),
                           capture_output=True, text=True, creationflags=subprocess.CREATE_NO_WINDOW)
        if r.returncode != 0:
            raise SystemExit(f"remove-stars failed on {src}:\n{r.stdout[-2000:]}\n{r.stderr[-2000:]}")
    return np.asarray(fits.getdata(dst), dtype=np.float32)


yy, xx = np.mgrid[-16:17, -16:17]
rr = np.hypot(yy, xx)
masks = [(rr >= a) & (rr < b) for a, b in RINGS]
for stem in stems:
    tag = stem.split("_")[0]
    with fits.open(f"{STORE}/session-masters/{stem}.fits") as h:
        master = np.asarray(h[0].data, dtype=np.float32)
    plate = np.asarray(fits.getdata(f"{STORE}/starless/plates/{stem}_plate.fits"), dtype=np.float32)
    layer = np.nan_to_num(master - plate, nan=0.0)
    out_a = remove(f"{STORE}/session-masters/{stem}.fits", f"{OUT}/{tag}-real-out.fits")
    stars = [r for r in csv.DictReader(open(f"{STORE}/starless/plates/{stem}_plate.stars.csv", encoding="utf-8"))]
    print(f"\n{stem}\n  master {master.shape}, median {np.nanmedian(master):.5f}, plate median {np.nanmedian(plate):.5f}, "
          f"layer share over 0 {100 * (layer > 0).mean():.1f} %, {len(stars)} catalogued")
    H, W = master.shape[1:]
    err = np.nanmean(out_a - plate, axis=0)
    print("  real site (shift 0)")
    print(f"    {'band':10s} {'n':>6s}" + "".join(f"   r{a}-{b}: med  dug% left%" for a, b in RINGS))
    for bname, lo, hi in BANDS:
        rows = [[] for _ in RINGS]
        for s in stars:
            sg = float(s["significance"])
            if not (lo <= sg < hi) or s["saturated"] == "1" or s["second_pass"] != "0":
                continue
            x, y = int(round(float(s["x"]))), int(round(float(s["y"])))
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
