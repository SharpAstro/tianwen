"""L-GAINS-S step B's reading (5) (RULES.md, amended 2026-10-14): each master's light just outside the limb, 1 to 1.1 radii, found over
what the planet's model through the pupil expects there, each channel, as `planetary compare` reads it (PlanetaryPicture.Measure against
PlanetaryPicture.Diffracted). Every finished twin of the three families and the real masters, through the twins' pupil. Glue only; rows
appended to glow.csv, so a stopped run resumes.

  python l_gains_s_glow.py [parallel]
"""
import csv, json, os, subprocess, sys
from concurrent.futures import ThreadPoolExecutor

D = "D:/temp/tianwen-scratch/i1056/deploy"
T = "D:/temp/tianwen-scratch/i1056/bin-s/tianwen.exe"
OUT = f"{D}/glow.csv"
REAL = {"real": "D:/temp/tianwen-scratch/final-1011/out/jup-edgehd/master_2023-11-11-0244_2-MPO_auto.fits",
        "real600": f"{D}/diag/real600/master_real600.fits"}


def masters():
    names = {}
    for plan, prefix in (("plan.json", "d"), ("plan-ext.json", "e"), ("plan-f.json", "f")):
        for r in json.load(open(f"{D}/{plan}")):
            n = f"{prefix}{r['index']:03d}"
            if os.path.exists(f"{D}/{n}/derived/master_{n}_sharpened.fits"):
                names[n] = f"{D}/{n}/master_{n}.fits"
    names.update(REAL)
    return names


def read(item):
    n, path = item
    os.makedirs(f"{D}/glow", exist_ok=True)
    js = f"{D}/glow/{n}.json"
    subprocess.run([T, "planetary", "compare", f"{n}={path}", "--planet", "jupiter", "--wavelength", "610,535,460", "--aperture-mm", "280",
                    "--obstruction", "0.375", "--json", js], capture_output=True, text=True)
    pics = json.load(open(js))["masters"][0]["pictures"]
    return {"twin": n, **{f"{c}_glow": p["picture"]["glowNearFound"] / p["picture"]["glowNearExpected"] for c, p in zip("rgb", pics)},
            **{f"{c}_edge": p["edgeTransfer"][0] for c, p in zip("rgb", pics)}}


def main():
    seen = set()
    if os.path.exists(OUT):
        with open(OUT) as f:
            seen = {r["twin"] for r in csv.DictReader(f)}
    todo = [(n, p) for n, p in masters().items() if n not in seen]
    fields = ["twin", "r_glow", "g_glow", "b_glow", "r_edge", "g_edge", "b_edge"]
    with ThreadPoolExecutor(int(sys.argv[1]) if len(sys.argv) > 1 else 2) as pool:
        for row in pool.map(read, todo):
            new = not os.path.exists(OUT)
            with open(OUT, "a", newline="") as f:
                w = csv.DictWriter(f, fieldnames=fields)
                if new:
                    w.writeheader()
                w.writerow(row)
            print(f"{row['twin']}: glow {row['r_glow']:.3f} / {row['g_glow']:.3f} / {row['b_glow']:.3f}, edge {row['r_edge']:.3f} / {row['g_edge']:.3f} / {row['b_edge']:.3f}", flush=True)


if __name__ == "__main__":
    main()
