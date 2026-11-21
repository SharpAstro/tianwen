"""Where the catalogue step's compact peaks lie about a clipped star's flat top, and which it took for stars (#2373).

    python -I core_stars_where.py <stem>.corestars.json <master.fits> <out.png> [core number, from 0]

Reads what `CoreMaskProbe --core-stars` wrote (CoreStars.Find's own peaks as it judged them, the catalogued stars on the
cores, and each core's flat top as the mask read it) and prints every peak and every catalogued star within REACH px of
the top's centre, with its distance in px and in top radii. Draws the crop at 6x: the master by an asinh stretch, the
top's circle (yellow), the peaks (green box: taken for a catalogued star, red box: kept with the core) and the catalogued
stars (cyan cross, magnitude beside). Without a core number it takes the first core whose top is a star's, else the first.
Nothing here is measured again: the judgement is the library's, this prints and draws it.
"""
import json
import sys

import numpy as np
from astropy.io import fits
from PIL import Image, ImageDraw

REACH = 48
ZOOM = 6


def main():
    found_path, master_path, out = sys.argv[1:4]
    found = json.load(open(found_path, encoding="utf-8"))
    cores = found["cores"]
    if len(sys.argv) > 4:
        core = cores[int(sys.argv[4])]
    else:
        core = next((c for c in cores if c["HasStarTop"]), cores[0])
    if core["FlatTopPx"] <= 0:
        raise SystemExit(f"the core at ({core['X']}, {core['Y']}) has no flat top the mask could read")
    cx, cy, radius = core["flatTopX"], core["flatTopY"], float(np.sqrt(core["FlatTopPx"] / np.pi))
    sigma, rep = found["sigma"], found["report"]
    print(found["stem"])
    print(f"  {found['source']}; zero point {rep['ZeroPoint']:.2f} (scatter {rep['ZeroPointScatter']:.2f}) over "
          f"{rep['CalibrationStars']} stars; {rep['Stars']} of {rep['Peaks']} peaks are catalogued stars")
    print(f"  flat top: centre ({cx:.1f}, {cy:.1f}), {core['FlatTopPx']} px, radius {radius:.2f} px, "
          f"{'a star' if core['HasStarTop'] else 'not a star'}'s by the mask's bars; the core is {core['CoreAreaPx']} px")

    def near(items, x, y):
        rows = [(float(np.hypot(i[x] - cx, i[y] - cy)), i) for i in items]
        return sorted([t for t in rows if t[0] <= REACH], key=lambda t: t[0])

    peaks, stars = near(found["peaks"], "X", "Y"), near(found["starsOnCores"], "x", "y")
    print(f"  compact peaks within {REACH} px of the top's centre: {len(peaks)}")
    for d, p in peaks:
        seen = rep["ZeroPoint"] - 2.5 * np.log10(p["heightSigma"] * sigma)
        gaps = [(float(np.hypot(s["x"] - p["X"], s["y"] - p["Y"])), s) for _, s in stars]
        gap = ""
        if gaps:
            away, star = min(gaps, key=lambda t: t[0])
            gap = f"; nearest catalogued star {away:.1f} px away, magnitude {star['magnitude']:.2f}"
        print(f"    ({p['X']}, {p['Y']}) {d:5.1f} px = {d / radius:4.1f} radii, {p['heightSigma']:10.1f} sigma, reads magnitude {seen:5.2f}, "
              f"{'STAR' if p['IsStar'] else 'kept'}, radius {p['Radius']}{gap}")
    print(f"  catalogued stars within {REACH} px of the top's centre: {len(stars)}")
    for d, s in stars:
        print(f"    ({s['x']:.1f}, {s['y']:.1f}) {d:5.1f} px = {d / radius:4.1f} radii, magnitude {s['magnitude']:.2f}")

    data = np.asarray(fits.getdata(master_path), np.float32)
    if data.ndim == 3:
        data = data.mean(axis=0)
    x0, y0 = int(round(cx)) - REACH, int(round(cy)) - REACH
    crop = data[max(y0, 0):y0 + 2 * REACH + 1, max(x0, 0):x0 + 2 * REACH + 1]
    x0, y0 = max(x0, 0), max(y0, 0)
    lo, hi = float(np.nanmedian(crop)), float(np.nanmax(crop))
    view = np.arcsinh(np.clip((crop - lo) / max(hi - lo, 1e-12), 0, 1) * 50) / np.arcsinh(50)
    img = Image.fromarray((255 * np.nan_to_num(view)).astype(np.uint8)).resize((crop.shape[1] * ZOOM, crop.shape[0] * ZOOM), Image.NEAREST).convert("RGB")
    draw = ImageDraw.Draw(img)

    def at(x, y):
        return ((x - x0 + 0.5) * ZOOM, (y - y0 + 0.5) * ZOOM)

    px, py = at(cx, cy)
    draw.ellipse([px - radius * ZOOM, py - radius * ZOOM, px + radius * ZOOM, py + radius * ZOOM], outline=(255, 255, 0))
    for _, p in peaks:
        qx, qy = at(p["X"], p["Y"])
        draw.rectangle([qx - ZOOM / 2 - 2, qy - ZOOM / 2 - 2, qx + ZOOM / 2 + 2, qy + ZOOM / 2 + 2], outline=(0, 255, 0) if p["IsStar"] else (255, 60, 60))
    for _, s in stars:
        qx, qy = at(s["x"], s["y"])
        draw.line([qx - 7, qy, qx + 7, qy], fill=(0, 255, 255))
        draw.line([qx, qy - 7, qx, qy + 7], fill=(0, 255, 255))
        draw.text((qx + 9, qy - 5), f"{s['magnitude']:.1f}", fill=(0, 255, 255))
    draw.text((6, 6), "yellow: the flat top; cyan +: catalogued star (magnitude); green box: peak taken for a star; red box: peak kept",
              fill=(255, 255, 255))
    img.save(out)
    print(f"  wrote {out} ({img.size[0]}x{img.size[1]}, {ZOOM}x, {crop.shape[1]} px across)")


main()
