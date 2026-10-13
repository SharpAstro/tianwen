"""R9 L-GAINS step 0 (RULES.md): the truth's own gains applied THROUGH the shipped sharpening (planetary sharpen --gains) against the
derived gains, on the R8 twins and every finished L-REFINE-T twin. Glue only: every number is the CLI's. Rows are written as they come.

  python step0.py            (appends to step0.csv, skipping twins already in it)
"""
import csv, json, os, re, subprocess

I = "D:/temp/tianwen-scratch/i1056"
T = os.path.join(I, "bin", "tianwen.exe")
S = "D:/temp/tianwen-scratch/planetary/synthetic"
OUT = os.path.join(I, "step0.csv")
SCORE = re.compile(r"\] (.*?) against the truth: transfer ([^;]+); error ([^(]+) \(bands 1 to 4 ([0-9.]+)\).*?limb profile error ([0-9.]+)")
TRUTH = re.compile(r"the truth's own gains, fitted jointly to it: gains ([0-9., -]+?); .*?error ([0-9., ]+?) \(bands", re.M)
EDGE = re.compile(r"derived, outside.*?: gains ([0-9., -]+?); the limb's edge at 0.1 and 0.3 cycles a pixel ([0-9.]+), ([0-9.]+)")


def twins():
    for name, b in (("r8_calibrated", "calibrated"), ("r8_nostill", "nostill"), ("r8w_warped", "warped")):
        d = os.path.join(I, "g2", name)
        yield name, os.path.join(d, f"master_{b}.fits"), f"{S}/{name.split('_')[0]}/{b}.truth.fits", os.path.join(d, "sharpen.log")
    plan = json.load(open(os.path.join(I, "twins", "plan.json")))
    for n in ["t000"] + [f"t{r['index'] + 1:03d}" for r in plan]:
        d = os.path.join(I, "twins", n)
        if os.path.exists(os.path.join(d, "derived", f"master_{n}_sharpened.fits")):
            yield n, os.path.join(d, f"master_{n}.fits"), os.path.join(d, f"{n}.truth.fits"), os.path.join(d, "sharpen.log")


def main():
    done = set()
    if os.path.exists(OUT):
        with open(OUT) as f:
            done = {r["twin"] for r in csv.DictReader(f)}
    fields = ["twin", "derived", "derived_limb", "derived_gains", "edge_01", "edge_03", "truth_gains", "insample", "through", "through_limb",
              "through_bands"]
    new = not os.path.exists(OUT)
    with open(OUT, "a", newline="") as f:
        w = csv.DictWriter(f, fieldnames=fields)
        if new:
            w.writeheader()
        for name, master, truth, log in twins():
            if name in done:
                continue
            text = open(log, encoding="utf-8", errors="replace").read()
            m = TRUTH.search(text)
            derived = [s for s in SCORE.finditer(text) if s.group(1).startswith("derived")]
            edge = EDGE.search(text)
            if not m or not derived:
                print(f"{name}: no truth gains or derived score in its log")
                continue
            gains = [g.strip() for g in m.group(1).split(",")]
            insample = sum(float(v) for v in m.group(2).split(",")[:4])
            out = subprocess.run([T, "planetary", "sharpen", master, "--gains", ",".join(gains), "--truth", truth, "--planet", "jupiter",
                                  "--wavelength", "650", "--telescope", "newtonian", "--no-write"], capture_output=True, text=True).stdout
            given = [s for s in SCORE.finditer(out) if "gains given" in s.group(1)]
            if not given:
                print(f"{name}: no score for the given gains")
                continue
            row = {"twin": name, "derived": float(derived[0].group(4)), "derived_limb": float(derived[0].group(5)),
                   "derived_gains": edge.group(1).strip() if edge else "", "edge_01": edge.group(2) if edge else "",
                   "edge_03": edge.group(3) if edge else "", "truth_gains": ",".join(gains), "insample": round(insample, 3),
                   "through": float(given[0].group(4)), "through_limb": float(given[0].group(5)), "through_bands": given[0].group(3).strip()}
            w.writerow(row)
            f.flush()
            print(f"{name}: derived {row['derived']:.3f}, truth's gains through the sharpening {row['through']:.3f} "
                  f"({100 * (row['through'] / row['derived'] - 1):+.1f} %)", flush=True)


if __name__ == "__main__":
    main()
