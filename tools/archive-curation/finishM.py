"""Group M's two provenance records, after the copy (organizeM.py) and the tagging (--verify-tags).

Dry run by default. --apply:
  1. copies session-calibration-map.csv to the next free .bakN, then appends group M's row
     (C:/temp/e2/groupM-calmap-row.csv, 20 columns, checked against the map's header);
  2. appends the group M section (C:/temp/e2/groupM-corrections-section.md) to CORRECTIONS.md.
Refuses when either record is already present, so running it twice changes nothing.
Then run: python targetview.py --apply
"""
import csv
import os
import shutil
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
MAP = os.path.join(HERE, "session-calibration-map.csv")
CORRECTIONS = os.path.join(HERE, "CORRECTIONS.md")
ROW = "C:/temp/e2/groupM-calmap-row.csv"
SECTION = "C:/temp/e2/groupM-corrections-section.md"
SECTION_MARK = "## 2026-09-17: the ASI294MC Eta Carinae night's frame types"


def main():
    apply = "--apply" in sys.argv[1:]
    with open(MAP, encoding="utf-8", newline="") as fh:
        rows = list(csv.reader(fh))
    header, existing = rows[0], rows[1:]
    with open(ROW, encoding="utf-8", newline="") as fh:
        row = next(csv.reader(fh))
    if len(row) != len(header):
        print(f"REFUSE: row has {len(row)} columns, the map {len(header)}")
        return 1
    key = (row[0], row[1], row[2])
    map_done = any((r[0], r[1], r[2]) == key for r in existing)
    with open(CORRECTIONS, encoding="utf-8") as fh:
        corrections_done = SECTION_MARK in fh.read()
    with open(SECTION, encoding="utf-8") as fh:
        section = fh.read()

    # After the highest existing number, never into a gap, and zero-padded to three digits so the
    # series sorts by name. The number is read with or without padding: the older backups are .bak
    # (the first) and .bak2 ... .bak9.
    taken = [int(f[len(os.path.basename(MAP)) + 4:]) for f in os.listdir(HERE)
             if f.startswith(os.path.basename(MAP) + ".bak") and f[len(os.path.basename(MAP)) + 4:].isdigit()]
    backup = f"{MAP}.bak{max(taken, default=1) + 1:03d}"
    print(f"map: {'already has' if map_done else 'append'} {key[0]} / {key[1]} / {key[2]}"
          f" ({len(existing)} rows now){'' if map_done else f', backup to {os.path.basename(backup)}'}")
    print(f"CORRECTIONS.md: {'already has' if corrections_done else 'append'} the group M section")
    if not apply:
        print("dry run; pass --apply")
        return 0

    if not map_done:
        shutil.copy2(MAP, backup)
        with open(MAP, "a", encoding="utf-8", newline="") as fh:
            csv.writer(fh).writerow(row)
        with open(MAP, encoding="utf-8", newline="") as fh:
            after = list(csv.reader(fh))
        if len(after) != len(rows) + 1 or after[-1] != row:
            print(f"FAILED: the map did not read back with the new row; the original is {backup}")
            return 1
    if not corrections_done:
        with open(CORRECTIONS, "a", encoding="utf-8") as fh:
            fh.write(section)
    print("done")
    return 0


if __name__ == "__main__":
    sys.exit(main())
