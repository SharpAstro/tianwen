"""Group V: the three iTelescope sets of D:/Astro-Pics/Precalibrated, into D:/Astro-Organized as HARD LINKS.

Writes only under --root, and only links: nothing in Astro-Pics is created, moved or changed, and a link costs no space.
Dry run by default; --apply links. Every frame's destination is decided from its own header, the count of each
destination is declared below and anything undeclared is REFUSED, an existing destination is refused, and every link is
checked to be the same file as its source afterwards. A manifest records what was done.

SHARED DATA, NOT OUR CAPTURE, AND ALREADY CALIBRATED. iTelescope's pipeline (MaxIm DL) calibrated every frame before it
was handed out: CALSTAT='BDF' on all 191, float32 pixels, PEDESTAL=-100. The bake must never calibrate them again, which
is the CALSTAT read guard (#466, PreCalibration): no dark or flat is chosen for these lights, and their provenance says
"pre-calibrated BDF". No calibration is filed for them, since none exists for these cameras.

  M33  iTelescope 18, Nerpio (SBIG STL-6303, PlaneWave CDK12.5 at 2541 mm), Astrodon E-series L R G B, 81 x 180 s over
       four nights (2021-12-29, 12-30, 2022-01-01, 01-02). The owner's call (2026-10-12): in whole. 71 of the 81 are
       pier-EAST frames ACP turned 180 degrees (FLIPSTAT 'Flip/Mirror') BEFORE they were calibrated with unturned
       masters, so each keeps about 380 hot pixels and a mirrored flat (a smooth tilt of a few percent, no dust
       donuts); rejection plus gradient removal coped in a test stack.
  M42  iTelescope 31, Siding Spring (FLI PL09000, PlaneWave CDK20 at 2262 mm), Astrodon 5 nm Ha and OIII: Ha 21 x 300 s
       (2020-12-04, 12-13, 2021-01-09), Ha 23 x 60 s (2020-12-07), OIII 20 x 300 s (2021-01-09, 01-10, 01-11).
  M45  iTelescope 68, Mayhill (an IMX571-class colour camera the header names only 'ASCOM', Celestron RASA 11 at
       635 mm, no filter: FILTER 'Color', BAYERPAT 'VALID' read as RGGB), 21 + 25 x 120 s on two nights.

ONE FOLDER PER FILTER WHERE NO NIGHT REACHES THE BAKE'S FLOOR. The bake keys a session on its folder, camera, target and
filter, and needs 10 subs (DatasetBuildOptions.MinSubsPerSession). M33 has at most 9 a filter a night, so each filter's
four nights share one folder (group U's shape); M42's Ha 300 s and OIII the same; Ha 60 s keeps a folder of its own
so no session mixes exposures. M45's nights reach 21 and 25 and keep a folder each. A folder is named by its first
night's local EVENING date: Nerpio is UTC+1, Siding Spring UTC+11 in summer, Mayhill UTC-7.

The site cards are TEXT ('38 09 56'), which the one header parse reads as no site at all; they are written as decimal
degrees afterwards by `tianwen dataset tag-card --numeric` (CORRECTIONS.md, group V). ACP writes the longitude east
positive (Nerpio -02 19 35 is 2.3 degrees WEST, as the site is), unlike group U's MaxIm.
"""
import argparse
import csv
import glob
import os
import sys
import warnings
from collections import Counter
from datetime import datetime, timedelta

warnings.filterwarnings("ignore")
from astropy.io import fits  # noqa: E402

SRC = "D:/Astro-Pics/Precalibrated"

# Per source folder: camera folder, target folder, the site's UTC offset in hours (for the evening date), the
# FILTER card -> filter folder, and how the frames are split into folders.
SETS = {
    "M33 Triangulum Galaxy Data": dict(cam="SBIG-STL-6303", target="Triangulum-Galaxy", utc=1,
                                       filters={"Luminance": "Luminance", "Red": "Red", "Green": "Green", "Blue": "Blue"},
                                       split="filter"),
    "M42 Orion Nebula Data": dict(cam="FLI-PL09000", target="Great-Orion-Nebula", utc=11,
                                  filters={"Ha": "Ha", "OIII": "OIII"}, split="filter+exposure"),
    "M45 Pleiades Data": dict(cam="iTelescope-T68", target="Pleiades", utc=-7,
                              filters={"Color": "No-Filter"}, split="night"),
}

