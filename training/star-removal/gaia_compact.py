"""Gaia completeness with the smooth level set aside (the crowded-gap diagnostic, 2026-11-20).
    python -I gaia_compact.py <input master.fits> <check2 work dir> <label=starless.fits> [...]
gaia_remover_check.py's completeness counts all the light a remover took inside a star's 3 px disc, so a remover that also
takes a smooth level between the stars scores higher at every star. Here the taken light is read over ITS OWN 16 to 24 px
annulus as the input's star is: (taken in the disc less the annulus median of the taken light) over (the input's disc less
its annulus median). Both figures per BP bin, green channel: as the row reads it, and with the level set aside."""
import sys
from pathlib import Path

import numpy as np
from astropy.io import fits
from astropy.wcs import WCS

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
rms = fits.getdata(work / f'{inp.stem}_c1.rms.fits').astype(np.float64)
yy, xx = np.mgrid[-24:25, -24:25]
rr = np.hypot(xx, yy)
disc, ann = rr < 3, (rr >= 16) & (rr < 24)
bins = [(0, 13), (13, 15), (15, 17), (17, 19)]
keep = (ix >= 24) & (ix < W - 24) & (iy >= 24) & (iy < H - 24)
rng = np.random.default_rng(1)
print("remover            BP bin   stars seen   removed over half, %: as the row reads it / with the level set aside   median share: row / level aside")
for label, path in removers:
    st = fits.getdata(path).astype(np.float64)
    st = st[1] if st.ndim == 3 else st
    taken = data[1] - st
    for lo, hi in bins:
        sel = np.where(keep & (gmag >= lo) & (gmag < hi))[0]
        if len(sel) > 40000:
            sel = rng.choice(sel, 40000, replace=False)
        row, aside = [], []
        for k in sel:
            x, y = ix[k], iy[k]
            win = data[1, y - 24:y + 25, x - 24:x + 25]
            excess = (win[disc] - np.median(win[ann])).sum()
            if excess > 3 * rms[y, x] * np.sqrt(disc.sum()):
                t = taken[y - 24:y + 25, x - 24:x + 25]
                row.append(t[disc].sum() / excess)
                aside.append((t[disc] - np.median(t[ann])).sum() / excess)
        row, aside = np.array(row), np.array(aside)
        if len(row) >= 5:
            print(f"{label:18s} {lo:>2}-{hi:<3}  {len(row):8d}     {100 * np.mean(row > 0.5):5.1f} / {100 * np.mean(aside > 0.5):5.1f}"
                  f"                                 {np.median(row):.2f} / {np.median(aside):.2f}")
