"""#1244 (3): where on a plate the remover takes light, against the catalogue's outcomes (2026-11-19).

    python -I zero_flux_where.py <out dir> <model.onnx> <session substring>

Reads the output zero_flux.py kept for the session and model. The change (output less plate, the channels' mean, in the
master's recorded sigma as zero_flux.py takes it) is cut at 3 sigma down and split by what lies within 40 px: a
catalogued star the builder did not subtract (any outcome but Subtracted), then a subtracted saturated star, then a
subtracted star of 100 sigma or more, then nothing. Printed: the regions and pixels, each class's share of the pixels
and of the light taken, and the five largest regions.
"""
import csv, os, sys
import numpy as np
from astropy.io import fits
from scipy.ndimage import binary_dilation, label

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # -I leaves the script's folder off the path
import zero_flux as z  # its store, CLI flags and output names

want = sys.argv[3]
# One session, said out loud. zero_flux.py runs every session a substring fits; this script reads ONE, and taking the
# first sorted match in silence read the wrong one ('Great-Orion-Nebula_2022-12-03' fits a QHY178M session ahead of the
# ASI533MC one). An exact name wins; anything else must fit exactly one session.
matches = [s for s in sorted(z.rows) if want.lower() in s.lower() and "flip" not in s]
exact = [s for s in matches if s.lower() == want.lower()]
if len(exact) == 1 or len(matches) == 1:
    sess = (exact or matches)[0]
else:
    sys.exit("\n   ".join([f"'{want}' fits {len(matches)} sessions, and this script reads one: give more of the name."] + matches))
row = z.rows[sess]
p = np.asarray(fits.getdata(f"{z.S}/starless/plates/{sess}_plate.fits"), np.float32)
o = np.asarray(fits.getdata(z.output_path(sess)), np.float32)
d = max(1.0, float(fits.getheader(f"{z.S}/session-masters/{sess}.fits").get("DATAMAX", 1.0)))
H, W = p.shape[1:]
v = np.nan_to_num(np.stack([(o[c] - p[c]) / (row["NoiseSigma"][c] * d) for c in range(p.shape[0])]).mean(0))
big = v < -3
lab, n = label(big); sizes = np.bincount(lab.ravel())[1:]; order = np.argsort(sizes)[::-1]
print(f"{z.name}  {sess[:80]}\nregions down over 3 sigma: {n}, pixels {int(big.sum())} of {H * W}; the ten largest hold {int(sizes[order[:10]].sum())}")
near = {k: np.zeros((H, W), bool) for k in ("not subtracted", "saturated", "subtracted, 100 sigma or more")}
for s in csv.DictReader(open(f"{z.S}/starless/plates/{sess}_plate.stars.csv", encoding="utf-8")):
    x, y = int(round(float(s["x"]))), int(round(float(s["y"])))
    if not (0 <= x < W and 0 <= y < H):
        continue
    if s["outcome"] != "Subtracted": near["not subtracted"][y, x] = True
    elif s["saturated"].lower() in ("true", "1"): near["saturated"][y, x] = True
    elif float(s["significance"]) >= 100: near["subtracted, 100 sigma or more"][y, x] = True
total = (v * big).sum(); rest = big.copy()
for k, mk in near.items():
    zone = binary_dilation(mk, iterations=40) & rest
    print(f"   within 40 px of a {k} star ({int(mk.sum())}): {100 * zone.sum() / big.sum():.1f}% of the pixels, {100 * (v * zone).sum() / total:.1f}% of the light taken")
    rest &= ~zone
print(f"   elsewhere: {100 * rest.sum() / big.sum():.1f}% of the pixels, {100 * (v * rest).sum() / total:.1f}% of the light taken")
for i in order[:5]:
    ys, xs = np.where(lab == i + 1)
    print(f"   region of {sizes[i]} px at x {xs.mean():.0f} y {ys.mean():.0f}, deepest {v[ys, xs].min():.0f} sigma")
