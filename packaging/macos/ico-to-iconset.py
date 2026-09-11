#!/usr/bin/env python3
"""Extract the largest PNG-compressed frame of a Windows .ico as a .png, with the stdlib only.

Both app icons (src/TianWen.UI.FitsViewer/Resources/MilkyWay.ico, src/TianWen.UI.Gui/Resources/
HelixNebula.ico) carry a 256x256 PNG frame beside four small BMP ones. That frame is the base of the
.iconset that iconutil turns into the bundle's AppIcon.icns; build-dmg.sh resamples it to the other
sizes with sips on the runner. Stdlib only so the same file runs on the ubuntu validate step and on
this machine, where Pillow is not a given.
"""
import struct
import sys

PNG_MAGIC = b"\x89PNG\r\n\x1a\n"


def extract(src: str, dst: str) -> None:
    data = open(src, "rb").read()
    reserved, kind, count = struct.unpack_from("<HHH", data, 0)
    if reserved != 0 or kind != 1:
        raise SystemExit(f"{src}: not a Windows .ico (header {reserved}, {kind})")

    best = None
    for i in range(count):
        width, height, _, _, _, _, size, offset = struct.unpack_from("<BBBBHHII", data, 6 + (16 * i))
        width = width or 256
        height = height or 256
        frame = data[offset:offset + size]
        if frame[:8] == PNG_MAGIC and (best is None or width * height > best[0]):
            best = (width * height, width, height, frame)

    if best is None:
        raise SystemExit(f"{src}: no PNG-compressed frame; add a 256x256 PNG entry to the .ico")

    with open(dst, "wb") as out:
        out.write(best[3])
    print(f"{src}: {best[1]}x{best[2]} PNG frame -> {dst}")


if __name__ == "__main__":
    if len(sys.argv) != 3:
        raise SystemExit("usage: ico-to-iconset.py <in.ico> <out.png>")
    extract(sys.argv[1], sys.argv[2])
