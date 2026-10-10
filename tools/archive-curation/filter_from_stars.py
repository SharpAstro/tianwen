"""A colour session's filter read off its STARS, not its sky (docs/plans/filter-inference.md, section 9; #1146).

Stars are continuum sources, and none of what follows depends on the Moon, the transparency or the light pollution,
which is what moves the sky. Broadband, B/G and R/G both follow a star's temperature, a hot star bluer in both, so
across a field's stars B/G FALLS as R/G rises. Through a dual-band filter G and B both see only the 486 to 501 nm
window, so B/G no longer follows the star while R/G (H-alpha against that window) still does: the two are unrelated.

The headline is therefore the slope of ln(B/G) on ln(R/G) across the stars (Theil-Sen, with Spearman's rank
correlation beside it). On the IMX585's nights of known filter (2026-10-10, filter-inference.md section 9a), six
broadband nights read -0.49 to -0.98 (Spearman -0.42 to -0.68) and two L-eNhance nights -0.04 and +0.11 (-0.15 and
+0.11). The B/G spread alone is weaker: it narrowed through the L-eNhance on the SMC (11 %) but not on eta Car, a
heavily reddened Milky Way field, where it spread 68 %, wider than any broadband night.

Per session (a folder of one colour camera's raw lights), on a few subs spread over the night, minus a master dark at
the lights' gain, offset, binning and exposure:
  stars   per-channel aperture flux of bright unsaturated stars: the slope and correlation above, and the median and
          inter-quartile spread of B/G and R/G
  sky     per-channel level per second, R/G and B/G
  flux    the 50 brightest unsaturated stars' G flux per second (a rough cross-check: optics, fields and saturation
          differ between nights)
then one row per session side by side, and what each slope reads as.

The in-camera white balance (a digital gain on R and B, section 2a-quater) is read off the master dark's channel
medians and divided out of every ratio; a dark reading 1.0 for both has none. Neither the slope nor a spread depends
on it, a median does.

Compare sessions of ONE sensor: the ratios are its CFA's and do not transfer to another body (section 2a-ter: on the
IMX585 the sky separates on R/G, not B/G). The thresholds below are the IMX585's; read another sensor's known nights
first.

Darks: a light folder under <organized>/lights/<camera>/... takes them from <organized>/calibration/<camera>/DARK,
the folders whose name (<date>-g<gain>-o<offset>-t<temp>[-e<exp>s][-bin<n>]) matches the lights' gain, offset,
binning and exposure, at the nearest temperature, every folder tied there pooled. With no dark of that exposure the
same gain and offset's BIAS stands in and the run says so: the dark current is then left in every channel. Name the
darks yourself as LIGHTS=DARKS[,DARKS...]. A session with neither is skipped, and the rest still run.

Rows are read in file order and BAYERPAT applies as written, as TianWen's reader does (every archive frame is
TOP-DOWN). A frame with no BAYERPAT is skipped: star colours need a CFA.

  python filter_from_stars.py LIGHTS[=DARKS[,DARKS...]] [LIGHTS ...] [--subs 6] [--darks 12] [--organized D:/Astro-Organized]

First run, 2026-10-10: the Uranus-C Lagoon of 2023-08-09, filed as Unidentified-HaOIII on its sky rate alone, reads
-0.04 (Spearman -0.13): dual-band, as filed.

READ ONLY.
"""
import argparse
import glob
import os
import re
import sys

import astropy.io.fits as fits
import numpy as np
from scipy import ndimage, stats

FRAME_EXTENSIONS = ('.fits', '.fit', '.fts', '.fz')
FOLDER = re.compile(r'^\d{4}-\d{2}-\d{2}-g(?P<gain>[^-]+)-o(?P<offset>[^-]+)-t(?P<temp>[+-]?\d+(?:\.\d+)?)'
                    r'(?:-e(?P<exp>\d+(?:\.\d+)?)s)?(?:-bin(?P<bin>\d+))?$')
PEAK_SIGMA = 40        # a star's peak over the summed superpixel image's noise
PEAK_WINDOW = 9        # a peak is the maximum of this many superpixels square
APERTURE, ANNULUS_IN, ANNULUS_OUT = 4, 7, 10   # superpixels
NEAR_SATURATION = 0.8  # of the frame's own raw maximum
MIN_STARS = 30
BRIGHTEST = 50
# The slope of ln(B/G) on ln(R/G), read on the IMX585 (see the docstring): above DUAL_BAND it reads as a dual-band,
# below BROADBAND as a broadband filter, and between them as neither.
DUAL_BAND, BROADBAND = -0.25, -0.40


