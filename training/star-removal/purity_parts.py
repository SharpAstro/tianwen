"""Where a remover's stars-layer light lies that the purity row calls "away from every Gaia star" (2026-11-20).
    python -I purity_parts.py <input master.fits> <check2 work dir> <label=starless.fits> [...]
The purity row (gaia_remover_check.py): the share of the stars layer's positive light farther than 4 px from every Gaia
star to BP 19 (8 px from one brighter than BP 13). StarXTerminator reads 3.1 % on the Tarantula, our models 0.4 to 1.0 and
our guard is "at most 1.0 %". This splits that light, green channel, by where it lies:
  wing     4 to 8 px from a Gaia star fainter than BP 13, or 8 to 16 px from a brighter one (a catalogued star's own wing)
  faint    within 3 px of a compact peak of the INPUT that stands 3 sigma over its 9 px median and is no Gaia star
           (a star the catalogue lacks)
  rest     everything else (diffuse)
and gives each as a share of ALL the stars layer's light, so the parts add up to the purity figure."""
import sys
from pathlib import Path

import numpy as np
from astropy.io import fits
from astropy.wcs import WCS
from scipy.ndimage import distance_transform_edt, maximum_filter, median_filter

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / 'denoise'))
import gaia_vizier  # noqa: E402

inp, work = Path(sys.argv[1]), Path(sys.argv[2])
removers = [(a.split('=', 1)[0], Path(a.split('=', 1)[1])) for a in sys.argv[3:]]
hdr = fits.getheader(inp)
data = fits.getdata(inp).astype(np.float64)
H, W = data.shape[-2:]
w = WCS(hdr, naxis=2)
corners = w.all_pix2world([[0, 0], [W - 1, 0], [0, H - 1], [W - 1, H - 1]], 0)
ra, dec = corners[:, 0], corners[:, 1]
gra, gdec, gmag = gaia_vizier.fetch_box((ra.min() + ra.max()) / 2, (dec.min() + dec.max()) / 2,
                                        ra.max() - ra.min() + 0.1, dec.max() - dec.min() + 0.1, mag_max=19.0)
px, py = w.all_world2pix(gra, gdec, 0)
on = (px >= 0) & (px < W) & (py >= 0) & (py < H)
px, py, gmag = px[on], py[on], gmag[on]
ix, iy = np.clip(np.round(px).astype(int), 0, W - 1), np.clip(np.round(py).astype(int), 0, H - 1)
near = np.zeros((H, W), bool); near[iy, ix] = True
d_all = distance_transform_edt(~near)
nb = np.zeros((H, W), bool); nb[iy[gmag < 13], ix[gmag < 13]] = True
d_bright = distance_transform_edt(~nb)
far = (d_all > 4) & (d_bright > 8)
rms = fits.getdata(work / f'{inp.stem}_c1.rms.fits').astype(np.float64)
g = data[1]
valid = np.isfinite(g) & (g != 0)
wing = far & ((d_all <= 8) | (d_bright <= 16))
s = g - median_filter(g, 9)
peak = (s == maximum_filter(s, 5)) & (s > 3 * rms) & far
d_peak = distance_transform_edt(~peak)
faint = far & ~wing & (d_peak <= 3)
rest = far & ~wing & ~faint
print(f"{inp.name}: {len(px)} Gaia stars to BP 19; away from every one: {100 * far[valid].mean():.0f} % of the frame "
      f"(wing zone {100 * wing[valid].mean():.0f} %, round uncatalogued peaks {100 * faint[valid].mean():.1f} % with {int(peak[valid].sum())} peaks, rest {100 * rest[valid].mean():.0f} %)")
print("remover                         purity %   = wing + faint + rest      mean taken there, sigma: wing / faint / rest")
for label, path in removers:
    st = fits.getdata(path).astype(np.float64)
    st = st[1] if st.ndim == 3 else st
    taken = g - st
    pos = np.clip(taken, 0, None)
    total = pos[valid].sum()
    parts = [100 * pos[valid & m].sum() / total for m in (wing, faint, rest)]
    means = [float(np.mean(taken[valid & m] / rms[valid & m])) for m in (wing, faint, rest)]
    print(f"{label:30s} {100 * pos[valid & far].sum() / total:7.2f}    = {parts[0]:.2f} + {parts[1]:.2f} + {parts[2]:.2f}          {means[0]:+.2f} / {means[1]:+.2f} / {means[2]:+.2f}")
