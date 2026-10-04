"""Group U: LDN 1622 through a Takahashi FSQ-106 on a QSI 683ws (KAF-8300 CCD), R, G, B and H-alpha, into D:/Astro-Organized.
(Filed as "group Q" on 2026-10-04 and renamed the same day: Q was already the 2023-09-15 LMC set's letter.)

Writes only under --root. Same shape as organizeP: dry-run by default, collisions and existing destinations refused before
anything is written, every frame verified by sha256, a manifest recording what was done. With --move the filed frames are
MOVED rather than copied (a same-volume rename): the owner keeps another copy of this set and asked for it to be fixed in
place (2026-10-04), so the 100 frames not filed stay in the source folder and nothing in it is otherwise touched. After
filing, the frames are compressed with NTFS WOF LZX (compact /c /exe:lzx, 1.8 to 1 measured on a light and a bias), which
is transparent to every reader and is not a reparse point to FileEnumeration (checked: the attribute stays Archive).

A shared data set, not our capture: a remote pier on Rowe Mesa, Rowe, New Mexico (the owner, 2026-10-04; the frames'
SITELAT 35 19 30, SITELONG 105 43 01 agree), a dark site. MaxIm wrote both as TEXT, which the one header parse reads
as no site at all, and the longitude WEST-positive: the frames' own CENTALT 43.2 / CENTAZ 232.3 put LDN 1622 at 45.5 / 228
from -105.72 and 20 degrees below the horizon from +105.72. The 70 lights were given numeric SITELAT 35.325 and SITELONG
-105.716944 (east positive) by `tianwen dataset tag-card` after filing, and re-compressed (CORRECTIONS.md, group U).
MaxIm DL 5.24, December 2015 to January 2016, the photographer's notes in FSQ_LDN_1622.txt beside it (22.8 h). The first
CCD in the archive and the first mono RGB set; every frame states IMAGETYP and FILTER, so nothing is inferred.

ONE SESSION PER FILTER, ALL NIGHTS IN ONE FOLDER. The bake keys a session on its folder, camera, target and filter
(SessionDiscovery), and no single night reaches its 10-sub floor (at most 7 a filter a night), so a folder per night would
leave every session under it and the whole set out of the bake. The lights' folder is named by the filter's first night
(its local evening date, UTC minus 12 h, the site being UTC-7). The file names carry the pier side (_E_ / _W_), which no
header card does; the bake reads orientation off the stars, so a real flip would still show as one.

WHAT IS FILED, and what is not (calibration is filed for a filed session, never for its own sake):
  lights   H 21 x 1800 s, R 17 x 900 s, G 16 x 900 s, B 16 x 900 s
  bias     50
  darks    900 s (20) for R, G, B; 1800 s (8) for H
  flats    R, G, B, H (20 each), each under its own first date; sky flats of varying exposure, no dark-flats
           (bias only: a CCD at -20 C carries no dark current worth taking out of a 1 to 45 s flat)
  NOT      darks 300 s and 600 s (no light at either exposure); flats L, O, S (no lights through them)

NO GAIN OR OFFSET CARD on the calibration frames (the lights say GAIN 1.1), so their folders say gna-ona.
CalibrationResolver scores an unknown gain with a penalty, never a refusal, and these are the only sets of the camera.
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
from datetime import datetime, timedelta

warnings.filterwarnings("ignore")
from astropy.io import fits  # noqa: E402

SRC = "D:/Astro-Pics/2026/2026-10-04/FSQ-DS"
CAM = "QSI-683ws"
WANT_INSTRUME = "QSI 683ws S/N 00602201 HW 00.00.00 FW 06.03.01 PI 6.4.962.4"
TARGET = "LDN-1622"
WANT_OBJECT = "LDN 1622"
DIMS = (3326, 2504)
FILTER_SLUG = {"R": "Red", "G": "Green", "B": "Blue", "H": "Ha"}

# (kind, filter or exposure) -> expected frame count; anything not declared is REFUSED.
EXPECT = {
    ("LIGHT", "H"): 21, ("LIGHT", "R"): 17, ("LIGHT", "G"): 16, ("LIGHT", "B"): 16,
    ("BIAS", 0.0): 50,
    ("DARK", 900.0): 20, ("DARK", 1800.0): 8,
    ("FLAT", "R"): 20, ("FLAT", "G"): 20, ("FLAT", "B"): 20, ("FLAT", "H"): 20,
}
SKIP = {
    ("DARK", 300.0): "300 s darks: no light at that exposure",
    ("DARK", 600.0): "600 s darks: no light at that exposure",
    ("FLAT", "L"): "L flats: no lights through L",
    ("FLAT", "O"): "O flats: no lights through O",
    ("FLAT", "S"): "S flats: no lights through S",
}
EXPECT_LIGHT_EXP = {"H": 1800.0, "R": 900.0, "G": 900.0, "B": 900.0}


def header(path):
    with fits.open(path, memmap=False) as hd:
        h = hd[0].header
        return {
            "inst": str(h.get("INSTRUME", "")).strip(),
            "type": str(h.get("FRAMETYP", h.get("IMAGETYP", ""))).strip().upper(),
            "filter": str(h.get("FILTER", "")).strip(),
            "exp": round(float(h.get("EXPTIME", 0) or 0), 3),
            "temp": h.get("CCD-TEMP"),
            "dateobs": str(h.get("DATE-OBS", "")),
            "object": str(h.get("OBJECT", "")).strip(),
            "dims": (h.get("NAXIS1"), h.get("NAXIS2")),
            "bin": int(h.get("XBINNING", 1) or 1),
            "bayer": str(h.get("BAYERPAT", "")).strip(),
        }


def night_of(dateobs):
    # The observing night is the local evening's date: UTC minus 12 h puts every frame of a night on its evening.
    return (datetime.fromisoformat(dateobs) - timedelta(hours=12)).date().isoformat()


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def refuse(msg):
    print(f"  REFUSING: {msg}", file=sys.stderr)
    return None


def plan(root):
    frames = []
    for dirpath, _, names in os.walk(SRC):
        for n in sorted(names):
            if n.lower().endswith((".fit", ".fits", ".fts")):
                p = f"{dirpath}/{n}".replace("\\", "/")
                st = os.stat(p)
                frames.append(dict(path=p, name=n, ino=st.st_ino, size=st.st_size, h=header(p)))

    sets = defaultdict(list)
    for f in frames:
        h = f["h"]
        if h["inst"] != WANT_INSTRUME:
            return refuse(f"camera {h['inst']!r}, want {WANT_INSTRUME!r}: {f['path']}"), None
        if h["dims"] != DIMS or h["bin"] != 1 or h["bayer"]:
            return refuse(f"geometry {h['dims']} bin {h['bin']} bayer {h['bayer']!r}: {f['path']}"), None
        if h["type"] in ("LIGHT", "FLAT"):
            key = (h["type"], h["filter"])
        elif h["type"] in ("DARK", "BIAS"):
            key = (h["type"], h["exp"])
        else:
            return refuse(f"type {h['type']!r}: {f['path']}"), None
        sets[key].append(f)

    unknown = [k for k in sets if k not in EXPECT and k not in SKIP]
    if unknown:
        return refuse(f"source holds sets this plan did not declare: {sorted(map(str, unknown))}"), None
    missing = [k for k in EXPECT if k not in sets]
    if missing:
        return refuse(f"declared sets not found: {missing}"), None

    rows, seen = [], {}
    for key in sorted(sets, key=str):
        kind = key[0]
        members = sorted(sets[key], key=lambda f: f["h"]["dateobs"])
        if key in SKIP:
            for f in members:
                rows.append(dict(action="skip", kind=kind, reason=SKIP[key], set="", src=f["path"], dst="", ino=f["ino"], size=f["size"]))
            continue
        if len(members) != EXPECT[key]:
            return refuse(f"{key} has {len(members)} frames, expected {EXPECT[key]}"), None
        temps = [f["h"]["temp"] for f in members if f["h"]["temp"] is not None]
        if not temps:
            return refuse(f"{key} carries no CCD-TEMP"), None
        t = f"{statistics.median(temps):+.0f}"
        # A set is named by the date it STARTED (the first frame's night), never split by each frame's own date.
        start = night_of(members[0]["h"]["dateobs"])
        if kind == "LIGHT":
            filt = key[1]
            for f in members:
                if f["h"]["exp"] != EXPECT_LIGHT_EXP[filt]:
                    return refuse(f"{filt} light at {f['h']['exp']} s, want {EXPECT_LIGHT_EXP[filt]}: {f['path']}"), None
                if f["h"]["object"] != WANT_OBJECT:
                    return refuse(f"OBJECT {f['h']['object']!r}: {f['path']}"), None
            setname = f"{FILTER_SLUG[filt]}/{TARGET}/{start}"
            d = f"{root}/lights/{CAM}/{setname}"
        elif kind == "FLAT":
            setname = f"{FILTER_SLUG[key[1]]}/{start}/FLAT"
            d = f"{root}/flats/{CAM}/{setname}"
        else:
            e = f"-e{key[1]:g}s" if kind == "DARK" else ""
            setname = f"{start}-gna-ona-t{t}{e}"
            d = f"{root}/calibration/{CAM}/{kind}/{setname}"
        for f in members:
            dst = f"{d}/{f['name']}"
            if f["ino"] in seen:
                rows.append(dict(action="dedup-skip", kind=kind, reason="same inode", set=setname, src=f["path"], dst=seen[f["ino"]], ino=f["ino"], size=f["size"]))
                continue
            seen[f["ino"]] = dst
            rows.append(dict(action="copy", kind=kind, reason="", set=setname, src=f["path"], dst=dst, ino=f["ino"], size=f["size"]))

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
    return rows, sorted({os.path.dirname(d) for d in bydst})


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", default="D:/Astro-Organized")
    ap.add_argument("--apply", action="store_true")
    ap.add_argument("--manifest", default=None)
    ap.add_argument("--move", action="store_true",
                    help="move rather than copy (the owner keeps another copy of this set; decided 2026-10-04)")
    a = ap.parse_args()
    if a.move and not os.path.splitdrive(SRC)[0].lower() == os.path.splitdrive(a.root)[0].lower():
        sys.exit("refusing: --move needs the source and the root on one volume")
    root = a.root.replace("\\", "/").rstrip("/")
    if not root.lower().startswith("d:/astro-organized"):
        sys.exit("refusing: --root must be under D:/Astro-Organized")

    rows, dests = plan(root)
    if rows is None:
        sys.exit(1)
    copies = [r for r in rows if r["action"] == "copy"]
    skips = [r for r in rows if r["action"] == "skip"]
    print(f"{'APPLY' if a.apply else 'DRY RUN'}: {len(copies)} copy, {len(skips)} not filed, {sum(r['size'] for r in copies) / 2**30:.2f} GiB")
    print()
    for d in dests:
        n = sum(1 for r in copies if r["dst"].startswith(d + "/"))
        print(f"  {n:>3}  {d[len(root) + 1:]}")
    if skips:
        print()
        for reason in sorted({r["reason"] for r in skips}):
            print(f"  {sum(1 for r in skips if r['reason'] == reason):>3}  NOT FILED: {reason}")
    if not a.apply:
        print("\n(dry run; pass --apply to copy)")
        return

    for d in dests:
        os.makedirs(d, exist_ok=True)
    done = 0
    for r in copies:
        src_hash = sha256(r["src"])
        if a.move:
            # The same volume, so a rename: the frame's bytes never move and its inode stays.
            os.replace(r["src"], r["dst"])
            r["action"] = "move"
        else:
            shutil.copy2(r["src"], r["dst"])
        dst_hash = sha256(r["dst"])
        if src_hash != dst_hash:
            sys.exit(f"HASH MISMATCH, stopping: {r['src']} -> {r['dst']}")
        r["sha256"] = src_hash
        done += 1
        if done % 25 == 0:
            print(f"  {done}/{len(copies)}")
    print(f"  {done}/{len(copies)} {'moved' if a.move else 'copied'} and verified")
    man = a.manifest or f"{root}/_provenance/manifest-groupU-LDN-1622-2015.csv"
    os.makedirs(os.path.dirname(man), exist_ok=True)
    with open(man, "w", newline="", encoding="utf-8") as fh:
        w = csv.DictWriter(fh, fieldnames=["action", "kind", "reason", "set", "src", "dst", "ino", "size", "sha256"])
        w.writeheader()
        for r in rows:
            w.writerow({**{k: "" for k in w.fieldnames}, **r})
    print(f"manifest -> {man}")


if __name__ == "__main__":
    main()
