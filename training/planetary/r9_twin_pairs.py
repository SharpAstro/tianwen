"""R9 (#1056): gather finished twins (planetary degrade, stack --truth, sharpen) into the refiner's pair layout: <index>_stack.fits (the
master), <index>_sharp.fits (its derived sharpening), <index>_truth.fits (the twin's truth, DISKX/Y/R in its header), and pairs.csv.

  python r9_twin_pairs.py --twins DIR --plan plan.json --out DIR [--held] [--exclude-map M] [--only-map M]
      (training twins by default, the plan's held-out ones with --held; a map left out of training, or only that map's twins)
"""
import argparse, json, os, shutil


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--twins", required=True); ap.add_argument("--plan", required=True); ap.add_argument("--out", required=True)
    ap.add_argument("--held", action="store_true"); ap.add_argument("--extra", nargs="*", default=[], help="other twin names to include")
    ap.add_argument("--exclude-map", nargs="*", default=[]); ap.add_argument("--only-map", nargs="*", default=[])
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)
    plan = json.load(open(a.plan))
    names = [f"t{r['index'] + 1:03d}" for r in plan if (bool(r["held"]) == a.held or a.only_map) and r["map"] not in a.exclude_map
             and (not a.only_map or r["map"] in a.only_map)] + a.extra
    rows = []
    for k, n in enumerate(names):
        d = os.path.join(a.twins, n)
        files = (os.path.join(d, f"master_{n}.fits"), os.path.join(d, "derived", f"master_{n}_sharpened.fits"), os.path.join(d, f"{n}.truth.fits"))
        if not all(os.path.exists(f) for f in files):
            continue
        for src, kind in zip(files, ("stack", "sharp", "truth")):
            shutil.copyfile(src, os.path.join(a.out, f"{k:05d}_{kind}.fits"))
        rows.append(f"{k},{n},,,,,,,,,1")
    with open(os.path.join(a.out, "pairs.csv"), "w") as f:
        f.write("index,map,psf,when,radius_px,north_deg,keep,broaden_px,finest_snr,noise_sigma,size\n")
        f.write("\n".join(rows) + "\n")
    print(f"{len(rows)} twins gathered into {a.out}")


if __name__ == "__main__":
    main()