# Destination folder (relative to lights/) -> declared frame count. Anything else is refused.
EXPECT = {
    "SBIG-STL-6303/Luminance/Triangulum-Galaxy/2021-12-29": 16,
    "SBIG-STL-6303/Red/Triangulum-Galaxy/2021-12-29": 22,
    "SBIG-STL-6303/Green/Triangulum-Galaxy/2021-12-29": 21,
    "SBIG-STL-6303/Blue/Triangulum-Galaxy/2021-12-29": 22,
    "FLI-PL09000/Ha/Great-Orion-Nebula/2020-12-04": 21,
    "FLI-PL09000/Ha/Great-Orion-Nebula/2020-12-07": 23,
    "FLI-PL09000/OIII/Great-Orion-Nebula/2021-01-09": 20,
    "iTelescope-T68/No-Filter/Pleiades/2022-01-04": 21,
    "iTelescope-T68/No-Filter/Pleiades/2022-01-05": 25,
}


def evening(date_obs, utc_hours):
    """The local evening date a frame belongs to: local time minus 12 h, so everything until noon is the night before."""
    t = datetime.fromisoformat(str(date_obs)[:19]) + timedelta(hours=utc_hours) - timedelta(hours=12)
    return t.date().isoformat()


def plan():
    rows, refused = [], []
    for folder, s in SETS.items():
        files = sorted(glob.glob(f"{SRC}/{folder}/**/*.fit*", recursive=True))
        by_key = {}
        for f in files:
            h = fits.getheader(f)
            if str(h.get("CALSTAT", "")).strip().upper() != "BDF":
                refused.append((f, f"CALSTAT {h.get('CALSTAT')!r}, not BDF"))
                continue
            flt = s["filters"].get(str(h.get("FILTER", "")).strip())
            if flt is None:
                refused.append((f, f"FILTER {h.get('FILTER')!r} undeclared"))
                continue
            night = evening(h["DATE-OBS"], s["utc"])
            exp = float(h.get("EXPTIME", 0))
            key = {"filter": flt, "filter+exposure": (flt, exp), "night": night}[s["split"]]
            by_key.setdefault(key, []).append((f, night, exp, flt))
        for frames in by_key.values():
            first = min(n for _, n, _, _ in frames)
            flt = frames[0][3]
            dest = f"{s['cam']}/{flt}/{s['target']}/{first}"
            for f, night, exp, _ in frames:
                rows.append(dict(src=f.replace("\\", "/"), dest_dir=dest, night=night, exposure=exp, filter=flt))
    return rows, refused


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", default="D:/Astro-Organized")
    ap.add_argument("--apply", action="store_true")
    a = ap.parse_args()
    rows, refused = plan()
    counts = Counter(r["dest_dir"] for r in rows)
    bad = [f"{d}: {n} planned, {EXPECT.get(d)} declared" for d, n in counts.items() if EXPECT.get(d) != n]
    bad += [f"{d}: declared {n}, none planned" for d, n in EXPECT.items() if d not in counts]
    for d in sorted(counts):
        print(f"{counts[d]:3d}  lights/{d}")
    for f, why in refused:
        print(f"REFUSED {f}: {why}")
    taken = [r for r in rows if os.path.exists(f"{a.root}/lights/{r['dest_dir']}/{os.path.basename(r['src'])}")]
    for r in taken:
        print(f"EXISTS lights/{r['dest_dir']}/{os.path.basename(r['src'])}")
    if bad or refused or taken:
        print("\n".join(bad))
        sys.exit("refusing: the plan does not match what was declared")
    print(f"{len(rows)} frames planned")
    if not a.apply:
        print("dry run; --apply to link")
        return
    for r in rows:
        dst_dir = f"{a.root}/lights/{r['dest_dir']}"
        os.makedirs(dst_dir, exist_ok=True)
        dst = f"{dst_dir}/{os.path.basename(r['src'])}"
        os.link(r["src"], dst)
        if not os.path.samefile(r["src"], dst):
            sys.exit(f"link check failed: {dst}")
        r["dest"] = dst
    man = f"{a.root}/_provenance/manifest-groupV-iTelescope-precalibrated.csv"
    with open(man, "w", newline="", encoding="utf-8") as fh:
        w = csv.DictWriter(fh, fieldnames=["src", "dest", "dest_dir", "night", "exposure", "filter"])
        w.writeheader()
        w.writerows(rows)
    print(f"linked {len(rows)}; manifest -> {man}")


if __name__ == "__main__":
    main()
