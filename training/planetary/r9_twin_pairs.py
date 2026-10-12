"""R9 (#1056): gather finished twins (planetary degrade, stack --truth, sharpen) into the refiner's pair layout: <index>_stack.fits (the
master), <index>_sharp.fits (its derived sharpening), <index>_truth.fits (the twin's truth, DISKX/Y/R in its header), and pairs.csv.
A master is built on its stack's reference frame, 0.1 to 6 px off its truth, which the CLI's scorer undoes by a limb fit; a pixel loss
would not, so each truth is moved onto its master here (the cross-correlation's sub-pixel peak against the derived sharpening, then a
Fourier shift), its DISKX and DISKY with it.

  python r9_twin_pairs.py --twins DIR --plan plan.json --out DIR [--held] [--exclude-map M] [--only-map M]
      (training twins by default, the plan's held-out ones with --held; a map left out of training, or only that map's twins)
"""
import argparse, json, os, shutil
import numpy as np
from astropy.io import fits


def offset(a, b):
    """Where a lies against b: the cross-correlation's peak, refined by a parabola in each axis."""
    c = np.fft.ifft2(np.fft.fft2(a - a.mean()) * np.conj(np.fft.fft2(b - b.mean()))).real
    h, w = c.shape
    iy, ix = np.unravel_index(np.argmax(c), c.shape)
    sub = lambda m, z, p: 0.5 * (m - p) / (m - 2 * z + p)
    dy = sub(c[(iy - 1) % h, ix], c[iy, ix], c[(iy + 1) % h, ix]); dx = sub(c[iy, (ix - 1) % w], c[iy, ix], c[iy, (ix + 1) % w])
    return (ix - w if ix > w // 2 else ix) + dx, (iy - h if iy > h // 2 else iy) + dy


def shifted(image, dx, dy):
    """image moved by (dx, dy) px, by the Fourier shift theorem (the frame wraps; a truth's sky is black far out)."""
    fy = np.fft.fftfreq(image.shape[0])[:, None]; fx = np.fft.fftfreq(image.shape[1])[None, :]
    return np.fft.ifft2(np.fft.fft2(image) * np.exp(-2j * np.pi * (fx * dx + fy * dy))).real


def register_truth(sharp_path, truth_path, out_path):
    d = fits.getdata(sharp_path).astype(float)
    t = fits.getdata(truth_path).astype(float)
    h = fits.getheader(truth_path)
    x0, y0, r = float(h["DISKX"]), float(h["DISKY"]), float(h["DISKR"])
    sl = (slice(int(y0 - 1.3 * r), int(y0 + 1.3 * r)), slice(int(x0 - 1.3 * r), int(x0 + 1.3 * r)))
    dx, dy = offset(d[sl], t[sl])
    h["DISKX"], h["DISKY"] = x0 + dx, y0 + dy
    h["TRUTHDX"], h["TRUTHDY"] = (round(float(dx), 4), "moved onto the master, px"), (round(float(dy), 4), "moved onto the master, px")
    fits.writeto(out_path, shifted(t, dx, dy).astype(np.float32), h, overwrite=True)
    return dx, dy


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--twins", required=True); ap.add_argument("--plan", required=True); ap.add_argument("--out", required=True)
    ap.add_argument("--held", action="store_true"); ap.add_argument("--extra", nargs="*", default=[], help="other twin names to include")
    ap.add_argument("--exclude-map", nargs="*", default=[]); ap.add_argument("--only-map", nargs="*", default=[])
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)
    plan = json.load(open(a.plan))
    names = [f"t{r['index'] + 1:03d}" for r in plan if (bool(r["held"]) == a.held or a.only_map) and r["map"] not in a.exclude_map
             and (not a.only_map or r["map"] in a.only_map)] + a.extra
    rows = []
    for k, n in enumerate(names):
        d = os.path.join(a.twins, n)
        files = (os.path.join(d, f"master_{n}.fits"), os.path.join(d, "derived", f"master_{n}_sharpened.fits"), os.path.join(d, f"{n}.truth.fits"))
        if not all(os.path.exists(f) for f in files):
            continue
        for src, kind in zip(files[:2], ("stack", "sharp")):
            shutil.copyfile(src, os.path.join(a.out, f"{k:05d}_{kind}.fits"))
        dx, dy = register_truth(files[1], files[2], os.path.join(a.out, f"{k:05d}_truth.fits"))
        rows.append(f"{k},{n},,,,,,,,,1")
        print(f"{n}: its truth moved {dx:+.2f}, {dy:+.2f} px onto the master")
    with open(os.path.join(a.out, "pairs.csv"), "w") as f:
        f.write("index,map,psf,when,radius_px,north_deg,keep,broaden_px,finest_snr,noise_sigma,size\n")
        f.write("\n".join(rows) + "\n")
    print(f"{len(rows)} twins gathered into {a.out}")


if __name__ == "__main__":
    main()
