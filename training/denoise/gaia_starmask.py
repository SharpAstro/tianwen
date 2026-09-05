"""Which of a cache's detected peaks are actually STARS, from Gaia rather than from the image.

`n2n_metrics.star_table` calls any 5x5 local maximum above median + 8 MAD a star. Measured against
Gaia DR3 on 2026-09-04 that is right on a sparse field (99.6 percent) and wrong on an emission nebula
(30.0 percent, against an 8.1 percent coincidence floor), and this project's pool is 100 percent OSC
narrowband. So "faint-star amplitude" has been reading mostly as faint COMPACT DETAIL, and the two
need reporting apart: a denoiser should scrub neither, but they are different claims.

The chain, all cached, one pass per session rather than per cell:

    session id -> master FITS -> plate solve (WCS written back) -> Gaia box -> master pixels
               -> per-cell star positions in CROPPED tile coordinates

Cell geometry is exact, not inferred: `meta["keys"][i]` is `[session, CellX, CellY]` and
DatasetTileExporter documents CellX/CellY as the tile ORIGIN in canvas pixels, so tile pixel (j, i)
is master pixel (CellY + j, CellX + i), and the metric's 16 px rim crop shifts both by 16.

The WCS itself has to be in the FITS convention for astropy to place a star on the right pixel. A CLI
from before 2026-09-05 wrote its 0-based CRPIX under a "1-based" comment, and astropy then read every
star one pixel low: measured as a constant (+0.95, +0.91) px offset between peaks and their Gaia stars
on all four eval4b fields, which a 2.5 px tolerance had been absorbing at the price of a 40 percent
coincidence floor on eta Car. The fixed writer stamps PIXORIG = 1; this module refuses a solved file
without it and re-solves.
"""
import json
import os
import shutil
import subprocess

import numpy as np

import gaia_vizier

# Gaia goes far deeper than any of these frames. Measured detector completeness on this pool's own
# masters collapses past BP 16 (75 percent at 15-16, 15 percent at 16-17), so fainter catalogue stars
# are invisible to us and serve only to manufacture coincidences: at BP < 21 a plate carries 3.5M
# stars, one per 21 pixels of a cell, and a 2.5 px tolerance then matches nearly every peak by chance.
# The cut is what the IMAGE can see, not what the catalogue holds.
DEFAULT_MAG_MAX = 16.0

# Written beside CRPIX by WCS.WriteToHeader since 2026-09-05; its absence means 0-based CRPIX numbers.
PIXEL_ORIGIN_CARD = 'PIXORIG'

# A peak is an integer pixel and a Gaia position is not, so a real match sits within 0.71 px of
# quantisation plus the WCS residual: measured on eval4b, 96.7 percent inside 1.0 px and 99.6 inside
# 1.5 once the writer's one-pixel CRPIX offset was fixed (module docstring). The 2.5 px this used to
# be was covering that offset, at 6.25 times the coincidence floor.
MATCH_PX = 1.0

TILE = 256
BORDER = 16                      # n2n_metrics.crop drops this many pixels per edge
CROP = TILE - 2 * BORDER

BAKES = [r'D:/Astro-Dataset/2025-2026-organized', r'D:/Astro-Dataset/2025-2026-darkscaled']
SOLVED_DIR = os.environ.get('TIANWEN_SOLVED_MASTERS', r'C:/temp/e2/gaia/solved')
CLI = os.environ.get('TIANWEN_CLI',
                     r'C:/Users/SebastianGodelet/source/repos/sharpastro/tianwen/src/TianWen.Cli/bin/Debug/net10.0/tianwen.exe')


def master_path(session_id):
    """Session id -> master FITS. The bakes name a master by the id with '/' and '|' replaced."""
    name = session_id.replace('/', '_').replace('|', '_') + '.fits'
    for bake in BAKES:
        p = os.path.join(bake, 'session-masters', name)
        if os.path.exists(p):
            return p
    return None


