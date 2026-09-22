"""Group P: the 2026-09-22 Uranus-C dark and bias library into D:/Astro-Organized.

READ-ONLY ON THE SOURCES. Writes only under --root. Same shape as organizeM: dry-run by default,
collisions and existing destinations refused before anything is copied, every copy verified by
sha256, a manifest recording what was done.

These are OUR OWN captures (tianwen darks), not a swept-in session, so two of the usual problems do
not arise: FRAMETYP and IMAGETYP both already state the type, and the frames were never mislabelled.
What IS new here is binning.

THE FOLDER CONVENTION COULD NOT EXPRESS BINNING, AND TWO OF THESE SETS NEED IT.
`<date>-g<gain>-o<offset>-t<temp>[-e<exp>s]` names gain, offset, temperature and exposure, so a bin-2
30 s dark and a bin-1 30 s dark at the same gain, offset and temperature land on ONE directory and
the second refuses as a collision. This session has exactly that pair. The extension is a trailing
`-bin<n>`, present only when n > 1, which leaves every existing folder name unchanged and matches the
rule the FITS filenames already use (bin 1 omitted, bin 2 named).

It is a HUMAN and COLLISION fix, not a correctness one. MasterGroupKey carries Width and Height but
no binning field, and binning changes the geometry (3856x2180 against 1928x1090), so the resolver
already tells these apart on dimensions and could never hand a bin-2 dark to bin-1 lights.

TWO SETS ARE DELIBERATELY NOT FILED, which is a decision with a reason rather than an omission:

  41 x 30 s bin 1   the aborted run, before the DAL ROI bug was found. darks-to-shoot.csv's 30 s row
                    at gain 220 offset 8 is BIN 2 (the Lagoon-and-Trifid 2023-08-03 lights are
                    1928x1090), so no filed light is served by a bin-1 30 s dark. Calibration is
                    filed for a filed session, never for its own sake.
   3 x 1 s  bin 1   verification frames shot while proving the ROI fix. Not a library row.

TEMPERATURE IS THE SET'S MEDIAN, rounded once with the same `:+.0f` organizeM uses, never each
frame's own: this body is uncooled and every set drifts (the 200 s run fell 17.5 to 16.3 C across
2.8 hours as the night cooled), so a per-frame round would split each set into a half-library.

WHAT THESE SERVE, from darks-to-shoot.csv:
  g220 o8 bin2 30 s   Lagoon-and-Trifid 2023-08-03, 237 lights (the row's target was 18.66 C)
  g220 o8 bin1 200 s  Lagoon-and-Trifid 2023-08-09, 20 lights (17.07 C)
  g200 o10 bin1 bias  eta-Car-Nebula 2023-02-24, 168 lights, whose 27.7 C dark stays a summer job
Flats are NOT shot or needed: flats/Uranus-C-IMX585/.../2023-07-29 already holds FLAT and DARKFLAT,
five and eleven days from those lights, inside CalibrationResolver.UnprovenFlatMaxDays.
"""
import argparse
import csv
import hashlib
import os
import shutil
import statistics
import sys
import warnings
from collections import defaultdict

warnings.filterwarnings("ignore")
from astropy.io import fits  # noqa: E402

SRC_DIRS = [
    ("BIAS", "C:/Users/SebastianGodelet/Pictures/TianWen/Bias/2026-09-22"),
    ("DARK", "C:/Users/SebastianGodelet/Pictures/TianWen/Darks/2026-09-22"),
]

CAM = "Uranus-C-IMX585"
WANT_INSTRUME = "Uranus-C"
NIGHT = "2026-09-22"
DIMS = {1: (3856, 2180), 2: (1928, 1090)}

# (kind, exposure s, gain, offset, bin) -> expected frame count. A set that is not listed is
# REFUSED rather than guessed at, and a listed set whose count differs is refused too: both mean
# the source holds something this plan did not anticipate.
EXPECT = {
    ("BIAS", 1e-05, 200, 10, 1): 50,
    ("BIAS", 1e-05, 220, 8, 1): 50,
    ("BIAS", 1e-05, 220, 8, 2): 53,
    ("DARK", 200.0, 220, 8, 1): 50,
    ("DARK", 30.0, 220, 8, 2): 50,
}

