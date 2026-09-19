"""Build every gallery picture for a bake store, through tianwen verbs only.

    python build_views.py <store> <rows.json> <img out dir> [--jobs N] [--limit N]
                          [--names names.json] [--scratch DIR] [--force]

Writes, per row id:

    <id>_raw.png        the cropped, solved master as the stacker wrote it
    <id>_enhanced.png   the same crop, enhanced
    <id>_crop.png       a 1:1 patch of the quietest sky, cut from the raw view

and stamps each row with the view geometry and the white balance the pair was rendered on.

ONE TOOL, ONE PASS, because the two views have to agree and they can only do that while both
files exist at once. This replaces batch_enhance.py + render_views.py, which split the job in
half: the batch rendered the raw view and deleted the cropped FITS, and render_views rendered
the enhanced one later from the enhanced FITS. Three things went wrong with that and all three
are structural, not bugs anyone could have spotted by reading either half.

* THE COLOUR. Each half solved its own SPCC, so the pair disagreed about colour by
  construction -- and the two solves are not the same question asked twice. The enhance has
  already flattened the background and pulled the noise down, so the second solve reads a
  frame the first never saw, and its answer lands on top of a calibration the pixels already
  carry: a double correction, which is what put a blue cast on cards whose raw half was fine.
  Here the raw view is rendered FIRST, its balance is read off `image render`'s own output,
  and the enhanced view is rendered with `--white-balance` set to it. One solve, both halves,
  which is what the skill always claimed happened.
* THE FRAMING. The raw view had to be rendered inside the batch, before the crop was deleted,
  so a fix to `image render` could not be picked up without re-running the enhance beside it
  -- an hour of GPU to re-make a picture that takes seconds. There was a --rawhalf-only mode
  to work around exactly that.
* THE PATCH. It came from a THIRD render, of the uncropped store master, purely because the
  cropped file was gone by then. It is cut out of the raw view now, at the same coordinates
  translated into crop space, so it is a real 1:1 window on the picture beside it.

NO IMAGING LOGIC LIVES HERE. Crop, solve, enhance and render are `image autocrop`, `solve`,
`image sharpen` and `image render`; this file chooses filenames, scales for the web and cuts a
rectangle. Anything load-bearing belongs in the product, where the viewer, the stacker and this
script all reach the same implementation.

Jobs run concurrently against ONE GPU. More jobs only help while the card is not the
bottleneck, so the batch prints per-master wall time and is worth re-timing rather than assumed.
"""
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
from concurrent.futures import ThreadPoolExecutor

import numpy as np
from PIL import Image as PILImage

from astropy.io import fits

# Wider than the old 512-px half: these are looked at to judge a crop edge and a noise floor,
# and at 512 a 130-px border band on a 4108-px master was 16 screen pixels.
VIEW_W = 1200
CROP = 320

PILImage.MAX_IMAGE_PIXELS = None

# The Release CLI. Override with TIANWEN_EXE; the default is this repo's own build output,
# resolved from the script's location so a checkout anywhere works.
EXE = os.environ.get('TIANWEN_EXE') or os.path.normpath(os.path.join(
    os.path.dirname(os.path.abspath(__file__)), '..', '..', 'src', 'TianWen.Cli',
    'bin', 'Release', 'net10.0', 'tianwen.exe'))

# `[render] white-balance 1.068962,1.000000,1.809589 (SPCC)` -- the line `image render` prints
# and `--white-balance` takes back verbatim. Matched rather than assumed: a mono master and an
# unsolved one both render with no balance at all, and that has to read as "nothing to inherit"
# rather than as a parse failure.
WB_LINE = re.compile(r'\[render\] white-balance ([0-9.]+,[0-9.]+,[0-9.]+) \(([^)]+)\)')
# `[autocrop] 3072x3060 -> 2944x2960 at (64,48) via the coverage plane`
CROP_LINE = re.compile(r'\[autocrop\] \d+x\d+ -> (\d+)x(\d+) at \((\d+),(\d+)\)')


def tw(*args, timeout=3600):
    """Run a tianwen verb and hand back its console output. Never raises on a non-zero exit --
    the caller decides whether a failed stage is fatal for that master."""
    proc = subprocess.run([EXE, *args], capture_output=True, text=True, timeout=timeout)
    return proc.returncode, (proc.stdout or ''), (proc.stderr or '')


