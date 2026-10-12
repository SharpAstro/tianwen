"""R9 (#1056): score a held-out set's stacks, shipped sharpenings and refined outputs against their truths (G1, G3 of RULES.md).

The band errors are the CLI's own (`tianwen planetary fidelity`, R8's measure); python only runs it and reads the lines, and measures
G3's power past 0.9 of the pupil's cutoff, or of Nyquist where that is lower, on the disk (a Hann window over 1.1 radii).

  python r9_score.py --tianwen EXE --pairs DIR --refined DIR --psf TWIN.psf [--aperture 0.254] --out scores.csv
      [--g3-from scores.csv]   (G3 read again over a scoring's rows, the band errors kept)
"""
import argparse, csv, os, re, struct, subprocess
import numpy as np
from astropy.io import fits

LINE = re.compile(r"\] (\S+) against the truth: transfer ([^;]+); error ([^(]+) \(bands 1 to 4 ([0-9.]+)\)")
LIMB = re.compile(r"\] (\S+) against the truth: .*limb profile error ([0-9.]+)")
FIELDS = ["index", "stack", "sharp", "refined", *[f"sharp_b{b}" for b in range(1, 5)], *[f"refined_b{b}" for b in range(1, 5)],
          "past_cutoff_sharp", "past_cutoff_refined", "past_cutoff_truth", "limb_sharp", "limb_refined", "trough_sharp", "trough_refined"]
TROUGH = re.compile(r"\] (\S+): the limb's trough below the truth ([0-9.]+)")


def psf_scale(path):
    with open(path, "rb") as f:
        f.read(8)
        grid, oversample = struct.unpack("<ii", f.read(8))
        arcsec, wavelength = struct.unpack("<dd", f.read(16))
    return arcsec, wavelength


