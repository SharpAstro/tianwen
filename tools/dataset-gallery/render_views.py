"""Render each master's views as SEPARATE PNGs, one file per view.

    python render_views.py <rows.json> <store> <enhanced dir> <out dir>

Writes, per row id:

    <id>_enhanced.png   the cropped, solved, enhanced master
    <id>_raw.png        the SAME crop, solved, unenhanced
    <id>_crop.png       a 1:1 patch of the quietest sky, from the UNCROPPED store master

This replaces the side-by-side sheet render_pairs.py produced. That packed two views into one file
because an Artifact publish takes at most 255 supporting files and three views of ~92 masters is 273
-- but the cap is PER PUBLISH and files left out of a publish are kept, so the same artifact holds as
many as wanted across two or three calls. The sheet cost more than it saved: both halves had to share
one box, so the crop was invisible (every card read as "before and after are the same size"), and the
page could only address a half through a 50 percent background slice.

Everything is PNG. A JPEG at quality 82 is a quarter of the size and puts ringing exactly where this
gallery is judged -- the frame border, which is the thing being inspected for a coverage ramp.

NO IMAGING LOGIC LIVES HERE. Both stretched views come from `tianwen image render`
(MasterPreviewRenderer + StretchSolver), the same path the GPU viewer and `tianwen stack` use, so a
card and the app agree by construction.
"""
import json
import os
import subprocess
import sys
import tempfile

import numpy as np
from PIL import Image as PILImage

# Wider than the old 512-px half: these are looked at to judge a crop edge and a noise floor, and at
# 512 a 130-px border band on a 4108-px master was 16 screen pixels.
VIEW_W = 1200
CROP = 320

EXE = os.environ.get('TIANWEN_EXE') or os.path.normpath(os.path.join(
    os.path.dirname(os.path.abspath(__file__)), '..', '..', 'src', 'TianWen.Cli',
    'bin', 'Release', 'net10.0', 'tianwen.exe'))


def render_rgb(path):
    """A stretched 8-bit RGB view of a FITS, through the product's own renderer."""
    png = os.path.join(tempfile.gettempdir(), 'twview_%d.png' % (abs(hash(path)) % 10 ** 10))
    try:
        proc = subprocess.run([EXE, 'image', 'render', path, '-o', png],
                              capture_output=True, text=True, timeout=1800)
        if proc.returncode != 0 or not os.path.exists(png):
            raise RuntimeError((proc.stderr or proc.stdout or 'render failed')[-300:])
        PILImage.MAX_IMAGE_PIXELS = None
        return np.asarray(PILImage.open(png).convert('RGB'))
    finally:
        if os.path.exists(png):
            os.remove(png)


def save_scaled(rgb, dst, width=VIEW_W):
    img = PILImage.fromarray(rgb)
    if img.width > width:
        img = img.resize((width, max(1, round(width * img.height / img.width))), PILImage.LANCZOS)
    img.save(dst, optimize=True)
    return img.width, img.height


def main():
    rows = json.load(open(sys.argv[1], encoding='utf-8'))
    store, enhanced_dir, outdir = sys.argv[2], sys.argv[3], sys.argv[4]
    os.makedirs(outdir, exist_ok=True)
    rawhalf_dir = os.path.join(os.path.dirname(os.path.normpath(enhanced_dir)), 'rawhalf')

    for i, r in enumerate(rows, 1):
        name = r['name'] + '.fits'
        enhanced_path = os.path.join(enhanced_dir, name)
        if not os.path.exists(enhanced_path):
            print(f'{i}/{len(rows)} no enhanced master for {r["name"][:50]}', flush=True)
            continue

        enh = render_rgb(enhanced_path)
        r['shownW'], r['shownH'] = int(enh.shape[1]), int(enh.shape[0])
        w, h = save_scaled(enh, os.path.join(outdir, f'{r["id"]}_enhanced.png'))
        r['viewW'], r['viewH'] = w, h

        # The "before" view is rendered by the BATCH from the cropped-and-solved file, while that file
        # still exists: keeping every one as FITS would be ~9 GB, and re-rendering from the store
        # master here would lose both the crop and the solve, so the two views would disagree on
        # framing and on colour. Fall back to the store master where no such PNG exists.
        rawpng = os.path.join(rawhalf_dir, r['name'] + '.png')
        raw_src = np.asarray(PILImage.open(rawpng).convert('RGB')) if os.path.exists(rawpng) else None
        store_rgb = None
        if raw_src is None:
            store_rgb = render_rgb(os.path.join(store, 'session-masters', name))
            raw_src = store_rgb
        save_scaled(raw_src, os.path.join(outdir, f'{r["id"]}_raw.png'))

        # The 1:1 patch stays on the FULL store master: it answers a noise and Bayer-phase question at
        # the patch the manifest chose, and those coordinates are in full-canvas space. Enhanced pixels
        # have had the noise taken out, so they cannot answer it at all.
        if store_rgb is None:
            store_rgb = render_rgb(os.path.join(store, 'session-masters', name))
        cy, cx = r['cropY'], r['cropX']
        patch = store_rgb[cy:cy + CROP, cx:cx + CROP]
        PILImage.fromarray(patch).save(os.path.join(outdir, f'{r["id"]}_crop.png'), optimize=True)

        print(f'{i}/{len(rows)} {r["id"]}  {w}x{h}  {r["object"][:40]}', flush=True)

    json.dump(rows, open(sys.argv[1], 'w', encoding='utf-8'), separators=(',', ':'))
    print('wrote', sys.argv[1])


if __name__ == '__main__':
    main()