# Deliberately not filed; see the module docstring.
SKIP = {
    ("DARK", 30.0, 220, 8, 1): "aborted bin-1 run; the 30 s library row is bin 2, so no filed light is served",
    ("DARK", 1.0, 220, 8, 1): "ROI verification frames, not a library row",
}


def header(path):
    with fits.open(path, memmap=False) as hd:
        h = hd[0].header
        return {
            "inst": str(h.get("INSTRUME", "")).strip(),
            "gain": h.get("GAIN"),
            "offset": h.get("BLKLEVEL", h.get("OFFSET")),
            "temp": h.get("CCD-TEMP"),
            "exp": float(h.get("EXPTIME", 0) or 0),
            "date": str(h.get("DATE-OBS", ""))[:10],
            "frametyp": str(h.get("FRAMETYP", "")).strip(),
            "imagetyp": str(h.get("IMAGETYP", "")).strip(),
            "dims": (h.get("NAXIS1"), h.get("NAXIS2")),
            "bin": int(h.get("XBINNING", 1) or 1),
            "ybin": int(h.get("YBINNING", 1) or 1),
            "bayer": str(h.get("BAYERPAT", "")).strip(),
        }


def scan(d):
    out = []
    for n in sorted(os.listdir(d)):
        if n.lower().endswith((".fits", ".fit", ".fts")):
            p = f"{d}/{n}"
            st = os.stat(p)
            out.append(dict(path=p, name=n, ino=st.st_ino, size=st.st_size))
    return out


def refuse(msg):
    print(f"  REFUSING: {msg}", file=sys.stderr)
    return None


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def check_common(h, path, kind):
    if h["inst"] != WANT_INSTRUME:
        return refuse(f"camera {h['inst']!r}, want {WANT_INSTRUME!r}: {path}")
    if h["date"] != NIGHT:
        return refuse(f"dated {h['date']}, not {NIGHT}: {path}")
    if h["bayer"] != "RGGB":
        return refuse(f"bayer {h['bayer']!r}: {path}")
    if h["bin"] != h["ybin"]:
        return refuse(f"non-square binning {h['bin']}x{h['ybin']}: {path}")
    if h["dims"] != DIMS.get(h["bin"]):
        return refuse(f"geometry {h['dims']} does not match bin {h['bin']} ({DIMS.get(h['bin'])}): {path}")
    # Our own writer states both cards; a frame that does not is not from this run.
    if h["frametyp"].upper() != kind or h["imagetyp"].upper() != kind:
        return refuse(f"type FRAMETYP={h['frametyp']!r} IMAGETYP={h['imagetyp']!r}, want {kind}: {path}")
    return True


