"""Star colours on the SAME sensor (IMX294), the one filter comparison that controls both the CFA and
the light source. READ ONLY.

A flat's ratios carry the flat's own light: group M's flats are DAYLIGHT (34 ms at 09:49 local) while
group J's came from a panel, so their B/G difference (0.81 against 0.53) mixes illumination with filter.
The sky mixes site, moon and light pollution. Stars are the same light source on every night, and the
median star over hundreds of them is a stable population average in a Milky Way field, so star R/G and
B/G on one sensor move with the FILTER. An IDAS LPS-D3 notches part of green out (Hg 546, Na 589), which
should show in them.
"""
import glob
import numpy as np
import astropy.io.fits as fits
from scipy.ndimage import median_filter, binary_dilation, label

SETS = [
    ('ASI294MC  2024-02-03 Eta Car  (owner: UV/IR cut)',
     sorted(glob.glob('D:/Astro-Pics/2024/2024-02-03/eta Car/2024-02-03/Light/12_48_52Z/rawframes/*.fits'))),
]
for d in sorted(glob.glob('D:/Astro-Organized/lights/QHYCCD-QHY294C/IDAS-LPS-D3/*/*/')):
    ps = sorted(glob.glob(d + '*.fit*'))
    if ps:
        SETS.append((f"QHY294C   {d.split('/')[-2]} {d.split('/')[-3][:18]} (IDAS-LPS-D3)", ps))


def split(a, pattern):
    a = a[:a.shape[0] // 2 * 2, :a.shape[1] // 2 * 2]
    by = {}
    for c, q in zip(pattern, (a[0::2, 0::2], a[0::2, 1::2], a[1::2, 0::2], a[1::2, 1::2])):
        by.setdefault(c, []).append(q)
    return by['R'][0], 0.5 * (by['G'][0] + by['G'][1]), by['B'][0]


def star_colours(path):
    h = fits.getheader(path)
    r, g, b = split(fits.getdata(path).astype(np.float32), str(h.get('BAYERPAT')).strip().upper())
    sl = [median_filter(x, size=9) for x in (r, g, b)]
    exc = [x - s for x, s in zip((r, g, b), sl)]
    mad = np.median(np.abs(exc[1] - np.median(exc[1])))
    peak = exc[1] > 25 * mad
    # Per STAR, then the median over stars: one bright star must not set the answer, and a saturated
    # core (which clips green first) is excluded by requiring every channel's peak below 90% of max.
    lab, n = label(binary_dilation(peak, iterations=2))
    full = float(np.max(r))
    rg, bg = [], []
    for i in range(1, min(n, 3000) + 1):
        m = lab == i
        if m.sum() < 6 or max(r[m].max(), g[m].max(), b[m].max()) > 0.9 * full:
            continue
        gs = exc[1][m].sum()
        if gs <= 0:
            continue
        rg.append(exc[0][m].sum() / gs)
        bg.append(exc[2][m].sum() / gs)
    return np.median(rg), np.median(bg), len(rg)


for lbl, ps in SETS:
    vals = [star_colours(ps[i]) for i in np.linspace(0, len(ps) - 1, 3).round().astype(int)]
    rg, bg = np.median([v[0] for v in vals]), np.median([v[1] for v in vals])
    print(f'{lbl:62s} stars/frame ~{int(np.median([v[2] for v in vals])):5d}  star R/G {rg:.3f}  star B/G {bg:.3f}')
