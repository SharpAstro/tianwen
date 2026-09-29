# Each retained master's pointing and field, read off its FITS header, for E16b's widening rule: no session whose plate
# overlaps an eval field's. A plate solution (CRVAL + CD) is preferred; else OBJCTRA/OBJCTDEC, then RA/DEC; the field from
# CD, else PIXSCALE/SCALE, else pixel size x binning over the focal length.
#   python e16b_master_fields.py <store> > fields.tsv
import math, os, sys

def header(path):
    cards = {}
    with open(path, "rb") as f:
        while True:
            block = f.read(2880)
            if len(block) < 2880:
                break
            for i in range(0, 2880, 80):
                card = block[i:i + 80].decode("ascii", "replace")
                key = card[:8].strip()
                if key == "END":
                    return cards
                if card[8:10] == "= ":
                    value = card[10:].split("/")[0].strip().strip("'").strip()
                    cards.setdefault(key, value)
    return cards

def sexagesimal(text, hours):
    parts = text.replace(":", " ").split()
    if len(parts) < 2:
        return float(text) * (15.0 if hours else 1.0)
    sign = -1.0 if parts[0].startswith("-") else 1.0
    d = abs(float(parts[0])) + float(parts[1]) / 60.0 + (float(parts[2]) / 3600.0 if len(parts) > 2 else 0.0)
    return sign * d * (15.0 if hours else 1.0)

def num(cards, *keys):
    for k in keys:
        if k in cards:
            try:
                return float(cards[k])
            except ValueError:
                pass
    return None

store = sys.argv[1]
root = os.path.join(store, "session-masters")
print("master\tra\tdec\twidth_deg\theight_deg\tsource")
for name in sorted(os.listdir(root)):
    if not name.endswith(".fits") or name.count(".") > 1:
        continue
    c = header(os.path.join(root, name))
    nx, ny = num(c, "NAXIS1"), num(c, "NAXIS2")
    ra = dec = None
    source = ""
    if "CRVAL1" in c and "CRVAL2" in c:
        ra, dec, source = float(c["CRVAL1"]), float(c["CRVAL2"]), "wcs"
    elif "OBJCTRA" in c and "OBJCTDEC" in c:
        ra, dec, source = sexagesimal(c["OBJCTRA"], True), sexagesimal(c["OBJCTDEC"], False), "objct"
    elif "RA" in c and "DEC" in c:
        ra, dec, source = float(c["RA"]), float(c["DEC"]), "radec"
    scale = None
    cd11, cd21 = num(c, "CD1_1"), num(c, "CD2_1")
    if cd11 is not None and cd21 is not None:
        scale = math.hypot(cd11, cd21)
    elif num(c, "PIXSCALE", "SCALE") is not None:
        scale = num(c, "PIXSCALE", "SCALE") / 3600.0
    elif num(c, "XPIXSZ") is not None and num(c, "FOCALLEN"):
        scale = math.degrees(num(c, "XPIXSZ") * 1e-3 / num(c, "FOCALLEN"))
    w = nx * scale if scale and nx else float("nan")
    h = ny * scale if scale and ny else float("nan")
    print(f"{name[:-5]}\t{ra if ra is not None else float('nan'):.4f}\t{dec if dec is not None else float('nan'):.4f}\t{w:.3f}\t{h:.3f}\t{source}")
