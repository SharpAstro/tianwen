"""How far apart the two Pleiades nights stand in a ring file, and how many rows keep their innermost ring (#2309).

    python -I bright_star_nights.py <ring file> <image label> [<ring file> <image label> ...]

A ring file is what bright_star_sheet.py prints for the rows of bright_star_list.tsv (one block a row, one table of
ring medians an image). For each (file, label) this prints, in the red channel:

- on how many rows the image takes under 80 percent of what StarXTerminator takes at 8 to 22 px, and which rows;
- over the eight Pleiads that are on both nights (2022-01-04 and 2022-01-05), the widest difference between the
  nights in the share taken at 8-22, 22-40, 40-60 or 60-80 px, with its star and ring, and the same over Alcyone,
  Maia and Merope alone.

The share is (master - image) / (master - sxt) on the ring's median less its sky, as the plan's tables give it; a
ring where StarXTerminator takes under one pixel sigma is left out. It exists because the first night-to-night
figures in the plan were read off a table of three stars and written as if of all eight (20 and 19 points, where
all eight read 31 and 38)."""
import re, sys

RINGS = ["8 to  22", "22 to  40", "40 to  60", "60 to  80"]
SHOWN = ("Alcyone", "Maia", "Merope")


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
            row.append(None if taken < 1.0 else 100.0 * (images["master"][w] - images[ours][w]) / taken)
        out[star] = row
    return out


def main(args):
    if len(args) < 2 or len(args) % 2:
        sys.exit(__doc__)
    for path, label in zip(args[0::2], args[1::2]):
        s = shares(path, label)
        under = [k for k, v in s.items() if v[0] is not None and round(v[0]) < 80]
        widest, widest_shown = (0, "none"), (0, "none")
        for star in sorted({k.rsplit(" ", 1)[0] for k in s if "2022-01-0" in k and not k.startswith("south")}):
            a, b = s.get(f"{star} 2022-01-05"), s.get(f"{star} 2022-01-04")
            if not a or not b:
                continue
            for i, w in enumerate(RINGS):
                if a[i] is None or b[i] is None:
                    continue
                d = abs(round(a[i]) - round(b[i]))
                where = f"{star} at {'-'.join(p.strip() for p in w.split(' to '))} px"
                if d > widest[0]:
                    widest = (d, where)
                if star in SHOWN and d > widest_shown[0]:
                    widest_shown = (d, where)
        print(f"{label}: the innermost ring is taken under 80 percent on {len(under)} of {len(s)} rows ({', '.join(under) or 'none'})")
        print(f"    the two nights, eight Pleiads: up to {widest[0]} points apart ({widest[1]}); "
              f"{', '.join(SHOWN)}: up to {widest_shown[0]} ({widest_shown[1]})")


if __name__ == "__main__":
    main(sys.argv[1:])
