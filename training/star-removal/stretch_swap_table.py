"""The stretch swap's rings as shares of StarXTerminator's take (red), per star: the changed frame against the frame as it is.
    python -I stretch_swap_table.py <rings file> [...]

The rings file holds one "=== <star> (<name>; <frame>, ..." block per star and frame, each with the "<label>: ring medians less ..."
tables this reads. stretch_swap_rings.py writes it for one changed frame, from the three plates it is given."""
import re, sys
RINGS = [(8, 22), (22, 40), (40, 60), (60, 80), (80, 100), (100, 120)]
for path in sys.argv[1:]:
    text = open(path, encoding="utf-8").read()
    print(path.split("/")[-1])
    for block in text.split("=== ")[1:]:
        lines = block.splitlines()
        m = re.match(r"^\S+ \((.+?); (\S+),", lines[0]); star, frame = m.group(1), m.group(2)
        im, cur = {}, None
        for l in lines[1:]:
            m = re.match(r"^([^:]+): ring medians less", l)
            if m: cur = m.group(1); im[cur] = {}; continue
            m = re.match(r"^\s*(\d+) to\s+(\d+)\s+(.*)$", l)
            if m and cur: im[cur][(int(m.group(1)), int(m.group(2)))] = [float(v) for v in m.group(3).split("|")[0].split("/")]
        def shares(k):
            out = []
            for e in RINGS:
                took = im["master"][e][0] - im["sxt"][e][0]
                out.append("  -" if took < 3 else f"{max(0, 100 * (im['master'][e][0] - im[k][e][0]) / took):3.0f}")
            return " ".join(out)
        c = lambda k: " / ".join(f"{v:.0f}" for v in im[k][(40, 60)])
        print(f"  {star:20s} {frame:18s} changed {shares('r4d as trained')}   as it is {shares('r4d on the frame as it is')}   40-60 px R/G/B: changed {c('r4d as trained')}, as it is {c('r4d on the frame as it is')}, master {c('master')}")
