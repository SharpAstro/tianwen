"""Make the plates plate_grain.py wrote a complete starless root an export can read (#2203, R4f, 2026-11-20).

    python -I grain_store.py <store's starless root> <grain root> [--apply]

The grain root holds plates/<stem>_plate.fits written by plate_grain.py and nothing else. An export reads the plate's
side files too (the star records, the profile, the masks, the stars layer) and the root's stats folder. They are the
store's own, unchanged by the rule, so each is HARD-LINKED into the grain root (same volume, no bytes copied; nothing
writes through a link: an export only reads). Without --apply it lists what it would link and stops. A plate of the
store with no grain plate is named, and is never linked over: the plate is the one file the rule rewrites.

export-synth.ps1 -PlatesRoot <grain root> then exports from it.
"""
import os, sys

SRC, DST = sys.argv[1], sys.argv[2]
apply = "--apply" in sys.argv
made = kept = missing_plate = 0
for sub in ("plates", "stats"):
    s_dir, d_dir = os.path.join(SRC, sub), os.path.join(DST, sub)
    for root, _, files in os.walk(s_dir):
        rel = os.path.relpath(root, s_dir)
        out = os.path.normpath(os.path.join(d_dir, rel))
        for name in files:
            target = os.path.join(out, name)
            if sub == "plates" and name.endswith("_plate.fits"):
                if not os.path.exists(target):
                    missing_plate += 1
                    print("NO GRAIN PLATE FOR", name)
                continue
            if os.path.exists(target):
                kept += 1
                continue
            made += 1
            if apply:
                os.makedirs(out, exist_ok=True)
                os.link(os.path.join(root, name), target)
print(f"{'linked' if apply else 'would link'} {made} files; already there {kept}; plates of the store with no grain plate: {missing_plate}")