def has_wcs(path):
    """Whether the file already carries a plate solution, so the solve can be skipped.

    CRPIX1 is the cheap tell: TianWen stamps it (with PIXORIG) whenever it writes a WCS, and a
    file without it did not come from a solved pipeline. Read rather than assumed, because the
    answer differs per master -- a wide field that no installed star index can place stays
    unsolved however many times it is asked (#55).
    """
    try:
        with fits.open(path, memmap=True) as h:
            for hdu in h:
                if hdu.header.get('NAXIS', 0) >= 2:
                    return 'CRPIX1' in hdu.header
    except Exception:
        return False
    return False


def render(path, dst, white_balance=None):
    """Render one FITS through `image render`; return (rgb, wb, wb_source)."""
    args = ['image', 'render', path, '-o', dst]
    if white_balance:
        args += ['--white-balance', white_balance]
    code, out, err = tw(*args)
    if code != 0 or not os.path.exists(dst):
        raise RuntimeError((err or out or 'render failed')[-300:])
    m = WB_LINE.search(out)
    return np.asarray(PILImage.open(dst).convert('RGB')), (m.group(1) if m else None), (m.group(2) if m else None)


def save_scaled(rgb, dst, width=VIEW_W):
    img = PILImage.fromarray(rgb)
    if img.width > width:
        img = img.resize((width, max(1, round(width * img.height / img.width))), PILImage.LANCZOS)
    img.save(dst, optimize=True)
    return img.width, img.height


def run(row, store, outdir, scratch):
    """Crop, solve, render, enhance, render again -- one master, entirely through tianwen verbs."""
    name = row['name'] + '.fits'
    src = os.path.join(store, 'session-masters', name)
    tag = str(abs(hash(row['name'])) % 10 ** 8)
    croppath = os.path.join(scratch, f'tmp_{tag}_crop.fits')
    sharppath = os.path.join(scratch, f'tmp_{tag}_sharp.fits')
    rawfull = os.path.join(scratch, f'tmp_{tag}_raw.png')
    enhfull = os.path.join(scratch, f'tmp_{tag}_enh.png')
    started = time.time()
    try:
        # --margin: the crop here feeds GraXpert, not an eye. The edge walk REFUSES an edge whose
        # band never settles, and a refusal keeps the partial-coverage ramp, which the background
        # model then fits -- on HIP 80609 that left green and blue at 0.98 of the interior for the
        # first ten columns. One percent narrows it to a single column, sub-pixel once scaled.
        code, out, err = tw('image', 'autocrop', src, '-o', croppath, '--margin', '0.02')
        if code != 0 or not os.path.exists(croppath):
            return {**row, 'ok': False, 'stage': 'autocrop', 'error': (err or out)[-300:]}
        cm = CROP_LINE.search(out)
        crop_origin = (int(cm.group(3)), int(cm.group(4))) if cm else (0, 0)
        crop_line = next((l for l in out.splitlines() if '[autocrop]' in l), '').replace('[autocrop] ', '').strip()

        # A WCS, because the colour balance depends on it. MasterPreviewRenderer runs SPCC only
        # when the file carries one; without it the render falls back to sky-background white
        # balance, which neutralises the BACKGROUND and leaves the signal on the raw OSC balance --
        # Rho Ophiuchi came out uniformly green, Antares included.
        #
        # SINCE THE BAKE SOLVES ITS OWN MASTERS this is usually already done, and `image autocrop`
        # carries the solution through (CRPIX shifted by the crop, PIXORIG stamped), so a blind
        # re-solve is the most expensive stage in the run bought for nothing. Ask the file.
        solved = has_wcs(croppath)
        solve_line = 'carried from the master'
        if not solved:
            code, out, _ = tw('solve', croppath, '--update-fits')
            solved = code == 0 and '[solve] wrote WCS' in out
            solve_line = next((l for l in out.splitlines() if l.startswith('[solve] RA=')), '')
            solve_line = solve_line.replace('[solve] ', '').strip() or 'solve failed'

        # THE RAW VIEW FIRST, because its white balance is what the enhanced view inherits.
        raw, wb, wb_source = render(croppath, rawfull)
        shown_h, shown_w = raw.shape[0], raw.shape[1]
        view_w, view_h = save_scaled(raw, os.path.join(outdir, f'{row["id"]}_raw.png'))

        # The 1:1 patch, cut from the raw view at the coordinates build_rows chose. Those are in
        # FULL-canvas space and this is the crop, so the origin the autocrop reported is
        # subtracted; a patch that would fall outside is pulled back inside rather than dropped,
        # since an empty card reads as a broken render.
        px = min(max(0, row['cropX'] - crop_origin[0]), max(0, shown_w - CROP))
        py = min(max(0, row['cropY'] - crop_origin[1]), max(0, shown_h - CROP))
        PILImage.fromarray(raw[py:py + CROP, px:px + CROP]).save(
            os.path.join(outdir, f'{row["id"]}_crop.png'), optimize=True)
        del raw

        code, out, err = tw('image', 'sharpen', croppath, '-o', sharppath, '--ai-backend', 'rc')
        if code != 0 or not os.path.exists(sharppath):
            return {**row, 'ok': False, 'stage': 'sharpen', 'error': (err or out)[-300:],
                    'shownW': shown_w, 'shownH': shown_h, 'viewW': view_w, 'viewH': view_h}

        # THE ENHANCED VIEW ON THE RAW VIEW'S BALANCE. Not a second solve: see the module docstring.
        enh, _, _ = render(sharppath, enhfull, white_balance=wb)
        save_scaled(enh, os.path.join(outdir, f'{row["id"]}_enhanced.png'))
        del enh

        return {**row, 'ok': True, 'seconds': round(time.time() - started, 1),
                'crop': crop_line, 'solved': solved, 'solve': solve_line,
                'shownW': shown_w, 'shownH': shown_h, 'viewW': view_w, 'viewH': view_h,
                'wb': wb, 'wbSource': wb_source}
    except Exception as ex:  # a failed master must not take the batch down
        return {**row, 'ok': False, 'stage': 'exception', 'error': str(ex)[:300]}
    finally:
        for f in (croppath, sharppath, rawfull, enhfull):
            if os.path.exists(f):
                try:
                    os.remove(f)
                except OSError:
                    pass


