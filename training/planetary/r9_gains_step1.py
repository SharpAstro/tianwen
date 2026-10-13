"""R9 L-GAINS step 1 (RULES.md): ridge regression from what a master gives at deploy time (its derived gains and the limb's edge at 0.1
and 0.3 cycles a pixel, as `planetary sharpen` prints them) to the truth's own gains, linear (a derived gain can be zero or below).
Trained on the L-REFINE-T training twins (the R8 twins' map out), its ridge strength picked by leave-one-out; every training twin then
scored leave-one-out and every held twin with the full fit, each through `planetary sharpen --gains` (the CLI's score). Rows are written
as they come; G3's power read by r9_score's past_cutoff over the written masters.

  python step1.py
"""
import csv, json, os, re, subprocess, sys
import numpy as np
from astropy.io import fits

sys.path.insert(0, "C:/Users/SebastianGodelet/source/repos/sharpastro/tianwen-r9/training/planetary")
from r9_score import past_cutoff  # noqa: E402

I = "D:/temp/tianwen-scratch/i1056"
T = os.path.join(I, "bin", "tianwen.exe")
S = "D:/temp/tianwen-scratch/planetary/synthetic"
OUT = os.path.join(I, "step1.csv")
BAND = 0.45  # G3 past 0.9 of Nyquist (RULES.md, G3's amendment)
SCORE = re.compile(r"\] (.*?) against the truth: transfer ([^;]+); error ([^(]+) \(bands 1 to 4 ([0-9.]+)\).*?limb profile error ([0-9.]+)")
TROUGH = re.compile(r"\] (.*?): the limb's trough below the truth ([0-9.]+)")
LAMBDAS = [1e-3, 3e-3, 1e-2, 3e-2, 0.1, 0.3, 1, 3, 10, 30, 100]


def paths(name):
    if name.startswith("r8"):
        b = name.split("_")[1]
        d = os.path.join(I, "g2", name)
        return os.path.join(d, f"master_{b}.fits"), f"{S}/{name.split('_')[0]}/{b}.truth.fits", os.path.join(d, "derived", f"master_{b}_sharpened.fits")
    d = os.path.join(I, "twins", name)
    return os.path.join(d, f"master_{name}.fits"), os.path.join(d, f"{name}.truth.fits"), os.path.join(d, "derived", f"master_{name}_sharpened.fits")


def features(row):
    g = [float(v) for v in row["derived_gains"].split(",")[:4]]
    return np.array([*g, float(row["edge_01"]), float(row["edge_03"])])


def target(row):
    return np.array([float(v) for v in row["truth_gains"].split(",")[:4]])


def ridge(x, y, lam):
    mu, sd = x.mean(0), x.std(0) + 1e-12
    z = (x - mu) / sd
    z1 = np.hstack([z, np.ones((len(z), 1))])
    pen = lam * np.eye(z1.shape[1]); pen[-1, -1] = 0  # the intercept is not shrunk
    w = np.linalg.solve(z1.T @ z1 + pen, z1.T @ y)
    return lambda xq: np.hstack([(xq - mu) / sd, np.ones((len(xq), 1))]) @ w


def loo(x, y, lam):
    preds = np.zeros_like(y)
    for i in range(len(x)):
        keep = np.arange(len(x)) != i
        preds[i] = ridge(x[keep], y[keep], lam)(x[i:i + 1])[0]
    return preds


def score(name, gains):
    master, truth, derived = paths(name)
    out_dir = os.path.join(I, "step1", name)
    os.makedirs(out_dir, exist_ok=True)
    out = subprocess.run([T, "planetary", "sharpen", master, "--gains", ",".join(f"{g:.4f}" for g in gains), "--truth", truth, "--planet",
                          "jupiter", "--wavelength", "650", "--telescope", "newtonian", "-o", out_dir], capture_output=True, text=True).stdout
    given = [s for s in SCORE.finditer(out) if "gains given" in s.group(1)]
    trough = [t for t in TROUGH.finditer(out) if "gains given" in t.group(1)]
    written = [f for f in os.listdir(out_dir) if f.endswith("_sharpened.fits")]
    h = fits.getheader(truth)
    disk = (float(h["DISKX"]), float(h["DISKY"]), float(h["DISKR"]))
    power = {k: past_cutoff(fits.getdata(p).astype(float), disk, BAND) for k, p in
             (("derived", derived), ("predicted", os.path.join(out_dir, written[0])), ("truth", truth))} if written else {}
    return (float(given[0].group(4)), given[0].group(3).strip(), float(given[0].group(5)), float(trough[0].group(2)) if trough else np.nan,
            power) if given else None


