"""Bakes the GUI's application icon, src/TianWen.UI.Gui/Resources/TianWen.ico, from the SharpAstro mark.

The mark is the one the organisation's site draws (sharpastro.github.io, index.html, the `.mark` SVG): a moon with a
bite taken out of it, lit from the upper left, and the sharp sign in the logo's orange where the bite is. It is drawn
here from the same numbers, with Pillow alone, so the icon can be made again on any machine and no SVG rasteriser
decides how it looks. Change the mark on the site and here together.

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

from PIL import Image, ImageChops, ImageDraw

VIEW = 32.0          # the SVG's viewBox is 0 0 32 32
SUPER = 1024         # drawn at this size, then reduced: Pillow's shapes are not antialiased
SIZES = [256, 64, 48, 32, 24, 16]

MOON = (14.6, 17.4, 12.2)      # cx, cy, r
BITE = (24.5, 8.0, 10.4)
# The gradient "umbra": objectBoundingBox units over the moon's box, from (0.15, 0.15) to (0.85, 0.95).
STOPS = [(0.0, (0xff, 0xc9, 0x8a)), (0.45, (0xd4, 0x55, 0x2a)), (1.0, (0x5c, 0x14, 0x09))]
SHARP = (0xe0, 0x4a, 0x00, 0xff)   # --mark on a dark page, tianwen's NightPalette.BodyText


def scale(v):
    return v * SUPER / VIEW


def circle(draw, cx, cy, r, fill):
    draw.ellipse([scale(cx - r), scale(cy - r), scale(cx + r), scale(cy + r)], fill=fill)


def gradient():
    """The moon's fill over the whole canvas: a linear gradient along the SVG's gradient vector."""
    cx, cy, r = MOON
    x0, y0, size = cx - r, cy - r, 2 * r
    ax, ay = x0 + 0.15 * size, y0 + 0.15 * size
    bx, by = x0 + 0.85 * size, y0 + 0.95 * size
    dx, dy = bx - ax, by - ay
    length2 = dx * dx + dy * dy

    # One row of the ramp, then every pixel reads it by its position along the vector.
    ramp = []
    for i in range(256):
        t = i / 255.0
        for (t0, c0), (t1, c1) in zip(STOPS, STOPS[1:]):
            if t <= t1:
                u = (t - t0) / (t1 - t0)
                ramp.append(tuple(round(c0[k] + (c1[k] - c0[k]) * u) for k in range(3)))
                break

    image = Image.new("RGB", (SUPER, SUPER))
    pixels = image.load()
    unit = VIEW / SUPER
    for y in range(SUPER):
        py = (y + 0.5) * unit
        for x in range(SUPER):
            px = (x + 0.5) * unit
            t = ((px - ax) * dx + (py - ay) * dy) / length2
            pixels[x, y] = ramp[min(255, max(0, int(t * 255.0 + 0.5)))]
    return image


def rounded(draw, x, y, w, h, rx, fill):
    draw.rounded_rectangle([scale(x), scale(y), scale(x + w), scale(y + h)], radius=scale(rx), fill=fill)


def mark():
    # The moon: the gradient, shown where the disc is and the bite is not.
    disc = Image.new("L", (SUPER, SUPER), 0)
    circle(ImageDraw.Draw(disc), *MOON, 255)
    bite = Image.new("L", (SUPER, SUPER), 255)
    circle(ImageDraw.Draw(bite), *BITE, 0)
    moon = gradient().convert("RGBA")
    moon.putalpha(ImageChops.multiply(disc, bite))

    # The sharp: two uprights, and two bars turned 12 degrees anticlockwise about (24.2, 8.9).
    uprights = Image.new("RGBA", (SUPER, SUPER), (0, 0, 0, 0))
    draw = ImageDraw.Draw(uprights)
    rounded(draw, 20.4, 1.6, 1.6, 14.6, 0.8, SHARP)
    rounded(draw, 26.4, 1.6, 1.6, 14.6, 0.8, SHARP)

    bars = Image.new("RGBA", (SUPER, SUPER), (0, 0, 0, 0))
    draw = ImageDraw.Draw(bars)
    rounded(draw, 17.4, 5.2, 13.6, 2.7, 1.35, SHARP)
    rounded(draw, 17.4, 10.8, 13.6, 2.7, 1.35, SHARP)
    # SVG's rotate(-12) is anticlockwise on screen, which is Pillow's positive angle.
    bars = bars.rotate(12, resample=Image.BICUBIC, center=(scale(24.2), scale(8.9)))

    image = Image.alpha_composite(moon, uprights)
    return Image.alpha_composite(image, bars)


def png_frame(image):
    data = io.BytesIO()
    image.save(data, format="PNG", optimize=True)
    return data.getvalue()


def bitmap_frame(image):
    """A 32-bit DIB as an .ico holds it: the header with the height doubled, the rows bottom up, then an empty AND mask."""
    size = image.width
    header = struct.pack("<IiiHHIIiiII", 40, size, size * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    blue, green, red, alpha = image.split()[2], image.split()[1], image.split()[0], image.split()[3]
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
    out = os.path.join(repo, "src", "TianWen.UI.Gui", "Resources", "TianWen.ico")
    image = mark()
    frames = [image.resize((s, s), Image.LANCZOS) for s in SIZES]
    write_ico(out, frames)
    print(out)
    if len(sys.argv) > 1:
        frames[0].save(sys.argv[1])
        print(sys.argv[1])


if __name__ == "__main__":
    main()