def solved_master(session_id, verbose=True):
    """A copy of the session master carrying a WCS in the FITS convention. Solves once, then reuses."""
    src = master_path(session_id)
    if src is None:
        return None
    os.makedirs(SOLVED_DIR, exist_ok=True)
    dst = os.path.join(SOLVED_DIR, os.path.basename(src))
    from astropy.io import fits
    if os.path.exists(dst):
        hdr = fits.getheader(dst)
        if 'CRVAL1' in hdr and hdr.get(PIXEL_ORIGIN_CARD) == 1:
            return dst
        if 'CRVAL1' in hdr:
            # Solved by a CLI from before the marker: its CRPIX is 0-based and astropy reads every star one
            # pixel low (module docstring). Start again from the bake master.
            if verbose:
                print(f'  {os.path.basename(dst)[:60]}: legacy 0-based CRPIX, re-solving')
            os.remove(dst)
    if not os.path.exists(dst):
        shutil.copy2(src, dst)
    hdr = fits.getheader(dst)
    hint = (hdr.get('OBJCTRA'), hdr.get('OBJCTDEC'))
    cmd = [CLI, 'solve', dst, '--update-fits']
    if all(hint):
        # A hint narrows the search enormously; OBJCTRA is what the mount reported, which is close
        # enough for a 5 degree radius even on an unsynced mount.
        try:
            hh, hm, hs = [float(x) for x in str(hint[0]).replace(':', ' ').split()]
            dd, dm, ds = [float(x) for x in str(hint[1]).replace(':', ' ').split()]
            ra_h = abs(hh) + hm / 60 + hs / 3600
            dec_d = abs(dd) + dm / 60 + ds / 3600
            if str(hint[1]).strip().startswith('-') or dd < 0:
                dec_d = -dec_d
            cmd += ['--search-origin', f'{ra_h:.4f},{dec_d:.4f}', '--search-radius', '5']
        except (ValueError, TypeError):
            pass
    if verbose:
        print(f'  solving {os.path.basename(dst)[:60]}')
    r = subprocess.run(cmd, capture_output=True, text=True, timeout=1800)
    hdr = fits.getheader(dst)
    if 'CRVAL1' not in hdr:
        if verbose:
            print(f'    SOLVE FAILED: {r.stdout.strip().splitlines()[-1] if r.stdout.strip() else r.returncode}')
        return None
    if hdr.get(PIXEL_ORIGIN_CARD) != 1:
        # An old CLI would hand astropy 0-based numbers and every star would land a pixel low, silently.
        raise SystemExit(f'{CLI} wrote a WCS without {PIXEL_ORIGIN_CARD}; rebuild it from a checkout at or '
                         f'after 2026-09-05 (dotnet build src/TianWen.Cli)')
    return dst


def stars_for_session(session_id, mag_max=DEFAULT_MAG_MAX, verbose=True, with_mag=False):
    """Gaia stars over the master, as (y, x) master pixels, or (y, x, BP) with `with_mag`. None when the
    master will not solve."""
    path = solved_master(session_id, verbose)
    if path is None:
        return None
    from astropy.io import fits
    from astropy.wcs import WCS
    with fits.open(path) as h:
        hdr, shape = h[0].header, h[0].data.shape
    w = WCS(hdr, naxis=2)
    ny, nx = shape[-2], shape[-1]
    corners = w.all_pix2world([[0, 0], [nx - 1, 0], [0, ny - 1], [nx - 1, ny - 1]], 0)
    ra, dec = corners[:, 0], corners[:, 1]
    ra0, dec0 = (ra.min() + ra.max()) / 2, (dec.min() + dec.max()) / 2
    # BOX width is COORDINATE degrees of RA at CDS -- see gaia_vizier.fetch_box.
    gra, gdec, gmag = gaia_vizier.fetch_box(ra0, dec0,
                                            (ra.max() - ra.min()) + 0.1,
                                            (dec.max() - dec.min()) + 0.1, mag_max=mag_max)
    if len(gra) == 0:
        return np.empty((0, 3 if with_mag else 2))
    px, py = w.all_world2pix(gra, gdec, 0)
    inside = (px >= 0) & (px < nx) & (py >= 0) & (py < ny)
    if verbose:
        print(f'    {inside.sum()} Gaia stars on the plate ({nx}x{ny}) to BP {mag_max:g}')
    cols = [py[inside], px[inside]] + ([gmag[inside]] if with_mag else [])
    return np.column_stack(cols)


def build(cache, mag_max=DEFAULT_MAG_MAX, verbose=True, with_mag=False):
    """cell index -> (n, 2) array of Gaia star (y, x) in CROPPED tile coordinates, or (n, 3) with the
    star's BP magnitude as the third column when `with_mag`, so a caller can cut the catalogue per
    session AFTER one fetch (n2n_starsplit's per-session cap).

    A cell whose session will not solve is absent from the dict, so a caller can tell "no stars here"
    from "no answer for this cell" -- scoring the two the same way would silently count an unsolved
    session as pure structure.
    """
    meta = json.load(open(os.path.join(cache, 'meta.json')))
    keys = meta['keys']
    per_session = {}
    out = {}
    for i, (sid, cx, cy) in enumerate(keys):
        if sid not in per_session:
            if verbose:
                print(f'  session {sid[:64]}')
            per_session[sid] = stars_for_session(sid, mag_max, verbose, with_mag=with_mag)
        stars = per_session[sid]
        if stars is None:
            continue
        y = stars[:, 0] - cy - BORDER
        x = stars[:, 1] - cx - BORDER
        keep = (y >= 0) & (y < CROP) & (x >= 0) & (x < CROP)
        cols = [y[keep], x[keep]] + ([stars[keep, 2]] if with_mag else [])
        out[i] = np.column_stack(cols)
    if verbose:
        solved = sum(1 for v in per_session.values() if v is not None)
        print(f'{solved}/{len(per_session)} sessions solved; {len(out)} of {len(keys)} cells covered')
    return out


if __name__ == '__main__':
    import sys
    m = build(sys.argv[1] if len(sys.argv) > 1 else r'C:/temp/tianwen-scratch/n2n-eval4')
    counts = np.array([len(v) for v in m.values()])
    if len(counts):
        print(f'Gaia stars per cell: median {np.median(counts):.0f}, '
              f'mean {counts.mean():.1f}, range {counts.min()}..{counts.max()}')