class Skip(Exception):
    """A session this run cannot read; the others still run."""


def frames(folder):
    """The folder's frames, those a curator marked BAD_ left out, in name order."""
    if not os.path.isdir(folder):
        raise Skip(f'{folder}: not a folder')
    found = [os.path.join(folder, f) for f in os.listdir(folder)
             if f.lower().endswith(FRAME_EXTENSIONS) and not f.startswith('BAD_')]
    return sorted(found)


def spread_over(items, count):
    """At most count items spread evenly from first to last, so a sample covers the whole night."""
    if len(items) <= count:
        return list(items)
    picks = sorted(set(np.linspace(0, len(items) - 1, count).round().astype(int)))
    return [items[i] for i in picks]


def read(path):
    """The first HDU holding a 2-D image, as float64 with BZERO and BSCALE applied, and its header."""
    with fits.open(path, memmap=False) as hdus:
        for hdu in hdus:
            if hdu.data is not None and hdu.data.ndim == 2:
                return hdu.header, hdu.data.astype(np.float64)
    raise Skip(f'{path}: no 2-D image')


def header(path):
    with fits.open(path, memmap=False) as hdus:
        for hdu in hdus:
            if hdu.header.get('NAXIS', 0) == 2 or hdu.header.get('ZNAXIS', 0) == 2:
                return hdu.header
    raise Skip(f'{path}: no 2-D image')


def card(h, *names):
    for name in names:
        if name in h and h[name] not in (None, ''):
            return h[name]
    return None


def settings(h):
    """What a dark must match: gain, offset, binning and exposure, and the temperature it should be near."""
    def number(*names):
        value = card(h, *names)
        try:
            return float(value)
        except (TypeError, ValueError):
            return None
    return {'gain': number('GAIN'), 'offset': number('OFFSET', 'BLKLEVEL'), 'bin': int(number('XBINNING') or 1),
            'exp': number('EXPTIME', 'EXPOSURE'), 'temp': number('CCD-TEMP', 'SET-TEMP')}


def same(a, b):
    """Equal settings, an absent one (a CCD's 'na') matching only another absent one."""
    if a is None or b is None:
        return a is None and b is None
    return abs(a - b) <= 1e-6 * max(1.0, abs(a))


def matched_darks(light_dir, organized, want):
    """Dark folders for these lights from the Organized calibration tree, and what was matched (see the docstring)."""
    parts = os.path.normpath(os.path.abspath(light_dir)).split(os.sep)
    root = os.path.normpath(os.path.abspath(organized)).split(os.sep)
    folded = [os.path.normcase(p) for p in parts]
    if (folded[:len(root)] != [os.path.normcase(p) for p in root] or len(parts) <= len(root) + 1
            or folded[len(root)] != 'lights'):
        raise Skip(f'{light_dir}: not under {organized}/lights, so name its darks: LIGHTS=DARKS')
    calibration = os.path.join(organized, 'calibration', parts[len(root) + 1])
    for kind, wants_exposure in (('DARK', True), ('BIAS', False)):
        candidates = []
        for folder in glob.glob(os.path.join(calibration, kind, '*')):
            m = FOLDER.match(os.path.basename(folder))
            if not m:
                continue
            gain = None if m['gain'] == 'na' else float(m['gain'])
            offset = None if m['offset'] == 'na' else float(m['offset'])
            if not (same(gain, want['gain']) and same(offset, want['offset'])) or int(m['bin'] or 1) != want['bin']:
                continue
            if wants_exposure and not (m['exp'] and same(float(m['exp']), want['exp'])):
                continue
            distance = abs(float(m['temp']) - want['temp']) if want['temp'] is not None else 0.0
            candidates.append((distance, folder))
        if candidates:
            nearest = min(d for d, _ in candidates)
            return [f for d, f in sorted(candidates) if d == nearest], kind
    raise Skip(f"{light_dir}: no dark or bias at gain {want['gain']}, offset {want['offset']}, bin {want['bin']} "
               f"under {calibration}; name them: LIGHTS=DARKS")


def master(folders, count):
    files = spread_over(sorted(f for d in folders for f in frames(d)), count)
    if not files:
        raise Skip(f'no frames in {", ".join(folders)}')
    return np.median(np.stack([read(f)[1] for f in files]), axis=0), len(files)


