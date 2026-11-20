"""How far apart the two Pleiades nights stand in a ring file, and how many rows keep their innermost ring (#2309).

    python -I bright_star_nights.py [--floor SIGMA] <ring file> <image label> [<ring file> <image label> ...]

A ring file is outputs of stretch_swap_rings.py put end to end. One run of that script writes one night and frame:
one block a row of bright_star_list.tsv on that night, opened by its "=== " line, with one table of ring medians an
image. What this script needs of the file differs by line:

- the night-to-night gap needs the two Pleiades nights, 2022-01-04 and 2022-01-05, and a file without a Pleiad on
  both stops with a message;
- the count of rows is over the rows the file holds, so the plan's "7 of 26" needs the output of every night of the
  list (26 rows over eight nights; the two Pleiades nights alone hold 18).

For each (file, label) this prints, in the red channel:

- on how many rows the image takes under 80 percent of what StarXTerminator takes at 8 to 22 px, and which rows;
- over the eight Pleiads that are on both nights (2022-01-04 and 2022-01-05), the widest difference between the
  nights in the share taken at 8-22, 22-40, 40-60 or 60-80 px, with its star and ring, and the same over Alcyone,
  Maia and Merope alone.

The share is (master - image) / (master - sxt) on the ring's median less its sky, clamped at 0, and a ring where
StarXTerminator takes under --floor sigma is left out. The floor is 3 by default, as stretch_swap_table.py and
stretched_levels.py have it (the plan's older table left out a ring under a sigma: pass --floor 1 for that).

The floor matters. At 60 and 120 sigma the widest gap over the eight Pleiads is 20 and 19 points at the floor of 3
and 31 and 38 at a floor of 1, the last two set by one ring where StarXTerminator takes 2.6 sigma. This script's
first version had the floor at 1 and so "corrected" a right figure in the plan to a wrong one (2026-11-21)."""
import re, sys

RINGS = ["8 to  22", "22 to  40", "40 to  60", "60 to  80"]
SHOWN = ("Alcyone", "Maia", "Merope")


FLOOR = 3.0


def shares(path, ours):
    out = {}
    for block in open(path, encoding="utf-8").read().split("=== ")[1:]:
        lines = block.splitlines()
        star = re.match(r"^(\S+) \((.+?); ", lines[0]).group(2)
        images, image = {}, None
        for l in lines[1:]:
            m = re.match(r"^([^:]+): ring medians less", l)
            if m:
                image = m.group(1); images[image] = {}
                continue
            for w in RINGS:
                if image and l.strip().startswith(w):
                    images[image][w] = float(l.split(w)[1].split("|")[0].split("/")[0])
        if ours not in images:
            sys.exit(f"FAILED: {path} has no image '{ours}' for {star}; it has {', '.join(images)}")
        row = []
        for w in RINGS:
            taken = images["master"][w] - images["sxt"][w]
            row.append(None if taken < FLOOR else max(0.0, 100.0 * (images["master"][w] - images[ours][w]) / taken))
        out[star] = row
    return out


def main(args):
    global FLOOR
    args = list(args)
    if "--floor" in args:
        at = args.index("--floor")
        try:
            FLOOR = float(args[at + 1])
        except (IndexError, ValueError):
            sys.exit("FAILED: --floor takes a number of sigma, as in --floor 1")
        del args[at:at + 2]
    if len(args) < 2 or len(args) % 2:
        sys.exit(__doc__)
    for path, label in zip(args[0::2], args[1::2]):
        s = shares(path, label)
        under = [k for k, v in s.items() if v[0] is not None and round(v[0]) < 80]
        widest, widest_shown = (0, "none"), (0, "none")
        paired, compared = 0, 0
        for star in sorted({k.rsplit(" ", 1)[0] for k in s if "2022-01-0" in k and not k.startswith("south")}):
            a, b = s.get(f"{star} 2022-01-05"), s.get(f"{star} 2022-01-04")
            if not a or not b:
                continue
            paired += 1
            for i, w in enumerate(RINGS):
                if a[i] is None or b[i] is None:
                    continue
                compared += 1
                d = abs(round(a[i]) - round(b[i]))
                where = f"{star} at {'-'.join(p.strip() for p in w.split(' to '))} px"
                if d > widest[0]:
                    widest = (d, where)
                if star in SHOWN and d > widest_shown[0]:
                    widest_shown = (d, where)
        if not paired:
            sys.exit(f"FAILED: {path} has no Pleiad on both 2022-01-04 and 2022-01-05; "
                     "concatenate the two nights' ring files")
        if not compared:
            sys.exit(f"FAILED: at a floor of {FLOOR:g} sigma {path} has no ring that both nights keep, "
                     "so there is no gap to give")
        print(f"{label}: the innermost ring is taken under 80 percent on {len(under)} of {len(s)} rows "
              f"({', '.join(under) or 'none'})")
        print(f"    the two nights, eight Pleiads: up to {widest[0]} points apart ({widest[1]}); "
              f"{', '.join(SHOWN)}: up to {widest_shown[0]} ({widest_shown[1]})")


if __name__ == "__main__":
    main(sys.argv[1:])
