"""Group M: filter evidence and calibration safety for the 2024-02-03 ASI294MC Eta Carinae night.
READ ONLY. All arithmetic on raw, undebayered frames, BAYERPAT read from the header and asserted.

  filter     flat R/G and B/G (bias-corrected by the dark-flats), sky R/G and B/G and sky ADU/s on the
             lights (dark-subtracted, p20 per channel so the nebula does not lift it)
  darks      median(dark - bias) per channel; fraction of light - dark below zero and its p0.01
             (the one-sided over-subtraction test)
  flats      the flats were shot the NEXT MORNING (22:49 UTC against lights ending 15:32 UTC), so the
             train may have moved: compare the flat's radial falloff with the lights' own sky falloff,
             and flat-field the lights' sky to see whether the correction flattens it
"""
import glob
import numpy as np
import astropy.io.fits as fits

ROOT = r'D:/Astro-Pics/2024/2024-02-03'
LIGHTS = sorted(glob.glob(ROOT + '/eta Car/2024-02-03/Light/12_48_52Z/rawframes/*.fits'))
DARKS = sorted(glob.glob(ROOT + '/eta Car Darks/2024-02-03/Light/15_34_37Z/*.fits'))
BIAS = sorted(glob.glob(ROOT + '/eta Car Bias/2024-02-03/Bias/15_48_22Z/*.fits'))
FLATS = sorted(glob.glob(ROOT + '/Flats/*.fits'))
DFLATS = sorted(glob.glob(ROOT + '/DarkFlats/2024-02-03/DarkFlat/22_55_28Z/*.fits'))
print({k: len(v) for k, v in dict(lights=LIGHTS, darks=DARKS, bias=BIAS, flats=FLATS, darkflats=DFLATS).items()})

for label, ps in (('light', LIGHTS), ('dark', DARKS), ('bias', BIAS), ('flat', FLATS), ('darkflat', DFLATS)):
    h = fits.getheader(ps[0])
    assert h.get('BAYERPAT', '').strip() == 'RGGB', (label, h.get('BAYERPAT'))
    assert int(h.get('XBAYROFF', 0) or 0) == 0 and int(h.get('YBAYROFF', 0) or 0) == 0, (label, 'Bayer offset')


def load(p):
    return fits.getdata(p).astype(np.float32)


def spread(ps, n):
    idx = np.linspace(0, len(ps) - 1, n).round().astype(int)
    return [ps[i] for i in idx]


def master(ps, n):
    return np.median(np.stack([load(p) for p in spread(ps, n)]), axis=0)


def planes(a):
    """RGGB: R G / G B. Green is the mean of the two greens."""
    return a[0::2, 0::2], 0.5 * (a[0::2, 1::2] + a[1::2, 0::2]), a[1::2, 1::2]


def centre(a, half=250):
    h, w = a.shape
    return a[h // 2 - half:h // 2 + half, w // 2 - half:w // 2 + half]


def radial(g, nbins=8):
    """Median of the green plane in rings, normalised to the centre ring."""
    h, w = g.shape
    y, x = np.indices(g.shape)
    r = np.hypot((y - h / 2) / (h / 2), (x - w / 2) / (w / 2)) / np.sqrt(2)
    edges = np.linspace(0, 1, nbins + 1)
    vals = [np.median(g[(r >= a) & (r < b)]) for a, b in zip(edges[:-1], edges[1:])]
    return np.array(vals) / vals[0]


bias = master(BIAS, 30)
dark = master(DARKS, 30)
dflat = master(DFLATS, 20)
flat = master(FLATS, 20) - dflat

print('\n=== darks: what they carry beyond the offset ===')
for name, pl in zip('RGB', planes(dark - bias)):
    print(f'  median(dark - bias) {name}: {np.median(pl):+.1f} ADU')
print(f'  dark-flat - bias (34 ms): {np.median(dflat - bias):+.1f} ADU')

print('\n=== filter: flat ratios (flat - dark-flat), centre 500x500 of each plane ===')
r, g, b = (centre(p) for p in planes(flat))
print(f'  flat  R/G={np.median(r) / np.median(g):.4f}  B/G={np.median(b) / np.median(g):.4f}  G={np.median(g):.0f} ADU')

print('\n=== lights: over-subtraction and sky, 12 frames across the night ===')
sky = []
light_bg = []
for p in spread(LIGHTS, 12):
    a = load(p) - dark
    neg = float((a < 0).mean())
    p001 = float(np.percentile(a, 0.01))
    rr, gg, bb = planes(a)
    s = (np.percentile(rr, 20), np.percentile(gg, 20), np.percentile(bb, 20))
    sky.append(s)
    light_bg.append(gg)
    t = fits.getheader(p).get('CCD-TEMP')
    print(f'  {p[-16:]}  T={t}C  below zero {neg:.4%}  p0.01 {p001:+.0f}  sky R/G={s[0] / s[1]:.4f} B/G={s[2] / s[1]:.4f} '
          f'G={s[1]:.0f} ADU ({s[1] / 10:.0f} ADU/s)')
s = np.median(np.array(sky), axis=0)
print(f'  median sky  R/G={s[0] / s[1]:.4f}  B/G={s[2] / s[1]:.4f}  G {s[1] / 10:.0f} ADU/s')

print('\n=== flats against the lights: radial falloff of the green plane (centre = 1) ===')
bg = np.median(np.stack(light_bg), axis=0)   # median of 12 frames: the stars drift out, the vignette stays
fr = radial(planes(flat)[1])
lr = radial(bg)
cr = radial(bg / (planes(flat)[1] / np.median(planes(flat)[1])))
print('  ring         ' + ' '.join(f'{i:6d}' for i in range(len(fr))))
print('  flat         ' + ' '.join(f'{v:6.3f}' for v in fr))
print('  light sky    ' + ' '.join(f'{v:6.3f}' for v in lr))
print('  sky / flat   ' + ' '.join(f'{v:6.3f}' for v in cr))