def channels(image, pattern):
    """R, G (the mean of both) and B photosite planes of a 2x2 Bayer pattern such as RGGB."""
    sites = {}
    for (dy, dx), colour in zip(((0, 0), (0, 1), (1, 0), (1, 1)), pattern):
        sites.setdefault(colour, []).append(image[dy::2, dx::2])
    if sorted(sites) != ['B', 'G', 'R'] or len(sites['G']) != 2:
        raise Skip(f'BAYERPAT {pattern!r} is not a 2x2 pattern of one R, two G and one B')
    return sites['R'][0], (sites['G'][0] + sites['G'][1]) / 2, sites['B'][0]


def stars(R, G, B, raw_peak):
    """Per-channel aperture flux of every bright, unsaturated star, found on the summed superpixel image."""
    lum = R + 2 * G + B
    bg = ndimage.median_filter(lum[::4, ::4], size=15)
    bg = ndimage.zoom(bg, (lum.shape[0] / bg.shape[0], lum.shape[1] / bg.shape[1]), order=1)[:lum.shape[0], :lum.shape[1]]
    resid = lum - bg
    noise = 1.4826 * np.median(np.abs(resid - np.median(resid)))
    peaks = (resid == ndimage.maximum_filter(resid, size=PEAK_WINDOW)) & (resid > PEAK_SIGMA * noise)
    yy, xx = np.mgrid[-ANNULUS_OUT:ANNULUS_OUT + 1, -ANNULUS_OUT:ANNULUS_OUT + 1]
    rr = np.hypot(yy, xx)
    aperture, annulus = rr <= APERTURE, (rr >= ANNULUS_IN) & (rr <= ANNULUS_OUT)
    h, w = G.shape
    for y, x in zip(*np.nonzero(peaks)):
        if y < ANNULUS_OUT or x < ANNULUS_OUT or y >= h - ANNULUS_OUT or x >= w - ANNULUS_OUT:
            continue
        window = (slice(y - ANNULUS_OUT, y + ANNULUS_OUT + 1), slice(x - ANNULUS_OUT, x + ANNULUS_OUT + 1))
        if raw_peak(window) > NEAR_SATURATION:
            continue
        flux = [c[window][aperture].sum() - np.median(c[window][annulus]) * aperture.sum() for c in (R, G, B)]
        if min(flux) > 0:
            yield flux


def reads_as(slope):
    return 'dual-band' if slope > DUAL_BAND else 'broadband' if slope < BROADBAND else 'neither'


