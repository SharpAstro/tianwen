"""The rings file of one changed frame, for stretch_swap_table.py (#2309, 2026-11-20).

    python -I stretch_swap_rings.py <changed frame fits> <frame name> <night> <sxt plate> <plate of the changed frame>
                                    <plate of the frame as it is> <out rings txt> [sheet folder] [list]

For every star of bright_star_list.tsv whose name ends in <night> (2022-01-05 and the like) this runs
bright_star_sheet.py on the changed frame at the star's pinned x, y, with the three plates under the labels
stretch_swap_table.py reads ("sxt", "r4d as trained" for the changed frame's plate, "r4d on the frame as it is"), and
writes its ring medians under a "=== " line. The plates come from `tianwen image remove-stars <frame> --model <onnx>
--views 1 --no-stars --no-protect-cores --no-fill-digs` (ours, on the changed frame and on the frame as it is) and
from the same verb with the registered remover (StarXTerminator, on the frame as it is). A sheet a star is written to
<sheet folder> when one is given.

<frame name> must hold no whitespace: stretch_swap_table.py reads it back as one word. The list is the ninth argument,
so a caller who wants another list and no sheets passes an empty eighth."""
import os, re, subprocess, sys, tempfile

FRAME, NAME, NIGHT, SXT, CHANGED, ASIS, OUT = sys.argv[1:8]
SHEETS = sys.argv[8] if len(sys.argv) > 8 else None
HERE = os.path.dirname(os.path.abspath(__file__))
LIST = sys.argv[9] if len(sys.argv) > 9 else os.path.join(HERE, "bright_star_list.tsv")
failed = 0
with open(OUT, "w", encoding="utf-8") as out:
    for line in open(LIST, encoding="utf-8"):
        p = line.rstrip("\n").split("\t")
        if len(p) < 5 or not p[0].endswith(NIGHT):
            continue
        star, x, y, split = p[0], p[2], p[3], p[4]
        tag = re.sub(r"-+", "-", re.sub(r"[^a-z0-9]", "-", star.lower())).strip("-")
        png = os.path.join(SHEETS, f"swap-{NAME}-{tag}.png") if SHEETS else os.path.join(tempfile.gettempdir(), f"swap-{NAME}-{tag}.png")
        out.write(f"=== {tag} ({star}; {NAME}, x {x}, y {y}, {split})\n"); out.flush()
        r = subprocess.run([sys.executable, "-I", "-W", "ignore", os.path.join(HERE, "bright_star_sheet.py"), FRAME, x, y, "512", png,
                            f"sxt={SXT}", f"r4d as trained={CHANGED}", f"r4d on the frame as it is={ASIS}"], stdout=out, stderr=subprocess.STDOUT)
        failed += r.returncode != 0
        if not SHEETS and os.path.exists(png):
            os.remove(png)
print(f"{OUT}" + (f" ({failed} stars failed)" if failed else ""))
sys.exit(1 if failed else 0)
