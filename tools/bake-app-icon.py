"""Bakes the GUI's application icon, src/TianWen.UI.Gui/Resources/TianWen.ico, from the SharpAstro moon.

The moon is the organisation's picture on GitHub (github.com/SharpAstro): a photograph of the eclipsed Moon on a black
ground. The source kept beside the icon, Resources/SharpAstroMoon.png, is the owner's original of it (900 px, the lunar
eclipse of 2022-11-08). An icon wants the disc alone, so the black ground is made transparent here: the disc is found by
its own edge, everything outside it is cut away with a soft rim, and the disc is cropped to fill the frame.

    python tools/bake-app-icon.py            # writes the .ico
    python tools/bake-app-icon.py out.png    # also writes the 256 px frame, to look at

The .ico carries a 256 px PNG frame and 64, 48, 32, 24 and 16 px frames as 32-bit bitmaps, the shape the icons here have
always had: packaging/macos/ico-to-iconset.py takes the PNG frame as the base of the macOS icon, and a small frame as a
bitmap is read by every Windows API, old ones included. The ground is transparent in every frame.
"""

import io
import os
import struct
import sys

from PIL import Image, ImageDraw

SIZES = [256, 64, 48, 32, 24, 16]
SUPER = 4            # the rim's mask is drawn this many times larger, then reduced: Pillow's shapes are not antialiased
GROUND = 16          # a pixel brighter than this (of 255) is the Moon, not the black ground
INSET = 0.012        # of the radius, the rim is taken inside the measured edge: the limb is not an exact circle, and
                     # a rim on the edge itself left a dark fringe along one side on a light ground
MARGIN = 0.02        # of the frame, left clear around the disc


def disc_of(image):
    """The Moon's centre and radius in source pixels, from the box of everything brighter than the ground."""
    left, top, right, bottom = image.convert("L").point(lambda v: 255 if v > GROUND else 0).getbbox()
    radius = ((right - left) + (bottom - top)) / 4.0
    return (left + right) / 2.0, (top + bottom) / 2.0, radius


def moon(source):
    image = Image.open(source).convert("RGB")
    cx, cy, radius = disc_of(image)
    radius *= 1.0 - INSET

    mask = Image.new("L", (image.width * SUPER, image.height * SUPER), 0)
    ImageDraw.Draw(mask).ellipse(
        [(cx - radius) * SUPER, (cy - radius) * SUPER, (cx + radius) * SUPER, (cy + radius) * SUPER], fill=255)
    cut = image.convert("RGBA")
    cut.putalpha(mask.resize(image.size, Image.LANCZOS))

    half = radius / (1.0 - 2.0 * MARGIN)
    box = [round(cx - half), round(cy - half), round(cx + half), round(cy + half)]
    return cut.crop(box)


def png_frame(image):
    data = io.BytesIO()
    image.save(data, format="PNG", optimize=True)
    return data.getvalue()


def bitmap_frame(image):
    """A 32-bit DIB as an .ico holds it: the header with the height doubled, the rows bottom up, then an empty AND mask."""
    size = image.width
    header = struct.pack("<IiiHHIIiiII", 40, size, size * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    red, green, blue, alpha = image.split()
    rows = Image.merge("RGBA", (blue, green, red, alpha)).transpose(Image.FLIP_TOP_BOTTOM).tobytes()
    mask_row = ((size + 31) // 32) * 4
    return header + rows + bytes(mask_row * size)


def write_ico(path, frames):
    blobs = [png_frame(f) if f.width >= 256 else bitmap_frame(f) for f in frames]
    offset = 6 + 16 * len(frames)
    with open(path, "wb") as out:
        out.write(struct.pack("<HHH", 0, 1, len(frames)))
        for frame, blob in zip(frames, blobs):
            side = frame.width if frame.width < 256 else 0
            out.write(struct.pack("<BBBBHHII", side, side, 0, 0, 1, 32, len(blob), offset))
            offset += len(blob)
        for blob in blobs:
            out.write(blob)


def main():
    repo = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    resources = os.path.join(repo, "src", "TianWen.UI.Gui", "Resources")
    out = os.path.join(resources, "TianWen.ico")
    image = moon(os.path.join(resources, "SharpAstroMoon.png"))
    frames = [image.resize((s, s), Image.LANCZOS) for s in SIZES]
    write_ico(out, frames)
    print(out)
    if len(sys.argv) > 1:
        frames[0].save(sys.argv[1])
        print(sys.argv[1])


if __name__ == "__main__":
    main()
