# Regenerates FONT-LICENSES.txt at the repository root.
#
# WHY THIS EXISTS
#
# The GUI bundles two font files as Content, so every published binary REDISTRIBUTES them. Both
# licences (SIL OFL 1.1 for Noto, Bitstream Vera for DejaVu) permit that on condition the copyright
# and licence notice travel with the font -- and neither of the repo's two existing notices files
# could carry them:
#
#   - THIRD-PARTY-NOTICES.txt is generated from project.assets.json, so it covers the NuGet graph
#     ONLY. A bundled Content file is structurally invisible to it.
#   - NOTICE covers methods, data and separately-invoked programs, and says so; it is attribution
#     rather than licence text, and explicitly "does not grant or restrict anything".
#
# So the fonts shipped with no licence text and no attribution in either file. This closes that.
#
# WHY IT READS THE FONTS RATHER THAN FETCHING THE LICENCES
#
# Every word of the output is extracted from the font's own OpenType `name` table (records 0, 13 and
# 14). That makes it the licence THOSE EXACT FILES declare, rather than a copy fetched from upstream
# that may describe a different version of the font than the one committed here -- and it means the
# text is never hand-written, which for a licence is the difference between a notice and a guess.
# DejaVu embeds the full Bitstream Vera licence in record 13, so the complete text really is in there.
#
# Follows the astap-readme.txt precedent recorded in src/Directory.Build.targets: NOTICE cites a file,
# therefore that file has to ship, or the notice points at something not present.
#
# Usage: python tools/extract-font-licences.py   (from the repository root)

import io
import os
import struct
import sys

# Font file, and what it is for. Order is the output order.
FONTS = [
    ("Noto-COLRv1.ttf", "src/TianWen.UI.Gui/Fonts/Noto-COLRv1.ttf",
     "Colour emoji, used for tab icons and emoji in text."),
    ("DejaVuSans.ttf", "src/TianWen.UI.Gui/Fonts/DejaVuSans.ttf",
     "UI text face, preferred over the platform default."),
]

# OpenType name IDs worth shipping. 1..12 and 15+ are family/style/vendor metadata, not licence.
WANTED = ((0, "Copyright"), (13, "Licence"), (14, "Licence URL"))

OUT_PATH = "FONT-LICENSES.txt"


def name_records(path):
    """The font's name table, as {nameID: string}, keeping the longest variant of each ID."""
    with open(path, "rb") as handle:
        data = handle.read()

    _, num_tables = struct.unpack(">IH", data[:6])
    offset, tables = 12, {}
    for _ in range(num_tables):
        tag, _checksum, table_offset, table_len = struct.unpack(">4sIII", data[offset:offset + 16])
        offset += 16
        tables[tag.decode("latin1")] = (table_offset, table_len)

    if "name" not in tables:
        raise SystemExit(f"{path}: no name table, so it declares no licence")

    base, _ = tables["name"]
    _fmt, count, strings = struct.unpack(">HHH", data[base:base + 6])

    found, cursor = {}, base + 6
    for _ in range(count):
        platform, encoding, _lang, name_id, length, str_offset = struct.unpack(
            ">HHHHHH", data[cursor:cursor + 12])
        cursor += 12
        if name_id not in (nid for nid, _ in WANTED):
            continue
        raw = data[base + strings + str_offset: base + strings + str_offset + length]
        try:
            # Platform 3 (Windows) is UTF-16BE; platform 1 (Mac) is MacRoman, close enough to latin1
            # for licence text, which is ASCII in practice.
            text = raw.decode("utf-16-be") if platform == 3 else raw.decode("latin1")
        except UnicodeDecodeError:
            continue
        # Longest wins: a font often carries the same record for several platforms, and a truncated
        # Mac variant beside a full Windows one would silently ship the short version.
        if name_id not in found or len(text) > len(found[name_id]):
            found[name_id] = text
    return found


def main():
    if not os.path.isdir("src"):
        raise SystemExit("run this from the repository root")

    out = io.StringIO()
    out.write("TianWen -- bundled font licences\n")
    out.write("================================\n\n")
    out.write("The GUI bundles the font files below and therefore REDISTRIBUTES them, which each of their\n")
    out.write("licences permits on condition that the copyright and licence notice travel with the font.\n")
    out.write("This file is that notice.\n\n")
    out.write("Every word below is extracted verbatim from the font files' own OpenType `name` table\n")
    out.write("(records 0, 13 and 14), so it is the licence text those exact files declare rather than a\n")
    out.write("copy fetched from elsewhere that could describe a different version. Re-extract with\n")
    out.write("tools/extract-font-licences.py after replacing a font.\n\n")
    out.write("These are the fonts only. TianWen itself is under LICENSE (AGPL-3.0-or-later); the\n")
    out.write("statically linked package graph is in THIRD-PARTY-NOTICES.txt; methods, data and\n")
    out.write("separately-invoked programs are in NOTICE.\n")

    for label, path, purpose in FONTS:
        if not os.path.exists(path):
            raise SystemExit(f"missing font: {path}")
        records = name_records(path)
        out.write("\n\n" + label + "\n" + "-" * len(label) + "\n\n")
        out.write(f"  {purpose}\n\n")
        for name_id, heading in WANTED:
            if name_id not in records:
                continue
            out.write(f"{heading}:\n\n")
            body = records[name_id].replace("\r\n", "\n").replace("\r", "\n")
            for line in body.split("\n"):
                out.write(("  " + line).rstrip() + "\n")
            out.write("\n")

    text = out.getvalue()
    with io.open(OUT_PATH, "w", encoding="utf-8", newline="\r\n") as handle:
        handle.write(text)
    print(f"wrote {OUT_PATH} ({len(text)} chars, {len(FONTS)} fonts)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