def plan(root):
    sets = defaultdict(list)
    for kind, d in SRC_DIRS:
        if not os.path.isdir(d):
            return refuse(f"source missing: {d}"), None
        for f in scan(d):
            h = header(f["path"])
            key = (kind, h["exp"], h["gain"], h["offset"], h["bin"])
            sets[key].append((f, h))

    unknown = [k for k in sets if k not in EXPECT and k not in SKIP]
    if unknown:
        return refuse(f"source holds sets this plan did not declare: {sorted(unknown)}"), None

    rows, seen = [], {}
    for key in sorted(sets, key=lambda k: (k[0], k[1], k[2], k[3], k[4])):
        kind, exp, gain, off, binning = key
        members = sets[key]
        if key in SKIP:
            for f, _ in members:
                rows.append(dict(action="skip", kind=kind, reason=SKIP[key], set="",
                                 src=f["path"], dst="", ino=f["ino"], size=f["size"]))
            continue
        if len(members) != EXPECT[key]:
            return refuse(f"{key} has {len(members)} frames, expected {EXPECT[key]}"), None

        temps = [h["temp"] for _, h in members if h["temp"] is not None]
        if not temps:
            return refuse(f"{key} carries no CCD-TEMP"), None
        set_temp = statistics.median(temps)

        e = f"-e{exp:g}s" if kind == "DARK" else ""
        b = f"-bin{binning}" if binning > 1 else ""
        setname = f"{NIGHT}-g{gain}-o{off}-t{set_temp:+.0f}{e}{b}"
        d = f"{root}/calibration/{CAM}/{kind}/{setname}"

        for f, h in members:
            if not check_common(h, f["path"], kind):
                return None, None
            dst = f"{d}/{f['name']}"
            if f["ino"] in seen:
                rows.append(dict(action="dedup-skip", kind=kind, reason="same inode", set=setname,
                                 src=f["path"], dst=seen[f["ino"]], ino=f["ino"], size=f["size"]))
                continue
            seen[f["ino"]] = dst
            rows.append(dict(action="copy", kind=kind, reason="", set=setname,
                             src=f["path"], dst=dst, ino=f["ino"], size=f["size"]))

    # Refuse collisions and pre-existing destinations before anything is written.
    bydst = defaultdict(list)
    for r in rows:
        if r["action"] == "copy":
            bydst[r["dst"]].append(r["src"])
    dupes = {d: s for d, s in bydst.items() if len(s) > 1}
    if dupes:
        for d, s in list(dupes.items())[:5]:
            print(f"  collision {d} <- {s}", file=sys.stderr)
        return refuse(f"{len(dupes)} destination collision(s)"), None
    existing = [d for d in bydst if os.path.exists(d)]
    if existing:
        for d in existing[:5]:
            print(f"  exists {d}", file=sys.stderr)
        return refuse(f"{len(existing)} destination(s) already exist"), None
    # The caller wants the SET directories, not every file path in them.
    return rows, sorted({os.path.dirname(d) for d in bydst})


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", default="D:/Astro-Organized")
    ap.add_argument("--apply", action="store_true")
    ap.add_argument("--manifest", default=None)
    a = ap.parse_args()

    root = a.root.replace("\\", "/").rstrip("/")
    if not root.lower().startswith("d:/astro-organized"):
        sys.exit("refusing: --root must be under D:/Astro-Organized")

    rows, dests = plan(root)
    if rows is None:
        sys.exit(1)

    copies = [r for r in rows if r["action"] == "copy"]
    skips = [r for r in rows if r["action"] == "skip"]
    print(f"{'APPLY' if a.apply else 'DRY RUN'}: {len(copies)} copy, {len(skips)} skipped, "
          f"{sum(r['size'] for r in copies) / 2**30:.2f} GiB")
    print()
    for d in dests:
        n = sum(1 for r in copies if r["dst"].startswith(d + "/"))
        print(f"  {n:>3}  {d[len(root) + 1:]}")
    if skips:
        print()
        for reason in sorted({r["reason"] for r in skips}):
            n = sum(1 for r in skips if r["reason"] == reason)
            print(f"  {n:>3}  NOT FILED: {reason}")

    if not a.apply:
        print("\n(dry run; pass --apply to copy)")
        return

    for d in dests:
        os.makedirs(d, exist_ok=True)
    done = 0
    for r in copies:
        shutil.copy2(r["src"], r["dst"])
        src_hash, dst_hash = sha256(r["src"]), sha256(r["dst"])
        if src_hash != dst_hash:
            sys.exit(f"HASH MISMATCH, stopping: {r['src']} -> {r['dst']}")
        r["sha256"] = src_hash
        done += 1
        if done % 25 == 0:
            print(f"  {done}/{len(copies)}")
    print(f"  {done}/{len(copies)} copied and verified")

    man = a.manifest or f"{root}/_provenance/manifest-groupP-{NIGHT}.csv"
    os.makedirs(os.path.dirname(man), exist_ok=True)
    with open(man, "w", newline="", encoding="utf-8") as fh:
        w = csv.DictWriter(fh, fieldnames=["action", "kind", "reason", "set", "src", "dst", "ino", "size", "sha256"])
        w.writeheader()
        for r in rows:
            w.writerow({**{k: "" for k in w.fieldnames}, **r})
    print(f"manifest -> {man}")


if __name__ == "__main__":
    main()