def past_cutoff(image, disk, band):
    """The share of the disk's power (sky taken off, a Hann window over 1.1 radii) past the band's edge, cycles a pixel."""
    x0, y0, r = disk
    yy, xx = np.indices(image.shape)
    rho = np.hypot(xx - x0, yy - y0) / r
    window = np.where(rho < 1.1, 0.5 * (1 + np.cos(np.pi * rho / 1.1)), 0.0)
    sky = np.median(image[rho > 1.4]) if (rho > 1.4).sum() > 50 else 0.0
    f = np.fft.fftshift(np.fft.fft2((image - sky) * window))
    p = np.abs(f) ** 2
    fy = np.fft.fftshift(np.fft.fftfreq(image.shape[0]))[:, None]
    fx = np.fft.fftshift(np.fft.fftfreq(image.shape[1]))[None, :]
    k = np.hypot(fx, fy)
    return float(p[k > band].sum() / p.sum())


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--tianwen", required=True); ap.add_argument("--pairs", required=True); ap.add_argument("--refined", required=True)
    ap.add_argument("--psf", required=True); ap.add_argument("--aperture", type=float, default=0.254); ap.add_argument("--out", required=True)
    ap.add_argument("--limit", type=int, default=0)
    ap.add_argument("--g3-from", default=None, help="a scoring's CSV: keep its band errors, read G3 again")
    ap.add_argument("--resume", action="store_true", help="keep the rows --out already holds and score only the pairs it lacks")
    a = ap.parse_args()
    arcsec, wavelength = psf_scale(a.psf)
    cutoff = a.aperture / wavelength * arcsec * np.pi / 648000  # cycles a pixel
    # A twin is sampled coarser than its optics resolve (cutoff 0.94 cycles a pixel on the R8 twins), so the band past 0.9 of the cutoff
    # would lie past Nyquist and read zero; it is read past 0.9 of the lesser of the two (RULES.md, 2026-10-13).
    band = 0.9 * min(cutoff, 0.5)
    rows = []
    with open(os.path.join(a.pairs, "pairs.csv")) as f:
        ids = [r["index"] for r in csv.DictReader(f) if r.get("size")]
    if a.limit:
        ids = ids[: a.limit]
    kept = {}
    if a.g3_from:
        with open(a.g3_from) as f:
            kept = {r["index"]: r for r in csv.DictReader(f)}
        ids = [i for i in ids if i in kept]
    # Each row is written as it is scored, so a scoring stopped part way keeps what it read (--resume carries on from it).
    resuming = a.resume and not a.g3_from and os.path.exists(a.out)
    if resuming:
        with open(a.out) as f:
            rows = [{k: (v if k == "index" else float(v)) for k, v in r.items()} for r in csv.DictReader(f)]
    done = {r["index"] for r in rows}
    sink = None if a.g3_from else open(a.out, "a" if resuming else "w", newline="")
    writer = None if sink is None else csv.DictWriter(sink, fieldnames=FIELDS)
    if writer is not None and not resuming:
        writer.writeheader()
    for i in ids:
        if i in done:
            continue
        stem = os.path.join(a.pairs, f"{int(i):05d}")
        if i in kept:
            r = {k: (v if k == "index" else float(v)) for k, v in kept[i].items()}
            h = fits.getheader(stem + "_truth.fits")
            disk = (float(h["DISKX"]), float(h["DISKY"]), float(h["DISKR"]))
            for k, p in (("sharp", stem + "_sharp.fits"), ("refined", os.path.join(a.refined, f"{int(i):05d}_refined.fits")), ("truth", stem + "_truth.fits")):
                r[f"past_cutoff_{k}"] = past_cutoff(fits.getdata(p).astype(float), disk, band)
            for k in ("limb_sharp", "limb_refined", "trough_sharp", "trough_refined"):
                r.setdefault(k, np.nan)
            rows.append(r)
            continue
        refined = os.path.join(a.refined, f"{int(i):05d}_refined.fits")
        truth = stem + "_truth.fits"
        out = subprocess.run([a.tianwen, "planetary", "fidelity", stem + "_stack.fits", stem + "_sharp.fits", refined, "--truth", truth],
                             capture_output=True, text=True).stdout
        kind_of = lambda name: "stack" if name.endswith("_stack.fits") else "sharp" if name.endswith("_sharp.fits") else "refined"
        scores, limb, trough = {}, {}, {}
        for m in LINE.finditer(out):
            scores[kind_of(m.group(1))] = (float(m.group(4)), [float(v) for v in m.group(3).split(",")])
        for m in LIMB.finditer(out):
            limb[kind_of(m.group(1))] = float(m.group(2))
        for m in TROUGH.finditer(out):
            trough[kind_of(m.group(1))] = float(m.group(2))
        if len(scores) < 3:
            continue
        h = fits.getheader(truth)
        disk = (float(h["DISKX"]), float(h["DISKY"]), float(h["DISKR"]))
        power = {k: past_cutoff(fits.getdata(p).astype(float), disk, band) for k, p in
                 (("sharp", stem + "_sharp.fits"), ("refined", refined), ("truth", truth))}
        rows.append({"index": i, "stack": scores["stack"][0], "sharp": scores["sharp"][0], "refined": scores["refined"][0],
                     **{f"sharp_b{b + 1}": scores["sharp"][1][b] for b in range(4)},
                     **{f"refined_b{b + 1}": scores["refined"][1][b] for b in range(4)},
                     "past_cutoff_sharp": power["sharp"], "past_cutoff_refined": power["refined"], "past_cutoff_truth": power["truth"],
                     "limb_sharp": limb.get("sharp", np.nan), "limb_refined": limb.get("refined", np.nan),
                     "trough_sharp": trough.get("sharp", np.nan), "trough_refined": trough.get("refined", np.nan)})
        writer.writerow(rows[-1])
        sink.flush()
    if sink is not None:
        sink.close()
    else:
        with open(a.out, "w", newline="") as f:
            w = csv.DictWriter(f, fieldnames=FIELDS)
            w.writeheader(); w.writerows(rows)
    s = np.array([r["sharp"] for r in rows]); rf = np.array([r["refined"] for r in rows]); st = np.array([r["stack"] for r in rows])
    print(f"{len(rows)} pairs, cutoff {cutoff:.3f} cycles a pixel, G3 read past {band:.3f}")
    print(f"bands 1 to 4, mean: stack {st.mean():.3f}, shipped sharpening {s.mean():.3f}, refined {rf.mean():.3f} ({100 * (rf.mean() / s.mean() - 1):+.1f} %)")
    for b in range(4):
        sb = np.mean([r[f"sharp_b{b + 1}"] for r in rows]); rb = np.mean([r[f"refined_b{b + 1}"] for r in rows])
        print(f"  band {b + 1}: shipped {sb:.3f}, refined {rb:.3f} ({100 * (rb / sb - 1):+.1f} %)")
    ps = np.mean([r["past_cutoff_sharp"] for r in rows]); pr = np.mean([r["past_cutoff_refined"] for r in rows])
    print(f"power past the G3 band: shipped {ps:.3e}, refined {pr:.3e} (ratio {pr / ps:.3f}), truth {np.mean([r['past_cutoff_truth'] for r in rows]):.3e}")
    print(f"limb profile error (G4): shipped {np.nanmean([r['limb_sharp'] for r in rows]):.4f}, refined {np.nanmean([r['limb_refined'] for r in rows]):.4f}; "
          f"trough below the truth: shipped {np.nanmean([r['trough_sharp'] for r in rows]):.4f}, refined {np.nanmean([r['trough_refined'] for r in rows]):.4f}")
    for r in rows if len(rows) <= 20 else []:
        print(f"  {r['index']}: stack {r['stack']:.3f}, shipped {r['sharp']:.3f}, refined {r['refined']:.3f}")
    print(f"refined better than shipped on {int((rf < s).sum())} of {len(rows)} pairs")


if __name__ == "__main__":
    main()