def main():
    smoke = "--smoke" in sys.argv  # the code on two training twins, left out, nothing held read
    plan = {f"t{r['index'] + 1:03d}": r for r in json.load(open(os.path.join(I, "twins", "plan.json")))}
    plan["t000"] = {"map": "2024c_f502n", "held": False}
    with open(os.path.join(I, "step0.csv")) as f:
        rows = {r["twin"]: r for r in csv.DictReader(f)}
    train = [n for n in rows if n in plan and not plan[n]["held"] and plan[n]["map"] != "2022b_f631n"]
    held = [n for n in rows if n.startswith("r8") or (n in plan and (plan[n]["held"] or plan[n]["map"] == "2022b_f631n"))]
    global OUT
    if smoke:
        held, OUT = [], os.path.join(I, "smoke", "step1.csv")
    x = np.array([features(rows[n]) for n in train]); y = np.array([target(rows[n]) for n in train])
    errors = {lam: float(((loo(x, y, lam) - y) ** 2).sum()) for lam in LAMBDAS}
    lam = min(errors, key=errors.get)
    print(f"{len(train)} training twins, {len(held)} held; ridge strength {lam} by leave-one-out "
          f"(squared error {errors[lam]:.3f}, the derived gains as they are {float(((x[:, :4] - y) ** 2).sum()):.3f})", flush=True)
    model = ridge(x, y, lam)
    preds = dict(zip(train, loo(x, y, lam)))
    for n in held:
        preds[n] = model(features(rows[n])[None])[0]
    done = set()
    if os.path.exists(OUT):
        with open(OUT) as f:
            done = {r["twin"] for r in csv.DictReader(f)}
    fields = ["twin", "set", "derived", "derived_limb", "truth_through", "predicted", "predicted_bands", "predicted_limb", "predicted_trough",
              "derived_gains", "truth_gains", "predicted_gains", "power_derived", "power_predicted", "power_truth"]
    new = not os.path.exists(OUT)
    with open(OUT, "a", newline="") as f:
        w = csv.DictWriter(f, fieldnames=fields)
        if new:
            w.writeheader()
        for n in held + (train[:2] if smoke else train):
            if n in done:
                continue
            d = np.array([float(v) for v in rows[n]["derived_gains"].split(",")[:4]])
            gains = preds[n]
            got = score(n, gains)
            if got is None:
                print(f"{n}: no score for its predicted gains", flush=True)
                continue
            err, bands, limb, trough, power = got
            w.writerow({"twin": n, "set": "held" if n in held else "train-loo", "derived": rows[n]["derived"], "derived_limb": rows[n]["derived_limb"],
                        "truth_through": rows[n]["through"], "predicted": err, "predicted_bands": bands, "predicted_limb": limb,
                        "predicted_trough": trough, "derived_gains": ",".join(f"{v:.2f}" for v in d), "truth_gains": rows[n]["truth_gains"],
                        "predicted_gains": ",".join(f"{v:.2f}" for v in gains), "power_derived": power.get("derived", np.nan),
                        "power_predicted": power.get("predicted", np.nan), "power_truth": power.get("truth", np.nan)})
            f.flush()
            print(f"{n} ({'held' if n in held else 'train, left out'}): derived {float(rows[n]['derived']):.3f}, predicted gains {err:.3f} "
                  f"({100 * (err / float(rows[n]['derived']) - 1):+.1f} %), truth's gains {float(rows[n]['through']):.3f}", flush=True)


if __name__ == "__main__":
    main()
