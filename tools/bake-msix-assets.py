#!/usr/bin/env python
"""Bake the MSIX logo assets for tianwen-fits from the app icon.

The package needs PNGs at fixed nominal sizes; the app has one .ico. Rather than commit a
pile of hand-exported images whose provenance nobody can reconstruct, this is the recipe and
the PNGs beside it are its output -- the same posture as the baked icon table, where the
recipe is checked in next to what it produced.

Run it after changing Resources/MilkyWay.ico, then commit the regenerated Assets/:

    python tools/bake-msix-assets.py

Two things decided here rather than in the manifest:

* **Nothing is upscaled.** The .ico's largest frame is 256x256, so the sizes MSIX would like
  above that (Square310x310Logo, Wide310x150Logo, and the scale-200 of Square150x150) are
  deliberately NOT generated. Windows downscales a smaller asset cleanly and upscaling would
  ship a visibly soft tile; neither of those assets is required for certification. Raise the
  source art past 256 first if they are ever wanted.

* **Wide310x150 is skipped for a second reason**: it is the only non-square asset, so it
  cannot be produced from a square source without either letterboxing or cropping the art.
  That is an art decision, not a resampling one.

The source frame is fully opaque full-bleed art (verified: alpha is 255 everywhere), so the
tiles read as a square image and the manifest's BackgroundColor is only visible in the thin
places Windows composites around it.
"""

import os
import sys

try:
    from PIL import Image
except ImportError:
    sys.exit("Pillow is required: python -m pip install Pillow")

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SOURCE = os.path.join(REPO, "src", "TianWen.UI.FitsViewer", "Resources", "MilkyWay.ico")
OUT = os.path.join(REPO, "packaging", "windows", "msix", "Assets")

# name -> pixel size. Scale variants use MSIX's own qualifier syntax, so Windows picks the
# right one per display scaling without the manifest naming them.
ASSETS = {
    "StoreLogo.png": 50,
    "StoreLogo.scale-200.png": 100,
    "Square44x44Logo.png": 44,
    "Square44x44Logo.scale-200.png": 88,
    # targetsize variants are what the shell uses for the file-type association icon and the
    # taskbar, at the exact sizes it asks for. 256 is the one Explorer's extra-large view wants.
    "Square44x44Logo.targetsize-16.png": 16,
    "Square44x44Logo.targetsize-24.png": 24,
    "Square44x44Logo.targetsize-32.png": 32,
    "Square44x44Logo.targetsize-48.png": 48,
    "Square44x44Logo.targetsize-256.png": 256,
    "Square71x71Logo.png": 71,
    "Square71x71Logo.scale-200.png": 142,
    "Square150x150Logo.png": 150,
}

MAX_SOURCE = 256


def main():
    if not os.path.isfile(SOURCE):
        sys.exit("source icon not found: " + SOURCE)

    icon = Image.open(SOURCE)
    # Pick the 256 frame explicitly. Pillow's default for a multi-frame .ico is the largest,
    # but saying which one we mean keeps this correct if a bigger frame is ever added.
    icon.size = (MAX_SOURCE, MAX_SOURCE)
    src = icon.convert("RGBA")

    os.makedirs(OUT, exist_ok=True)
    for name, size in sorted(ASSETS.items(), key=lambda kv: (kv[1], kv[0])):
        if size > MAX_SOURCE:
            sys.exit("refusing to upscale %s to %dpx from a %dpx source" % (name, size, MAX_SOURCE))
        img = src if size == MAX_SOURCE else src.resize((size, size), Image.LANCZOS)
        path = os.path.join(OUT, name)
        img.save(path, "PNG", optimize=True)
        print("%-40s %4dx%-4d %7d bytes" % (name, size, size, os.path.getsize(path)))

    print("\n%d assets written to %s" % (len(ASSETS), os.path.relpath(OUT, REPO)))


if __name__ == "__main__":
    main()
