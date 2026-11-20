"""Make the plates plate_grain.py wrote a complete starless root an export can read (#2203, R4f, 2026-11-20).

    python -I grain_store.py <store's starless root> <grain root> [--apply]

The grain root holds plates/<stem>_plate.fits written by plate_grain.py and nothing else. A stars export reads three
files per plate from <root>/plates: the plate, its star records (<stem>_plate.stars.csv) and its profile
(<stem>_plate.profile.json); the PSF store comes from the bake's own stats, not from this root. The side files are the
store's own, unchanged by the rule, so each is HARD-LINKED into the grain root (same volume, no bytes copied; nothing
writes through a link: an export only reads). Everything else under plates/ and the root's stats/ folder is linked too,
so the root is whole, but nothing reads it there, and the plate ledgers among it (starless-plates.jsonl and .md)
describe the ORIGINAL plates, not the grain ones beside them.

Without --apply it names every file it would link and stops. A plate of the store with no grain plate is named, none
of ITS side files is linked (a star list with no plate is a session the export skips with a warning and exit 0, which
makes a shorter export with no failure), and the script exits 1: a grain root is used only whole. A plate is never
linked over: it is the one file the rule rewrites.

export-synth.ps1 -PlatesRoot <grain root> then exports from it.
"""
import os, sys

SRC, DST = sys.argv[1], sys.argv[2]
apply = "--apply" in sys.argv
PLATE = "_plate.fits"

# The stems first, so a stem with no grain plate links nothing at all.
store_plates = sorted(n for n in os.listdir(os.path.join(SRC, "plates")) if n.endswith(PLATE))
missing = [n for n in store_plates if not os.path.exists(os.path.join(DST, "plates", n))]
for name in missing:
    print("NO GRAIN PLATE FOR", name)
missing_stems = tuple(n[:-len(".fits")] for n in missing)  # "<stem>_plate", the prefix of every side file

made = kept = skipped = 0
for sub in ("plates", "stats"):
    s_dir, d_dir = os.path.join(SRC, sub), os.path.join(DST, sub)
    for root, _, files in os.walk(s_dir):
        rel = os.path.relpath(root, s_dir)
        out = os.path.normpath(os.path.join(d_dir, rel))
        for name in sorted(files):
            target = os.path.join(out, name)
            if sub == "plates" and name.endswith(PLATE):
                continue
            if sub == "plates" and name.startswith(missing_stems):
                skipped += 1
                continue
            if os.path.exists(target):
                kept += 1
                continue
            made += 1
            print(("linked " if apply else "would link ") + os.path.relpath(target, DST))
            if apply:
                os.makedirs(out, exist_ok=True)
                os.link(os.path.join(root, name), target)
print(f"{'linked' if apply else 'would link'} {made} files; already there {kept}; "
      f"plates of the store with no grain plate: {len(missing)} (their {skipped} side files left out)")
if missing:
    sys.exit(1)
