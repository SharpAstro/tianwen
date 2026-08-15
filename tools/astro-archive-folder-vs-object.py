"""How far does the organized archive's folder name disagree with the OBJECT header?

Reads the bake's own scan summary rather than walking D:, so it costs no disk I/O against a
running bake. Each summary line is
    [dataset]   <camera>/<filter>/<target-dir>/<date>|<CAMERA>|<OBJECT>|<FILTER>: N lights, ...
The folder is only a label; the bake keys sessions on the header, so a disagreement is an
archive-hygiene defect, not a dataset one. The number that matters is how many FOLDERS hold
more than one object, because that is what a human or a path-trusting tool gets wrong.
"""
import io
import re
import sys
from collections import defaultdict

LINE = re.compile(r"^\[dataset]\s{2,}(\S+)\|([^|]+)\|([^|]+)\|([^|]*): (\d+) lights")


def slug(name):
    """The organizer's folder spelling: non-alphanumerics collapse to a single dash."""
    return re.sub(r"-+", "-", re.sub(r"[^A-Za-z0-9]+", "-", name)).strip("-").lower()


def main(path):
    by_folder = defaultdict(list)
    for line in io.open(path, encoding="utf-8", errors="replace"):
        m = LINE.match(line.rstrip("\n"))
        if m:
            rel, _cam, obj, filt, lights = m.groups()
            by_folder[rel].append((obj, filt, int(lights)))

    if not by_folder:
        print("no session summary lines yet")
        return 1

    print(f"{len(by_folder)} distinct light folders, "
          f"{sum(len(v) for v in by_folder.values())} sessions\n")

    shared, mislabelled = [], []
    for rel, entries in sorted(by_folder.items()):
        target_dir = rel.split("/")[2] if len(rel.split("/")) > 2 else rel
        if len(entries) > 1:
            shared.append((rel, entries))
        # the folder is wrong when NO object in it matches the folder's own name
        if not any(slug(obj) == slug(target_dir) for obj, _f, _n in entries):
            mislabelled.append((rel, target_dir, entries))

    print(f"=== {len(shared)} folders hold MORE THAN ONE object ===")
    for rel, entries in shared:
        print(f"  {rel}")
        for obj, filt, n in entries:
            print(f"      {n:4d} lights  {obj}")

    print(f"\n=== {len(mislabelled)} folders whose name matches NO object inside ===")
    for rel, target_dir, entries in mislabelled:
        print(f"  {rel}")
        print(f"      folder says {target_dir!r}, headers say "
              + ", ".join(f"{obj!r}" for obj, _f, _n in entries))

    affected = sum(n for _r, e in shared for _o, _f, n in e)
    print(f"\nlights sitting in a multi-object folder: {affected}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1]))
