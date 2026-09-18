"""Crop each session master to its covered rectangle and enhance it, through tianwen verbs only.

    python batch_enhance.py <store> <outdir> [--jobs N] [--limit N] [--names names.json]

Per master: `tianwen image autocrop`, then `tianwen image sharpen --ai-backend rc`, then the enhanced
FITS is kept only long enough for the gallery renderer to read it -- both intermediates are ~120 MB,
so they are deleted as soon as the pair is done.

NO IMAGING LOGIC LIVES HERE. It used to carry its own coverage crop, which could not see a drizzle
edge, and it called a sharpen verb that ran neither the deblur nor the gradient correction the viewer
runs. Python is for orchestration; anything load-bearing belongs in the product, where the viewer,
the stacker and this script all reach the same implementation.

Jobs run concurrently against ONE GPU. More jobs only help while the card is not the bottleneck, so
the batch prints per-master wall time and the script is worth re-timing at a different N rather than
assumed.
"""
import json
import os
import subprocess
import sys
import time
from concurrent.futures import ThreadPoolExecutor

from PIL import Image as PILImage


# The Release CLI. Override with TIANWEN_EXE; the default is this repo's own build output,
# resolved from the script's location so a checkout anywhere works.
EXE = os.environ.get('TIANWEN_EXE') or os.path.normpath(os.path.join(
    os.path.dirname(os.path.abspath(__file__)), '..', '..', 'src', 'TianWen.Cli',
    'bin', 'Release', 'net10.0', 'tianwen.exe'))


def run(name, store, outdir, rawhalf_only=False):
    """Crop and enhance ONE master, entirely through tianwen verbs.

    `rawhalf_only` stops after the raw half, keeping the enhanced FITS that is already on disk. The
    raw half is the one output that depends on the RENDERER rather than on the enhancers, so a fix
    to `image render` invalidates it alone -- redoing the enhance to pick up a render fix would cost
    an hour of GPU for a picture that takes seconds. It still re-runs the crop and the solve,
    because the half has to agree with the enhanced half on framing and on colour, and neither the
    cropped file nor its WCS is kept.

    This used to carry its own coverage crop in Python and call `image sharpen`, which between them
    got two things wrong that the product already had right: the crop rule could not see a drizzle
    edge (these masters are absent ~1 percent everywhere, so a fixed 2 percent threshold stops while
    the band is still 20 percent empty), and `image sharpen` assembled its own step list starting at
    RemoveStarsStep, so it ran neither the whole-frame deblur nor the gradient correction that the
    viewer's Enhance button and `tianwen stack --enhance` have always run.

    Both are fixed in the product now: `image autocrop` calls ViewerActions.ScanForCrop, the same
    scan the viewer uses, and `image sharpen` takes the head of SharpenRequest.Canonical /
    DeblurFirst. So the gallery is a consumer of the shipped verbs and holds no imaging logic of its
    own -- which is the only way the picture here can be trusted to match the picture in the app.
    """
    src = os.path.join(store, 'session-masters', name)
    stem = name[:-5]
    tag = str(abs(hash(stem)) % 10**8)
    croppath = os.path.join(outdir, f'tmp_{tag}_crop.fits')
    sharppath = os.path.join(outdir, f'tmp_{tag}_sharp.fits')
    started = time.time()
    try:
        # --margin: the crop here feeds GraXpert, not an eye. The edge walk REFUSES an edge whose
        # band never settles, and a refusal keeps the partial-coverage ramp, which the background
        # model then fits -- on HIP 80609 that left green and blue at 0.98 of the interior for the
        # first ten columns. One percent narrows it to a single column, sub-pixel once scaled.
        proc = subprocess.run([EXE, 'image', 'autocrop', src, '-o', croppath, '--margin', '0.02'],
                              capture_output=True, text=True, timeout=3600)
        if proc.returncode != 0 or not os.path.exists(croppath):
            return {'name': stem, 'ok': False, 'stage': 'autocrop',
                    'error': (proc.stderr or proc.stdout or '')[-300:]}
        crop_line = next((l for l in proc.stdout.splitlines() if '[autocrop]' in l and '->' in l), '')

        # SOLVE, because the colour balance depends on it. MasterPreviewRenderer runs SPCC only when
        # the file carries a WCS, and a dataset-bake session master carries none -- `tianwen stack`
        # plate-solves its masters, the bake does not. Without it the render falls back to sky-
        # background white balance, which neutralises the BACKGROUND and leaves the signal on the raw
        # OSC balance: Rho Ophiuchi came out uniformly green, Antares included. GraXpert does not fix
        # this and is not meant to -- background extraction removes the gradient per plane and adds
        # each plane's own median back, deliberately preserving the levels. One solve is enough for
        # both halves, because `image sharpen` carries the WCS through to its output.
        proc = subprocess.run([EXE, 'solve', croppath, '--update-fits'],
                              capture_output=True, text=True, timeout=3600)
        solved = proc.returncode == 0 and '[solve] wrote WCS' in (proc.stdout or '')
        solve_line = next((l for l in proc.stdout.splitlines() if l.startswith('[solve] RA=')), '')

        # The raw half is rendered NOW, while the cropped-and-solved file still exists: keeping 92 of
        # them as FITS would be 9 GB of scratch for a picture that is 512 px wide.
        rawpng = os.path.join(outdir, 'rawhalf', stem + '.png')
        proc = subprocess.run([EXE, 'image', 'render', croppath, '-o', rawpng],
                              capture_output=True, text=True, timeout=3600)
        if proc.returncode == 0 and os.path.exists(rawpng):
            im = PILImage.open(rawpng).convert('RGB')
            im.resize((1024, max(1, round(1024 * im.height / im.width))), PILImage.LANCZOS).save(rawpng)

        if rawhalf_only:
            return {'name': stem, 'ok': True, 'seconds': round(time.time() - started, 1),
                    'crop': crop_line.replace('[autocrop] ', '').strip(),
                    'solved': solved, 'solve': solve_line.replace('[solve] ', '').strip(),
                    'rawhalf_only': True}

        proc = subprocess.run([EXE, 'image', 'sharpen', croppath, '-o', sharppath, '--ai-backend', 'rc'],
                              capture_output=True, text=True, timeout=3600)
        if proc.returncode != 0 or not os.path.exists(sharppath):
            return {'name': stem, 'ok': False, 'stage': 'sharpen',
                    'error': (proc.stderr or proc.stdout or '')[-300:]}

        os.replace(sharppath, os.path.join(outdir, 'enhanced', stem + '.fits'))
        return {'name': stem, 'ok': True, 'seconds': round(time.time() - started, 1),
                'crop': crop_line.replace('[autocrop] ', '').strip(),
                'solved': solved, 'solve': solve_line.replace('[solve] ', '').strip()}
    except Exception as ex:  # a failed master must not take the batch down
        return {'name': stem, 'ok': False, 'error': str(ex)[:300]}
    finally:
        for f in (croppath, sharppath):
            if os.path.exists(f):
                os.remove(f)