def session(spec, organized, subs, dark_count):
    light_dir, _, named = spec.partition('=')
    lights = frames(light_dir)
    if not lights:
        raise Skip(f'{light_dir}: no frames')
    first = header(lights[0])
    pattern = str(card(first, 'BAYERPAT') or '').strip().upper()
    if not pattern:
        raise Skip(f'{light_dir}: no BAYERPAT; star colours need a CFA')
    want = settings(first)
    if want['exp'] is None:
        raise Skip(f'{light_dir}: no EXPTIME or EXPOSURE')
    if named:
        dark_dirs, kind = named.split(','), 'DARK (named)'
    else:
        dark_dirs, kind = matched_darks(light_dir, organized, want)
    dark, used = master(dark_dirs, dark_count)
    dR, dG, dB = channels(dark, pattern)
    wb_r, wb_b = np.median(dR) / np.median(dG), np.median(dB) / np.median(dG)

    picked = spread_over(lights, subs)
    print(f"\n== {light_dir}")
    print(f"   {len(picked)} of {len(lights)} subs, {want['exp']:g} s, gain {want['gain']}, offset {want['offset']}, "
          f"bin {want['bin']}, {want['temp']} C, {pattern}, {card(first, 'ROWORDER') or 'no ROWORDER'}; "
          f"filter card {card(first, 'FILTER')!r}, object {card(first, 'OBJECT')!r}")
    print(f"   {kind}: master of {used} from {', '.join(os.path.basename(d) for d in dark_dirs)}")
    if kind == 'BIAS':
        print('   (no dark at this exposure: the dark current stays in every channel below)')
    print(f"   white balance read off it: R x{wb_r:.4f}, B x{wb_b:.4f} (1.0 is none), divided out below")

    sky_rates, b_g, r_g, g_flux = [], [], [], []
    for path in picked:
        _, image = read(path)
        if image.shape != dark.shape:
            raise Skip(f'{path}: {image.shape} against the dark\'s {dark.shape}')
        R, G, B = channels(image, pattern)
        raw = (R, G, B)
        full = max(c.max() for c in raw)
        R, G, B = (R - dR) / wb_r, G - dG, (B - dB) / wb_b
        h, w = G.shape
        core = (slice(h // 4, 3 * h // 4), slice(w // 4, 3 * w // 4))
        sky_rates.append([np.median(c[core]) / want['exp'] for c in (R, G, B)])
        for flux in stars(R, G, B, lambda window: max(c[window].max() for c in raw) / full):
            r_g.append(flux[0] / flux[1])
            b_g.append(flux[2] / flux[1])
            g_flux.append(flux[1] / want['exp'])

    sky = np.median(sky_rates, axis=0)
    row = {'session': light_dir, 'sky_g': sky[1], 'sky_rg': sky[0] / sky[1], 'sky_bg': sky[2] / sky[1],
           'stars': len(b_g)}
    if len(b_g) < MIN_STARS:
        print(f'   too few stars ({len(b_g)})')
    else:
        lb, lr = np.log(b_g), np.log(r_g)
        row['slope'] = stats.theilslopes(lb, lr).slope
        row['rho'] = stats.spearmanr(lb, lr).statistic
        print(f"   stars: ln B/G on ln R/G slope {row['slope']:+.2f}, Spearman {row['rho']:+.2f} "
              f"({len(b_g)} stars over {len(picked)} subs): reads as {reads_as(row['slope'])}")
        for name, values in (('B/G', b_g), ('R/G', r_g)):
            q25, q50, q75 = np.percentile(values, [25, 50, 75])
            row[name] = (q50, (q75 - q25) / q50)
            print(f"   stars {name}       median {q50:.3f}, IQR {q25:.3f} to {q75:.3f} (relative spread {(q75 - q25) / q50:.1%})")
        row['g_flux'] = float(np.median(np.sort(g_flux)[::-1][:BRIGHTEST]))
        print(f"   G flux per second of the {BRIGHTEST} brightest: median {row['g_flux']:.1f}")
    print(f"   sky per second   R {sky[0]:.2f}  G {sky[1]:.2f}  B {sky[2]:.2f}  ->  R/G {row['sky_rg']:.3f}  "
          f"B/G {row['sky_bg']:.3f}")
    return row


def main():
    parser = argparse.ArgumentParser(description=__doc__.split('\n\n')[0])
    parser.add_argument('sessions', nargs='+', help='light folders, each optionally =DARK[,DARK...]')
    parser.add_argument('--subs', type=int, default=6, help='lights read per session, spread over the night')
    parser.add_argument('--darks', type=int, default=12, help='darks in each master')
    parser.add_argument('--organized', default='D:/Astro-Organized', help='the archive root darks are matched in')
    args = parser.parse_args()
    rows, skipped = [], []
    for spec in args.sessions:
        try:
            rows.append(session(spec, args.organized, args.subs, args.darks))
        except Skip as why:
            print(f'\n== skipped: {why}')
            skipped.append(spec)

    print('\n== side by side (one sensor only; the thresholds are the IMX585\'s)')
    print(f"   {'slope':>6} {'rho':>6}  {'reads as':9}  {'B/G spread':>10} {'R/G spread':>10}  {'G flux/s':>9}  "
          f"{'sky G/s':>8} {'sky R/G':>7} {'sky B/G':>7}  session")
    for r in rows:
        if 'slope' in r:
            head = (f"{r['slope']:+6.2f} {r['rho']:+6.2f}  {reads_as(r['slope']):9}  {r['B/G'][1]:10.1%} "
                    f"{r['R/G'][1]:10.1%}  {r['g_flux']:9.1f}")
        else:
            head = f"{'too few stars (' + str(r['stars']) + ')':<58}"
        print(f"   {head}  {r['sky_g']:8.2f} {r['sky_rg']:7.3f} {r['sky_bg']:7.3f}  {r['session']}")
    print(f'   (slope: of ln B/G on ln R/G across the stars; above {DUAL_BAND} reads as a dual-band, '
          f'below {BROADBAND} as broadband)')
    return 1 if skipped and not rows else 0


if __name__ == '__main__':
    sys.exit(main())
