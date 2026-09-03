"""The peak audit against UNTRUNCATED Gaia DR3 (Vizier), with D50 alongside as a cross-check."""
import numpy as np, os, sys
from astropy.io import fits
from astropy.wcs import WCS
from scipy.ndimage import maximum_filter
from scipy.spatial import cKDTree
import gaia_d50 as d50, gaia_vizier as vizier

path, label = sys.argv[1], sys.argv[2]
with fits.open(path) as hdul:
    data = hdul[0].data.astype(np.float32); w = WCS(hdul[0].header, naxis=2)
lum = data.mean(axis=0) if data.ndim == 3 else data
h, wid = lum.shape
med = float(np.median(lum)); mad = float(np.median(np.abs(lum - med)))
det = (lum >= maximum_filter(lum, size=5)) & (lum > med + 8 * mad)
ys, xs = np.nonzero(det)
ok = (ys > 3) & (ys < h - 4) & (xs > 3) & (xs < wid - 4)
ys, xs = ys[ok], xs[ok]
snr = (lum[ys, xs] - med) / mad
ra, dec = w.all_pix2world(xs.astype(float), ys.astype(float), 0)
scale = abs(w.pixel_scale_matrix[0, 0]) * 3600.0
tol = 2.5 * scale
cd = np.cos(np.radians(dec.mean()))
ra0, dec0 = (ra.min()+ra.max())/2, (dec.min()+dec.max())/2
# ADQL BOX width is in COORDINATE degrees of RA at CDS, not true angle. Passing true angle makes
# the box narrow by 1/cos(dec) and silently drops peaks near the RA edges: at M33's +30.7 deg that
# cost 10 percent of matches and read as 'a deeper catalogue matched FEWER', which is impossible.
wbox = (ra.max()-ra.min()) + 0.1; hbox = (dec.max()-dec.min()) + 0.1
area = (ra.max()-ra.min())*cd*(dec.max()-dec.min())

gra, gdec, gmag = vizier.fetch_box(ra0, dec0, wbox, hbox, mag_max=21.0)
dra, ddec, dmag = d50.read_sky(os.environ.get('ASTAP_DIR', r'C:/Program Files/astap'), 'd50_*.1476',
                               ra.min()-0.05, ra.max()+0.05, dec.min()-0.05, dec.max()+0.05)
print(f'== {label} ==  {len(ys)} peaks, {area:.2f} sq deg, {scale:.2f}"/px')
print(f'Vizier Gaia DR3 (BP<21): {len(gmag)} = {len(gmag)/area:.0f}/sq deg')
print(f'ASTAP D50:               {len(dmag)} = {len(dmag)/area:.0f}/sq deg  ({100*len(dmag)/max(len(gmag),1):.0f}% of Vizier)\n')

fwd = cKDTree(np.column_stack([gra*cd, gdec])).query(np.column_stack([ra*cd, dec]))[0]*3600.0 <= tol
rev = cKDTree(np.column_stack([ra*cd, dec])).query(np.column_stack([gra*cd, gdec]))[0]*3600.0 <= tol
# Chance rate: a peak lands within tol of SOME catalogue star by coincidence.
chance = len(gra)/ (area*3600*3600) * np.pi * tol**2
print(f'peaks with a Gaia star: {100*fwd.mean():.1f}%   (coincidence rate at this density: {100*chance:.1f}%)')

edges=[8,15,30,100,np.inf]
print(f"\n{'SNR bucket':>12} {'peaks':>8} {'matched':>9}")
for lo,hi in zip(edges,edges[1:]):
    m=(snr>=lo)&(snr<hi)
    if m.sum(): print(f'{f"{lo:g}-{hi:g}":>12} {m.sum():8d} {100*fwd[m].mean():8.1f}%')

expected=0.0
edges2=[gmag.min()-.01,13,14,15,16,17,18,19,20,21]
print(f"\n{'Gaia BP':>10} {'catalogued':>11} {'detector finds':>15} {'expected peaks':>15}")
for lo,hi in zip(edges2,edges2[1:]):
    m=(gmag>=lo)&(gmag<hi)
    if m.sum()>20:
        c=rev[m].mean(); expected+=m.sum()*c
        print(f'{f"{lo:.0f}-{hi:.0f}":>10} {m.sum():11d} {100*c:14.1f}% {m.sum()*c:15.0f}')
print(f"\nexpected star-peaks {expected:.0f} of {len(ys)} -> {100*expected/len(ys):.0f}% stars, "
      f"{100*max(0,1-expected/len(ys)):.0f}% NOT stars")