def main():
    store, outdir = sys.argv[1], sys.argv[2]
    jobs = int(sys.argv[sys.argv.index('--jobs') + 1]) if '--jobs' in sys.argv else 1
    limit = int(sys.argv[sys.argv.index('--limit') + 1]) if '--limit' in sys.argv else 0
    rawhalf_only = '--rawhalf-only' in sys.argv
    os.makedirs(os.path.join(outdir, 'enhanced'), exist_ok=True)
    os.makedirs(os.path.join(outdir, 'rawhalf'), exist_ok=True)
    names = sorted(n for n in os.listdir(os.path.join(store, 'session-masters')) if n.lower().endswith('.fits'))
    if '--names' in sys.argv:
        wanted = set(json.load(open(sys.argv[sys.argv.index('--names') + 1], encoding='utf-8')))
        names = [n for n in names if n in wanted]
    # An enhanced master already on disk is skipped -- except in --rawhalf-only, where its presence is
    # the whole point: that mode redoes the half BESIDE an enhance that is already good.
    if not rawhalf_only:
        done = {f[:-5] for f in os.listdir(os.path.join(outdir, 'enhanced'))}
        names = [n for n in names if n[:-5] not in done]
    if limit:
        names = names[:limit]

    print(f'{len(names)} masters to {"re-render the raw half of" if rawhalf_only else "enhance"}, '
          f'{jobs} at a time', flush=True)
    batch_started = time.time()
    results = []
    with ThreadPoolExecutor(max_workers=jobs) as pool:
        for r in pool.map(lambda n: run(n, store, outdir, rawhalf_only), names):
            results.append(r)
            state = (f"{r['seconds']:6.1f}s  {'solved' if r.get('solved') else 'NO SOLVE'}  {r['crop']}" if r['ok']
                     else f"FAILED at {r.get('stage', '?')}: {r.get('error', '')[:110]}")
            print(f"{len(results):3}/{len(names)} {state}  {r['name'][:60]}", flush=True)
    wall = time.time() - batch_started
    ok = [r for r in results if r['ok']]
    print(f"{len(ok)}/{len(results)} enhanced in {wall/60:.1f} min "
          f"({wall/max(1, len(ok)):.1f} s per master at {jobs} job(s))")
    json.dump(results, open(os.path.join(outdir, 'enhance-log.json'), 'w'), indent=1)


if __name__ == '__main__':
    main()
