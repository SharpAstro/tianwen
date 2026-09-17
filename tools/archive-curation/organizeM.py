"""Group M: the 2024-02-03 ZWO ASI294MC + 135 mm Eta Carinae night into D:/Astro-Organized.

READ-ONLY ON THE SOURCES. Writes only under --root. Same shape as organizeD..organizeL: dry-run by
default, collisions and existing destinations refused before anything is copied, every copy verified
by sha256, a manifest recording what was done. Then a SECOND, separate step writes the frame types
(below), and --verify-tags checks it and records it.

  eta Car   861 x 10 s lights, 12:48-15:32 UTC
  DARK 70 x 10 s   BIAS 140 x 32 us   FLAT 304 x 0.034 s   DARKFLAT 300 x 0.034 s

All same night or next morning: nothing is borrowed. Train: ZWO ASI294MC (uncooled, 20.7-22.8 C
through the night), 135 mm lens, gain 121, offset (BLKLEVEL) 8, RGGB, 4144x2822. SharpCap 4.0.

THE SCALE IS SOLVED, NOT CARDED. No FOCALLEN is recorded; a blind tianwen solve places a middle
frame at 10.7794h -59.8858 at 6.903 arcsec/px, which on 4.63 um pixels is 138.3 mm.

TWO SETS ARE MISLABELLED, AND FILING THEM AS THEY ARE WOULD BREAK THE BAKE. SharpCap typed the 70
darks and the 304 flats FRAMETYP='Light' (its type is a dropdown). The reader takes FRAMETYP first,
so they would enter the bake as two extra light sessions. The pixels settled it (#34, 2026-09-17):
the darks sit at the bias level (+4 to +5 ADU at 10 s), were shot straight after the lights at the
lights' own end temperature, and do not solve; the flats are flat-level and flat-shaped at 0.034 s,
shot the next morning, and do not solve. The folder names agree (`eta Car Darks`, `Flats`). The copy
keeps them byte-for-byte; the correction is the separate tagging step, recorded in CORRECTIONS.md.

THE FILTER IS A UV/IR CUT BY THE OWNER'S RECOLLECTION ("I think; it is a while ago"), which the
pixels do not contradict and cannot confirm. Measured on the same IMX294 sensor against the six
QHY294C + IDAS-LPS-D3 nights: star R/G 0.599 and B/G 0.669 here against 0.426-0.553 and 0.511-0.586
there, so not the D3 (whose own nights spread about 10 percent), and broadband. A UV/IR cut and no
filter cannot be told apart this way on a camera whose window already cuts IR. The flat ratios
(R/G 0.573, B/G 0.809) are NOT a filter comparison: these flats are daylight, group J's a panel.
The slug resolves to NO MATCH in FilterCurveDatabase (no standalone cut curve exists) and every
spelling is pinned there by SpccReachabilityProbe.

CALIBRATION MEASURED, not dated:
  darks   median(dark - bias) +4 R, +5 G, +5 B ADU; light - dark below zero on at most 0.0001 percent
          of pixels over 12 frames across the night, p0.01 +149 to +187 ADU
  flats   dust-free (flat / smoothed p0.1 0.987, pixels under 0.97 0.0001 percent), so they correct
          vignetting only (5.5 percent at the corner ring); owner confirms the train was untouched
          overnight. Comparing the flat's falloff with the lights' sky is confounded here: Eta
          Carinae and the Milky Way fill the 15-degree field and lift its centre.

SHARPCAP NAMES ARE UNIQUE ONLY WITHIN A RUN, so lights are prefixed with their run (12_48_52Z_),
as the ASI1600MM Ha session was. One run here, which is exactly when the collision would stay hidden.
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

SCR = os.path.dirname(os.path.abspath(__file__))
# The verbs tag-frame-type and relabel-frame-type landed in tianwen commit "feat(dataset): a frame type
# is stated in both cards at once, and a wrong one is relabelled by name"; a build older than that has
# neither.
TIANWEN = "C:/Users/SebastianGodelet/source/repos/sharpastro/tianwen/src/TianWen.Cli/bin/Release/net10.0/tianwen.exe"
CAM = "ZWO-ASI294MC"
FILT = "UV-IR-Cut"
NIGHT = "2024-02-03"
BASE = "D:/Astro-Pics/2024/2024-02-03"
TARGETS = {"eta Car": "eta-Car-Nebula"}

# (source dir, run prefix, stated FRAMETYP expected)
LIGHTS = [(f"{BASE}/eta Car/2024-02-03/Light/12_48_52Z/rawframes", "12_48_52Z", "Light")]

# (source dir, {exposure: kind}, stated FRAMETYP expected on every frame). The stated type is
# DECLARED, mislabels included, so a frame stating anything else refuses the run.
CAL = [
    (f"{BASE}/eta Car Darks/2024-02-03/Light/15_34_37Z", {10.0: "DARK"}, "Light"),
    (f"{BASE}/eta Car Bias/2024-02-03/Bias/15_48_22Z", {3.2e-05: "BIAS"}, "Bias"),
    (f"{BASE}/Flats", {0.034018: "FLAT"}, "Light"),
    (f"{BASE}/DarkFlats/2024-02-03/DarkFlat/22_55_28Z", {0.034018: "DARKFLAT"}, "DarkFlat"),
]

# What each kind IS, for the tagging step, and whether its stated type has to be corrected.
TYPE_OF = {"LIGHT": "Light", "DARK": "Dark", "BIAS": "Bias", "FLAT": "Flat", "DARKFLAT": "DarkFlat"}

WANT_INSTRUME = "ZWO ASI294MC"
WANT_GAIN = 121
WANT_OFFSET = 8
WANT_DIMS = (4144, 2822)
NIGHT_DATES = {"2024-02-03"}   # DATE-OBS is UTC; the whole night and the morning flats fall on it


def header(path):
    with fits.open(path, memmap=False) as hdul:
        h = hdul[0].header
        return {
            "inst": str(h.get("INSTRUME", "")).strip(),
            "gain": h.get("GAIN"),
            "offset": h.get("BLKLEVEL", h.get("OFFSET")),
            "temp": h.get("CCD-TEMP"),
            "exp": float(h.get("EXPTIME", 0) or 0),
            "obj": str(h.get("OBJECT", "")).strip(),
            "date": str(h.get("DATE-OBS", ""))[:10],
            "frametyp": str(h.get("FRAMETYP", "")).strip(),
            "imagetyp": str(h.get("IMAGETYP", "")).strip(),
            "dims": (h.get("NAXIS1"), h.get("NAXIS2")),
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


def check_common(h, path, stated):
    if h["inst"] != WANT_INSTRUME:
        return refuse(f"camera {h['inst']!r}, want {WANT_INSTRUME!r}: {path}")
    if h["gain"] != WANT_GAIN or h["offset"] != WANT_OFFSET:
        return refuse(f"gain/offset {h['gain']}/{h['offset']}, want {WANT_GAIN}/{WANT_OFFSET}: {path}")
    if h["dims"] != WANT_DIMS or h["bayer"] != "RGGB":
        return refuse(f"geometry {h['dims']} {h['bayer']}: {path}")
    if h["date"] not in NIGHT_DATES:
        return refuse(f"dated {h['date']}, not this night: {path}")
    if h["frametyp"] != stated or h["imagetyp"]:
        return refuse(f"stated type FRAMETYP={h['frametyp']!r} IMAGETYP={h['imagetyp']!r}, declared {stated!r}: {path}")
    return True


def plan(root):
    rows, seen, dupes = [], {}, 0

    for src, run, stated in LIGHTS:
        files = scan(src)
        if not files:
            return refuse(f"no lights in {src}"), None, None
        for f in files:
            h = header(f["path"])
            if not check_common(h, f["path"], stated):
                return None, None, None
            target = TARGETS.get(h["obj"])
            if not target:
                return refuse(f"unknown OBJECT {h['obj']!r}: {f['path']}"), None, None
            dst = f"{root}/lights/{CAM}/{FILT}/{target}/{NIGHT}/{run}_{f['name']}"
            if f["ino"] in seen:
                dupes += 1
                rows.append(dict(action="dedup-skip", kind="LIGHT", target=target, obj=h["obj"],
                                 src=f["path"], dst=seen[f["ino"]], ino=f["ino"], size=f["size"]))
                continue
            seen[f["ino"]] = dst
            rows.append(dict(action="copy", kind="LIGHT", target=target, obj=h["obj"],
                             src=f["path"], dst=dst, ino=f["ino"], size=f["size"]))

    for src, expect, stated in CAL:
        files = scan(src)
        if not files:
            return refuse(f"calibration set missing: {src}"), None, None
        heads = [(f, header(f["path"])) for f in files]
        temps = [h["temp"] for _, h in heads if h["temp"] is not None]
        # Uncooled: ONE tag per set from its median temperature, or a set drifting through a whole
        # degree would be split across two directories and build two weaker masters.
        set_temp = statistics.median(temps) if temps else 0.0
        for f, h in heads:
            if not check_common(h, f["path"], stated):
                return None, None, None
            match = [k for e, k in expect.items() if abs(e - h["exp"]) <= max(1e-5, 0.01 * e)]
            if not match:
                return refuse(f"unexpected {h['exp']:g}s in {src}; declared {sorted(expect)}"), None, None
            kind = match[0]
            if kind in ("FLAT", "DARKFLAT"):
                d = f"{root}/flats/{CAM}/{FILT}/{NIGHT}/{kind}"
            else:
                e = f"-e{h['exp']:g}s" if kind == "DARK" else ""
                d = f"{root}/calibration/{CAM}/{kind}/{NIGHT}-g{h['gain']}-o{h['offset']}-t{set_temp:+.0f}{e}"
            dst = f"{d}/{f['name']}"
            if f["ino"] in seen:
                dupes += 1
                rows.append(dict(action="dedup-skip", kind=kind, target="", obj=h["obj"],
                                 src=f["path"], dst=seen[f["ino"]], ino=f["ino"], size=f["size"]))
                continue
            seen[f["ino"]] = dst
            rows.append(dict(action="copy", kind=kind, target="", obj=h["obj"],
                             src=f["path"], dst=dst, ino=f["ino"], size=f["size"]))

    bydst = defaultdict(list)
    for r in rows:
        if r["action"] == "copy":
            bydst[r["dst"]].append(r["src"])
    return rows, dupes, {d: s for d, s in bydst.items() if len(s) > 1}


def sha256(path, buf=1 << 20):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        while chunk := f.read(buf):
            h.update(chunk)
    return h.hexdigest()


def payload_sha256(path):
    """sha256 of every byte AFTER the primary header: what a header edit must leave untouched."""
    with open(path, "rb") as f:
        data = f.read()
    for b in range(32):
        for off in range(b * 2880, (b + 1) * 2880, 80):
            card = data[off:off + 80]
            if card[:3] == b"END" and card[3:].strip() == b"":
                return hashlib.sha256(data[(b + 1) * 2880:]).hexdigest()
    raise ValueError(f"no END card in {path}")


def tagging_commands(copies):
    dirs = defaultdict(set)
    for r in copies:
        dirs[r["kind"]].add(os.path.dirname(r["dst"]))
    stated = {"LIGHT": "Light", "DARK": "Light", "BIAS": "Bias", "FLAT": "Light", "DARKFLAT": "DarkFlat"}
    out = []
    for kind in ("LIGHT", "DARK", "BIAS", "FLAT", "DARKFLAT"):
        for d in sorted(dirs[kind]):
            if stated[kind] == TYPE_OF[kind]:
                out.append(f'& "{TIANWEN}" dataset tag-frame-type --path "{d}" --as {TYPE_OF[kind]} --apply')
            else:
                out.append(f'& "{TIANWEN}" dataset relabel-frame-type --path "{d}" --frame-type {stated[kind]} '
                           f'--as {TYPE_OF[kind]} --apply')
    return out


def verify_tags(manifest):
    """After the tagging step: every copy states its kind in BOTH cards, its payload still equals the
    source's, and each changed card gets a row in label-corrections.csv."""
    rows = [r for r in csv.DictReader(open(manifest, encoding="utf-8")) if r["action"] == "copy"]
    corrections = os.path.join(SCR, "label-corrections.csv")
    already = set()
    if os.path.exists(corrections):
        for r in csv.DictReader(open(corrections, encoding="utf-8")):
            already.add((r["path"], r["card"]))
    was = {"LIGHT": ("", "Light"), "DARK": ("", "Light"), "BIAS": ("", "Bias"), "FLAT": ("", "Light"),
           "DARKFLAT": ("", "DarkFlat")}
    evidence = {
        "LIGHT": "fill: SharpCap 4 wrote FRAMETYP only; solved 10.7794h -59.8858 6.903 arcsec/px",
        "DARK": "relabel: at the bias level (+4/+5 ADU at 10 s), unsolved, shot after the lights at 20.7 C, folder 'eta Car Darks' (#34)",
        "BIAS": "fill: SharpCap 4 wrote FRAMETYP only; 32 us, the camera's minimum exposure",
        "FLAT": "relabel: flat-level and flat-shaped at 0.034 s, unsolved, next morning, folder 'Flats' (#34)",
        "DARKFLAT": "fill: SharpCap 4 wrote FRAMETYP only; 0.034 s at the bias level beside the flats",
    }
    bad, new_rows = 0, []
    for i, r in enumerate(rows):
        h = header(r["dst"])
        want = TYPE_OF[r["kind"]]
        if h["frametyp"] != want or h["imagetyp"] != want:
            print(f"  NOT TAGGED {r['dst']}: FRAMETYP={h['frametyp']!r} IMAGETYP={h['imagetyp']!r}, want {want!r}")
            bad += 1
            continue
        if payload_sha256(r["dst"]) != payload_sha256(r["src"]):
            print(f"  PAYLOAD DIFFERS {r['dst']}")
            bad += 1
            continue
        old_frametyp = was[r["kind"]][1]
        for card, old in (("IMAGETYP", ""), ("FRAMETYP", old_frametyp)):
            if old != want and (r["dst"], card) not in already:
                new_rows.append(dict(path=r["dst"], card=card, was=old, now=want, evidence=evidence[r["kind"]]))
        if (i + 1) % 200 == 0:
            print(f"    verified {i + 1}/{len(rows)}", flush=True)
    if bad:
        print(f"\n  {bad} file(s) failed; label-corrections.csv NOT written")
        return 1
    with open(corrections, "a", newline="", encoding="utf-8") as fh:
        w = csv.DictWriter(fh, fieldnames=["path", "card", "was", "now", "evidence"])
        if os.path.getsize(corrections) == 0:
            w.writeheader()
        w.writerows(new_rows)
    print(f"\n  all {len(rows)} copies state their type in both cards with the source's payload; "
          f"{len(new_rows)} rows appended to {corrections}")
    return 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", default="D:/Astro-Organized")
    ap.add_argument("--apply", action="store_true")
    ap.add_argument("--verify-tags", action="store_true",
                    help="after the tagging commands: check both cards and the payload, record corrections")
    args = ap.parse_args()
    manifest = os.path.join(SCR, "groupM-manifest.csv")

    if args.verify_tags:
        return verify_tags(manifest)

    rows, dupes, collisions = plan(args.root)
    if rows is None:
        print("\n  REFUSED. nothing written.")
        return 1

    copies = [r for r in rows if r["action"] == "copy"]
    by = defaultdict(lambda: [0, 0])
    for r in copies:
        by[(r["kind"], r["target"])][0] += 1
        by[(r["kind"], r["target"])][1] += r["size"]
    print(f"\n  root               : {args.root}")
    print(f"  filter slug        : {FILT}  (owner's recollection; measured broadband, not the IDAS LPS-D3)")
    print(f"  files to copy      : {len(copies)}")
    print(f"  bytes              : {sum(r['size'] for r in copies) / 2**30:.2f} GiB")
    print(f"  dedup-skips        : {dupes}")
    print(f"  collisions         : {len(collisions)}")
    for (kind, target), (n, b) in sorted(by.items()):
        print(f"    {kind:9s} {target:20s} {n:5d} files  {b / 2**30:7.2f} GiB")
    for d in sorted({os.path.dirname(r["dst"]) for r in copies}):
        print(f"    {d}")

    if collisions:
        for d, s in list(collisions.items())[:5]:
            print(f"  COLLISION {d} <- {s}", file=sys.stderr)
        print("\n  REFUSED on collisions. nothing written.")
        return 1

    existing = [r for r in copies if os.path.exists(r["dst"])]
    if existing:
        print(f"\n  REFUSED: {len(existing)} destination(s) already exist, e.g. {existing[0]['dst']}")
        return 1

    print("\n  then the frame types, run AFTER the copy (dry-run first by dropping --apply):")
    for c in tagging_commands(copies):
        print(f"    {c}")
    print("  then: python organizeM.py --verify-tags")

    if not args.apply:
        print("\n  nothing written. re-run with --apply to perform the copy.")
        return 0

    done, failed = 0, 0
    with open(manifest, "w", newline="", encoding="utf-8") as fh:
        w = csv.DictWriter(fh, fieldnames=["action", "kind", "target", "obj", "src", "dst",
                                           "ino", "size", "sha256"])
        w.writeheader()
        for r in rows:
            if r["action"] != "copy":
                w.writerow({**r, "sha256": ""})
                continue
            os.makedirs(os.path.dirname(r["dst"]), exist_ok=True)
            shutil.copy2(r["src"], r["dst"])
            a, b = sha256(r["src"]), sha256(r["dst"])
            if a != b:
                print(f"  HASH MISMATCH {r['src']}", file=sys.stderr)
                failed += 1
            else:
                done += 1
            w.writerow({**r, "sha256": b})
            if done % 50 == 0:
                print(f"    {done}/{len(copies)}", flush=True)
    print(f"\n  copied {done}, failed {failed}, manifest {manifest}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
