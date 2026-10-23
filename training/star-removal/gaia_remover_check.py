"""Star removers judged from BOTH sides against Gaia DR3, the known stars, never our own detector:

  completeness: at every Gaia star the INPUT visibly shows (its 3 px disc over the input's own 16-24 px annulus,
    above 3 sigma), the share of that light the remover moved into its stars layer (input less starless), by BP
    magnitude. 1 is a star fully removed, 0 a star left in the starless plate.
  purity: the stars layer's light that lies AWAY from every Gaia star (farther than 4 px from any star to BP 19, 8 px
    from any brighter than BP 13), as a share of all its light, over the whole frame and over bright backgrounds (the
    input's background map 1.5 sigma above the frame's sky: a galaxy, a nebula, the LMC's glow). A remover that takes
    only stars puts almost nothing there.

Glue only: Gaia from training/denoise/gaia_vizier.py (Vizier TAP, cached), the master's WCS (Tianwen-solved, FITS
convention, PIXORIG=1), the background and noise maps remover_check2.py had Tianwen's detector write.
    python gaia_remover_check.py <input master.fits> <check2 work dir> <label=starless.fits> [...]
"""
import sys
from pathlib import Path

import numpy as np
from astropy.io import fits
from astropy.wcs import WCS
from scipy.ndimage import distance_transform_edt

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / 'denoise'))
import gaia_vizier  # noqa: E402

inp, work = Path(sys.argv[1]), Path(sys.argv[2])
removers = [(a.split('=', 1)[0], Path(a.split('=', 1)[1])) for a in sys.argv[3:]]
hdr = fits.getheader(inp)
if hdr.get('PIXORIG') != 1:
    sys.exit(f'{inp.name}: no PIXORIG = 1, so the WCS convention is unknown; refusing')
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
print(f'{inp.name}: {len(px)} Gaia DR3 stars on the plate to BP 19')

# purity mask: far from every Gaia star (4 px to BP 19, 8 px for BP < 13)
near = np.zeros((H, W), bool)
ix, iy = np.clip(np.round(px).astype(int), 0, W - 1), np.clip(np.round(py).astype(int), 0, H - 1)
near[iy, ix] = True
far = distance_transform_edt(~near) > 4
bright = gmag < 13
nb = np.zeros((H, W), bool)
nb[iy[bright], ix[bright]] = True
far &= distance_transform_edt(~nb) > 8
bg = fits.getdata(work / f'{inp.stem}_c1.background.fits').astype(np.float64)
rms = fits.getdata(work / f'{inp.stem}_c1.rms.fits').astype(np.float64)
bright_bg = (bg - np.median(bg)) / rms > 1.5
valid = np.isfinite(data[1]) & (data[1] != 0)
print(f'  pixels away from every Gaia star: {np.mean(far[valid]) * 100:.0f} % of the frame, '
      f'{np.mean(far[valid & bright_bg]) * 100:.0f} % of its bright background')

yy, xx = np.mgrid[-24:25, -24:25]
rr = np.hypot(xx, yy)
disc, ann = rr < 3, (rr >= 16) & (rr < 24)
bins = [(0, 13), (13, 15), (15, 17), (17, 19)]
keep = (ix >= 24) & (ix < W - 24) & (iy >= 24) & (iy < H - 24)
for label, path in removers:
    st = fits.getdata(path).astype(np.float64)
    taken = data[1] - st[1]  # the stars layer, green
    print(f'\n  {label}')
    for lo, hi in bins:
        sel = np.where(keep & (gmag >= lo) & (gmag < hi))[0]
        fr = []
        for k in sel:
            x, y = ix[k], iy[k]
            win = data[1, y - 24:y + 25, x - 24:x + 25]
            excess = (win[disc] - np.median(win[ann])).sum()
            noise = rms[y, x] * np.sqrt(disc.sum())
            if excess > 3 * noise:
                fr.append(taken[y - 24:y + 25, x - 24:x + 25][disc].sum() / excess)
        fr = np.array(fr)
        if len(fr) >= 5:
            print(f'    BP {lo:>2}-{hi}: {len(sel):6d} Gaia stars, {len(fr):6d} visible in the input; light taken median '
                  f'{np.median(fr):.2f} (p10 {np.percentile(fr, 10):.2f}, p90 {np.percentile(fr, 90):.2f}); '
                  f'removed (over half) {np.mean(fr > 0.5) * 100:.1f} %')
    pos = np.clip(taken, 0, None)
    total = pos[valid].sum()
    for name, m in (('whole frame', valid), ('bright background', valid & bright_bg)):
        print(f'    purity, {name}: {pos[m & far].sum() / pos[m].sum() * 100 if pos[m].sum() > 0 else float("nan"):.1f} % of its stars-layer '
              f'light lies away from every Gaia star (mean there {np.mean(taken[m & far] / rms[m & far]):+.2f} sigma)')