def main():
    store, rows_path, outdir = sys.argv[1], sys.argv[2], sys.argv[3]
    jobs = int(sys.argv[sys.argv.index('--jobs') + 1]) if '--jobs' in sys.argv else 1
    limit = int(sys.argv[sys.argv.index('--limit') + 1]) if '--limit' in sys.argv else 0
    force = '--force' in sys.argv
    scratch = (sys.argv[sys.argv.index('--scratch') + 1] if '--scratch' in sys.argv
               else os.path.join(tempfile.gettempdir(), 'tw-gallery'))
    os.makedirs(outdir, exist_ok=True)
    os.makedirs(scratch, exist_ok=True)

    rows = json.load(open(rows_path, encoding='utf-8'))
    todo = rows
    if '--names' in sys.argv:
        wanted = set(json.load(open(sys.argv[sys.argv.index('--names') + 1], encoding='utf-8')))
        todo = [r for r in todo if r['name'] in wanted or r['name'] + '.fits' in wanted]
    if not force:
        # A row whose three pictures are all present is done. The enhance is the expensive stage
        # and re-running it over a finished card is an hour of GPU for the same bytes.
        todo = [r for r in todo
                if not all(os.path.exists(os.path.join(outdir, f'{r["id"]}_{k}.png'))
                           for k in ('raw', 'enhanced', 'crop'))]
    if limit:
        todo = todo[:limit]

    free = shutil.disk_usage(scratch).free / 1e9
    print(f'{len(todo)} of {len(rows)} rows to build, {jobs} at a time, '
          f'scratch {scratch} ({free:.0f} GB free)', flush=True)

    by_id = {r['id']: r for r in rows}
    batch_started, results = time.time(), []
    with ThreadPoolExecutor(max_workers=jobs) as pool:
        for r in pool.map(lambda row: run(row, store, outdir, scratch), todo):
            results.append(r)
            by_id[r['id']] = {k: v for k, v in r.items()
                              if k not in ('ok', 'stage', 'error', 'seconds', 'solve', 'crop')}
            state = (f"{r['seconds']:6.1f}s  {'solved' if r.get('solved') else 'NO SOLVE'}  "
                     f"wb {r.get('wb') or '--'} ({r.get('wbSource') or 'none'})" if r['ok']
                     else f"FAILED at {r.get('stage', '?')}: {r.get('error', '')[:110]}")
            print(f"{len(results):3}/{len(todo)} {state}  {r['name'][:52]}", flush=True)

    wall = time.time() - batch_started
    ok = [r for r in results if r['ok']]
    json.dump([by_id[r['id']] for r in rows], open(rows_path, 'w', encoding='utf-8'),
              separators=(',', ':'))
    json.dump(results, open(os.path.join(outdir, 'build-log.json'), 'w'), indent=1, default=str)
    print(f"{len(ok)}/{len(results)} built in {wall / 60:.1f} min "
          f"({wall / max(1, len(ok)):.1f} s per master at {jobs} job(s)); rows -> {rows_path}")
    return 0 if len(ok) == len(results) else 1


if __name__ == '__main__':
    sys.exit(main())
