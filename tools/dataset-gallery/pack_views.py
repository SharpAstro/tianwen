"""Pack the rendered views into an artifact's byte budget, without touching the originals.

    python pack_views.py <img dir> <out dir> [--width 1000] [--quality 92]

A PUBLISH CONSTRAINT IS NOT A RENDERING DECISION, which is why this is its own step and not a flag
on `build_views.py`. That script writes lossless PNGs and `check_views.py` measures THOSE; a
compression artifact must never reach the thing that decides whether a card is broken. This copies
them into a form an Artifact can actually hold, and nothing downstream of it is ever measured.

The budget is the reason. 139 masters x three views came to 782 MB of PNG against a 256 MiB cap per
artifact and 64 MB per publish, so publishing the originals is not an option at any quality setting.
Star fields are noise-dominated and PNG cannot compress noise, which is why a 1200 px view costs
~2.9 MB and the biggest cost 6.4 MB.

WHAT IS LOSSLESS AND WHAT IS NOT, decided by what each view is FOR:

* `<id>_crop.png` is the 1:1 window where pixel-level judgement happens (noise floor, Bayer phase,
  a star's core). It is copied verbatim, and it is cheap to do so: all 139 come to 28 MB.
* `<id>_raw` and `<id>_enhanced` are looked at whole, to judge framing, gradient and colour. They
  become JPEG at 4:4:4 (no chroma subsampling -- this gallery is largely judged on COLOUR, and
  subsampling is the one JPEG setting that attacks it directly).

The numbers behind 1000 px and quality 92, measured over eight representative views:

    w1200 q90   178 MB   mean |d| 4.09   max 45
    w1200 q86   138 MB   mean |d| 4.66   max 65
    w1000 q92   137 MB   mean |d| 3.51   max 35     <- chosen
    w1000 q90   118 MB   mean |d| 3.86   max 44

Downscaling first is what makes this work: it removes the sensor noise that JPEG spends its bits on,
so w1000 q92 is both SMALLER and more faithful than w1200 q90. The skill used to say "everything is
PNG, a JPEG at quality 82 puts its ringing exactly at the frame border" -- which remains true of
quality 82, and is why this does not go there. At 92/4:4:4 after a downscale the border band reads
within 3.5 display levels of the original, against the 7-to-36-level bands the edge check exists to
find.

1000 px still answers the question 1200 px was raised for: a 130 px partial-coverage band on a
4108 px master is 31 screen pixels here, against the 16 that made 512 px useless.
"""
import os
import shutil
import sys

from PIL import Image

Image.MAX_IMAGE_PIXELS = None

WIDTH = 1000
QUALITY = 92
# Copied verbatim rather than re-encoded: see the module docstring.
LOSSLESS_SUFFIX = "_crop.png"


def main():
    img_dir, out_dir = sys.argv[1], sys.argv[2]
    width = int(sys.argv[sys.argv.index("--width") + 1]) if "--width" in sys.argv else WIDTH
    quality = int(sys.argv[sys.argv.index("--quality") + 1]) if "--quality" in sys.argv else QUALITY
    os.makedirs(out_dir, exist_ok=True)

    names = sorted(f for f in os.listdir(img_dir) if f.lower().endswith(".png"))
    kept = packed = 0
    for name in names:
        src = os.path.join(img_dir, name)
        if name.endswith(LOSSLESS_SUFFIX):
            shutil.copyfile(src, os.path.join(out_dir, name))
            kept += 1
            continue
        im = Image.open(src).convert("RGB")
        if im.width > width:
            im = im.resize((width, max(1, round(width * im.height / im.width))), Image.LANCZOS)
        im.save(os.path.join(out_dir, name[:-4] + ".jpg"),
                format="JPEG", quality=quality, subsampling=0, optimize=True)
        packed += 1

    total = sum(os.path.getsize(os.path.join(out_dir, f)) for f in os.listdir(out_dir))
    print(f"{packed} views -> JPEG q{quality} at {width}px, {kept} crops copied verbatim")
    print(f"{len(os.listdir(out_dir))} files, {total / 1e6:.0f} MB "
          f"(artifact cap 256 MiB; publish in batches of at most 255 files and ~60 MB)")


if __name__ == "__main__":
    main()
