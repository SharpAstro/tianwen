"""Compose each master's two views into ONE image: the enhanced crop beside the raw master.

    python render_pairs.py <rows.json> <store> <enhanced dir> <out dir> [previous store]

A published artifact takes at most 255 supporting files, and three views of 92 masters is 276, so the
two full-frame views share a file: left half the enhanced crop, right half the raw master as the
stacker wrote it. Both are scaled to the same width and letterboxed to the same height, so either
half is addressable in CSS as a 50% background slice with one aspect ratio for both.

Both views go through `tianwen image render` (MasterPreviewRenderer + StretchSolver, the same path
the viewer and `tianwen stack` use), each rendered separately: the enhanced master has had its background flattened and its noise pulled down, so
forcing the raw frame's curve onto it would show the enhancement as a level shift rather than as what
it did to the pixels.
"""
import json
import os
import subprocess
import tempfile
import sys

import numpy as np
from PIL import Image as PILImage

HALF_W = 512
GROUND = (5, 7, 10)   # the page's own ground, so any fill is invisible
# The Release CLI. Override with TIANWEN_EXE; the default is this repo's own build output,
# resolved from the script's location so a checkout anywhere works.
EXE = os.environ.get('TIANWEN_EXE') or os.path.normpath(os.path.join(
    os.path.dirname(os.path.abspath(__file__)), '..', '..', 'src', 'TianWen.Cli',
    'bin', 'Release', 'net10.0', 'tianwen.exe'))


def read_rgb(path):
    """A stretched 8-bit RGB view, rendered by `tianwen image render`.

    This used to carry its own stretch in numpy -- median minus 2.8 MAD to black, midtones to 0.25 --
    and it was both wrong and worse. Wrong because nothing else in the project renders that way, so a
    card could not be compared against what the viewer shows; worse because the curve was far harder
    than the product's, washing the background to light grey, blowing the Orion core to a white blob,
    and amplifying a sub-sigma edge (+1.4 sigma at worst, measured) into a visible border.

    `image render` is MasterPreviewRenderer, the same component `tianwen stack` uses for its
    master_*.png companion and the same StretchSolver the GPU viewer draws through. Rendering here
    instead means a gallery card and the app agree by construction, which is the only reason to trust
    one as a picture of the other.
    """
    png = os.path.join(tempfile.gettempdir(), 'twgal_%d.png' % (abs(hash(path)) % 10**10))
    try:
        proc = subprocess.run([EXE, 'image', 'render', path, '-o', png],
                              capture_output=True, text=True, timeout=1800)
        if proc.returncode != 0 or not os.path.exists(png):
            raise RuntimeError((proc.stderr or proc.stdout or 'render failed')[-300:])
        return np.asarray(PILImage.open(png).convert('RGB'))
    finally:
        if os.path.exists(png):
            os.remove(png)


def scaled(rgb):
    img = PILImage.fromarray(rgb)
    return img.resize((HALF_W, max(1, round(HALF_W * img.height / img.width))), PILImage.LANCZOS)


CROP = 256


def main():
    rows = json.load(open(sys.argv[1], encoding='utf-8'))
    store, enhanced_dir, outdir = sys.argv[2], sys.argv[3], sys.argv[4]
    previous = sys.argv[5] if len(sys.argv) > 5 else None
    os.makedirs(outdir, exist_ok=True)
    # The 09-16 crop is worth a file only where that bake showed the Bayer-phase pattern; publishing
    # all of them would put the artifact over its 255-file budget for a comparison that is flat noise
    # on most sessions.
    compare = {r['name'] for r in sorted(rows, key=lambda x: -(x.get('prevAmp') or 0))[:24] if r.get('prevAmp')}
    for i, r in enumerate(rows, 1):
        name = r['name'] + '.fits'
        enhanced_path = os.path.join(enhanced_dir, name)
        raw_path = os.path.join(store, 'session-masters', name)
        if not os.path.exists(enhanced_path):
            print(f'{i}/{len(rows)} no enhanced master for {r["name"][:50]}', flush=True)
            continue
        # The raw half is rendered by the BATCH, from the cropped-and-solved file, while that file
        # still exists: keeping 92 of them as FITS would be 9 GB of scratch, and re-rendering from the
        # store's master here would lose both the crop and the plate solve, so the halves would
        # disagree on framing and on colour. Fall back to the store master where no such PNG exists.
        rawhalf = os.path.join(os.path.dirname(os.path.normpath(enhanced_dir)), 'rawhalf', r['name'] + '.png')
        half_rgb = np.asarray(PILImage.open(rawhalf).convert('RGB')) if os.path.exists(rawhalf) else None
        # The 1:1 crop stays on the FULL store master: its job is a noise and Bayer-phase judgement at
        # the patch the first render chose, and those coordinates are in full-canvas space. Enhanced
        # pixels have had the noise taken out, so they cannot answer that question at all.
        raw_rgb = read_rgb(raw_path)
        cy, cx = r['cropY'], r['cropX']
        PILImage.fromarray(raw_rgb[cy:cy + CROP, cx:cx + CROP]).save(
            os.path.join(outdir, f'{r["id"]}c.png'), optimize=True)
        if previous and r['name'] in compare:
            twin = os.path.join(previous, 'session-masters', name)
            if os.path.exists(twin):
                prev_rgb = read_rgb(twin)
                if prev_rgb.shape == raw_rgb.shape:
                    PILImage.fromarray(prev_rgb[cy:cy + CROP, cx:cx + CROP]).save(
                        os.path.join(outdir, f'{r["id"]}p.png'), optimize=True)
                    r['hasPrev'] = True
                else:
                    r['hasPrev'] = False
            else:
                r['hasPrev'] = False
        else:
            r['hasPrev'] = False
        # The two halves share one file, so they share one box, and the ENHANCED half sets it: it is
        # the cropped picture, the one the card shows, and it must never be padded. Letterboxing the
        # shorter half was harmless while the crop trimmed almost nothing; once `image autocrop`
        # started trimming properly the enhanced half became visibly shorter than the full canvas,
        # and the padding rendered as black bars along the top and bottom of every card -- read,
        # reasonably, as the coverage ring still being there. The raw half is centre-cropped to the
        # same box instead: it keeps its full width, ring included, and gives up a sliver top and
        # bottom, which costs a preview nothing.
        left, right = scaled(read_rgb(enhanced_path)), scaled(half_rgb if half_rgb is not None else raw_rgb)
        height = left.height
        if right.height > height:
            top = (right.height - height) // 2
            right = right.crop((0, top, HALF_W, top + height))
        sheet = PILImage.new('RGB', (HALF_W * 2, height), GROUND)
        sheet.paste(left, (0, 0))
        sheet.paste(right, (HALF_W, (height - right.height) // 2))
        sheet.save(os.path.join(outdir, f'{r["id"]}.jpg'), quality=82, optimize=True)
        r['pairH'] = height
        print(f'{i}/{len(rows)} {r["id"]}.jpg {HALF_W * 2}x{height}  {r["object"][:40]}', flush=True)
    json.dump(rows, open(sys.argv[1], 'w', encoding='utf-8'), separators=(',', ':'))
    print('wrote', sys.argv[1])


if __name__ == '__main__':
    main()
