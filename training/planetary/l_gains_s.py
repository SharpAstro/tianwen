"""L-GAINS-S (RULES.md): the learned gains made shippable. The deploy family (d000 to d029, no warp) and its extension (e000 on, a local
warp of 1 to 4 px), the edge at 0.1 cycles a pixel alone as the reading (step B's (3)), a ridge per channel. Glue only: every score is
the CLI's, and every learned arm runs through the shipped path (`planetary sharpen --learned-gains`, the model written by gen_model_cs.py
and built into BIN), so what is gated is what ships.

  python l_gains_s.py train         the ridge on the training twins of both families; the model to gains-s-model.json
  python l_gains_s.py check         the built CLI's learned gains against this script's arithmetic, on every held twin (they must agree)
  python l_gains_s.py g2            the held twins: learned (and, on the extension's, the classical comparators); logs kept per arm
  python l_gains_s.py g3            the power past 0.9 of each channel's cutoff, learned over derived, on the held twins
  python l_gains_s.py g4            step C: the limb profile error and the trough, learned through the shipped fix and through bounded
  python l_gains_s.py g6            the real master's edge against the training range, each channel, through the built CLI's own guard
"""
import csv, json, os, re, subprocess, sys
import numpy as np
from astropy.io import fits

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import r9_gains_deploy as ds  # noqa: E402

D = ds.D
# The CLI built from this checkout (dotnet build src/TianWen.Cli -c Release), the trained model compiled in.
BIN = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "src", "TianWen.Cli", "bin", "Release", "net10.0", "tianwen.exe")
MODEL = f"{D}/gains-s-model.json"
OUT = f"{D}/gates-s"
REAL = "D:/temp/tianwen-scratch/final-1011/out/jup-edgehd/master_2023-11-11-0244_2-MPO_auto.fits"
CUTOFF = re.compile(r"the pupil's cutoff, each channel: ([0-9.]+), ([0-9.]+), ([0-9.]+) cycles a pixel")
TROUGH = re.compile(r"\] (.*?), ([rgb]): the limb's trough below the truth ([0-9.]+)")
LEARNED = re.compile(r"the learned gains \(#1552\), (.*)$", re.M)
GAINS = re.compile(r"\] (.*?): each channel's gains r ([0-9., -]+?); g ([0-9., -]+?); b ([0-9., -]+?)$", re.M)


def plan():
    p = {f"d{r['index']:03d}": {**r, "warp": 0.0} for r in json.load(open(f"{D}/plan.json"))}
    if os.path.exists(f"{D}/plan-ext.json"):
        p.update({f"e{r['index']:03d}": r for r in json.load(open(f"{D}/plan-ext.json"))})
    if os.path.exists(f"{D}/plan-f.json"):
        p.update({f"f{r['index']:03d}": r for r in json.load(open(f"{D}/plan-f.json"))})
    return p


def finished():
    return [n for n in sorted(plan()) if os.path.exists(f"{D}/{n}/derived/master_{n}_sharpened.fits")]


def held():
    p = plan()
    return [n for n in finished() if p[n]["held"]]


def cutoffs(n):
    return [float(v) for v in CUTOFF.search(open(f"{D}/{n}/sharpen.log", encoding="utf-8", errors="replace").read()).groups()]


GLOW = {}


def glow(n, c):
    if not GLOW:
        with open(f"{D}/glow.csv") as f:
            GLOW.update({r["twin"]: r for r in csv.DictReader(f)})
    return float(GLOW[n][f"{c}_glow"])


def readings(n, c, names, logs):
    return [glow(n, c) if name == "glow" else logs[n][3][c][0] for name in names]


def fit(train, names=("glow",)):
    """One ridge per channel from the named readings (glow, edge) to the truth's own gains, bands 1 to 4, its strength by leave-one-out;
    in PlanetaryGainsModel's terms (mean, spread, range, cutoff, weights, intercepts)."""
    logs = {n: ds.read_log(n) for n in train}
    colours, report = [], []
    for i, c in enumerate(ds.CH):
        x = np.array([readings(n, c, names, logs) for n in train])
        y = np.array([logs[n][1][c][:4] for n in train])
        errs = {}
        for lam in ds.LAMBDAS:
            pred = np.array([ds.ridge(np.delete(x, k, 0), np.delete(y, k, 0), lam)(x[k:k + 1])[0] for k in range(len(x))])
            errs[lam] = float(((pred - y) ** 2).sum())
        lam = min(errs, key=errs.get)
        mu, sd = x.mean(0), x.std(0) + 1e-12
        z1 = np.hstack([(x - mu) / sd, np.ones((len(x), 1))])
        pen = lam * np.eye(z1.shape[1]); pen[-1, -1] = 0
        w = np.linalg.solve(z1.T @ z1 + pen, z1.T @ y)  # rows: the standardised edge, then the intercept; columns: bands 1 to 4
        cut = sorted({round(cutoffs(n)[i], 4) for n in train})
        k = len(names)
        colours.append({"mean": mu.tolist(), "spread": sd.tolist(), "low": x.min(0).tolist(), "high": x.max(0).tolist(),
                        "cutoff": float(np.mean(cut)), "weights": [[float(w[j, b]) for j in range(k)] for b in range(4)],
                        "intercepts": [float(w[k, b]) for b in range(4)], "strength": lam, "loo": errs[lam]})
        report.append(f"{c}: strength {lam}, leave-one-out {errs[lam]:.2f}, " + ", ".join(f"{name} {x[:, j].min():.3f} to {x[:, j].max():.3f}" for j, name in enumerate(names)) + f", cutoffs {cut}")
    return colours, report


def predict(colour, x):
    return [colour["intercepts"][b] + sum(colour["weights"][b][j] * (x[j] - colour["mean"][j]) / colour["spread"][j] for j in range(len(x))) for b in range(4)]


def training():
    p = plan()
    return [n for n in finished() if not p[n]["held"]]


def stepb():
    """Step B under the 2026-10-14 amendment: (3) the edge, (5) the glow, (6) the glow and the edge, leave-one-out over the training twins."""
    tr = training()
    for names in (("edge",), ("glow",), ("glow", "edge")):
        colours, _ = fit(tr, names)
        loo = [col["loo"] for col in colours]
        print(f"{' and '.join(names)}: leave-one-out {' / '.join(f'{v:.1f}' for v in loo)} / {sum(loo):.1f} ({len(tr)} twins)", flush=True)


def train():
    names = tuple(sys.argv[2].split(",")) if len(sys.argv) > 2 else ("glow",)
    tr = training()
    colours, report = fit(tr, names)
    json.dump({"name": f"the EdgeHD 11 Jupiter of 2023-11-11, {len(tr)} twins", "readings": list(names), "twins": tr, "colours": colours},
              open(MODEL, "w"), indent=1)
    print(f"{len(tr)} training twins ({sum(n[0] in 'ef' for n in tr)} warped or blurred), readings {', '.join(names)}", *report, sep="\n", flush=True)


def run(n, arm, extra):
    """The built CLI's sharpening of held twin n under `extra`, its log kept as gates-s/<n>/<arm>.log (read again, never re-run)."""
    os.makedirs(f"{OUT}/{n}", exist_ok=True)
    log = f"{OUT}/{n}/{arm}.log"
    if not os.path.exists(log):
        master, t = ds.twin_paths(n)
        out = subprocess.run([BIN, "planetary", "sharpen", master, "--truth", t, *ds.COMMON, *ds.PUPIL, "-o", f"{OUT}/{n}/{arm}", *extra],
                             capture_output=True, text=True).stdout
        open(log + ".part", "w", encoding="utf-8").write(out)
        os.replace(log + ".part", log)
    return open(log, encoding="utf-8").read()


def scores(text, label):
    return {m.group(2): (float(m.group(5)), float(m.group(6))) for m in ds.SCORE.finditer(text) if label(m.group(1))}


def troughs(text, label):
    return {m.group(2): float(m.group(3)) for m in TROUGH.finditer(text) if label(m.group(1))}


ARMS = {"learned": ["--learned-gains"], "learned-bounded": ["--learned-gains", "--limb-fix", "bounded"]}
IS_DERIVED = lambda s: s.startswith("derived") and "learned" not in s  # noqa: E731
IS_LEARNED = lambda s: "learned" in s  # noqa: E731


def prefetch():
    """Every sharpening the gates read, run ahead in parallel (the logs are kept and never re-run): the learned arm and the bounded
    one on every held twin, and the classical comparators on the warped and blurred held twins (the deploy family's are in
    deploy-comparators.csv)."""
    from concurrent.futures import ThreadPoolExecutor
    jobs = [(n, arm, ARMS[arm]) for n in held() for arm in ARMS]
    jobs += [(n, arm, extra) for n in held() if n[0] in "ef" for arm, extra in ds.COMPARATORS.items()]
    with ThreadPoolExecutor(int(sys.argv[2]) if len(sys.argv) > 2 else 4) as pool:
        for (n, arm, _), _ in zip(jobs, pool.map(lambda j: run(*j), jobs)):
            print(f"{n} {arm}", flush=True)


def check():
    model = json.load(open(MODEL))
    worst = 0.0
    for n in held():
        text = run(n, "learned", ARMS["learned"])
        _, _, _, edges = ds.read_log(n)
        g = GAINS.search(text)
        answers = LEARNED.search(text).group(1)
        for i, c in enumerate(ds.CH):
            if f"{c} learned" not in answers:
                continue  # refused by the guard: the CLI applied the derived gains, which the model does not predict
            built = [float(v) for v in g.group(i + 2).split(",")[:4]]
            mine = predict(model["colours"][i], [glow(n, c) if name == "glow" else edges[c][0] for name in model["readings"]])
            worst = max(worst, max(abs(a - b) for a, b in zip(built, mine)))
        print(f"{n}: {answers}", flush=True)
    print(f"the built CLI's gains against this script's, the worst band: {worst:.3f} (printed to 0.01)")


def g2():
    p = plan()
    lines = []
    for n in held():
        derived, _, _, _ = ds.read_log(n)
        learned = scores(run(n, "learned", ARMS["learned"]), IS_LEARNED)
        row = [f"{n} warp {p[n]['warp']}: " + "; ".join(f"{c} {derived[c][0]:.3f} to {learned[c][0]:.3f} ({100 * (learned[c][0] / derived[c][0] - 1):+.1f} %)" for c in ds.CH)]
        if n.startswith("e"):
            for arm, extra in ds.COMPARATORS.items():
                s = scores(run(n, arm, extra), IS_DERIVED)
                row.append(f"{arm} {np.mean([s[c][0] for c in ds.CH]):.3f}")
        lines.append(", ".join(row))
        print(lines[-1], flush=True)


def g4():
    out = []
    for n in held():
        derived_text = open(f"{D}/{n}/sharpen.log", encoding="utf-8", errors="replace").read()
        d_s, d_t = scores(derived_text, IS_DERIVED), troughs(derived_text, IS_DERIVED)
        row = {"twin": n, "derived": (np.mean([d_s[c][1] for c in ds.CH]), np.mean([d_t[c] for c in ds.CH]))}
        for arm in ARMS:
            text = run(n, arm, ARMS[arm])
            s, t = scores(text, IS_LEARNED), troughs(text, IS_LEARNED)
            row[arm] = (np.mean([s[c][1] for c in ds.CH]), np.mean([t[c] for c in ds.CH]))
        out.append(row)
        print(f"{n}: limb profile error / trough, mean of the channels: " + "; ".join(f"{k} {v[0]:.4f} / {v[1]:.4f}" for k, v in row.items() if k != "twin"), flush=True)
    for arm in ARMS:
        every = all(r[arm][1] <= r["derived"][1] for r in out)
        print(f"{arm}: trough no deeper than the derived's on every held twin: {every}; means, limb profile error {np.mean([r[arm][0] for r in out]):.4f} "
              f"against {np.mean([r['derived'][0] for r in out]):.4f}, trough {np.mean([r[arm][1] for r in out]):.4f} against {np.mean([r['derived'][1] for r in out]):.4f}")


def g3():
    from r9_score import past_cutoff
    from r9_twin_pairs import offset
    ratios = []
    for n in held():
        run(n, "learned", ARMS["learned"])
        cut = cutoffs(n)
        d_img = fits.getdata(f"{D}/{n}/derived/master_{n}_sharpened.fits").astype(float)
        l_dir = f"{OUT}/{n}/learned"
        l_img = fits.getdata(f"{l_dir}/{[f for f in os.listdir(l_dir) if f.endswith('_learned.fits')][0]}").astype(float)
        _, truth = ds.twin_paths(n)
        for i, c in enumerate(ds.CH):
            t_path = truth.replace(".truth.fits", f".truth.{c}.fits")
            h = fits.getheader(t_path)
            t = fits.getdata(t_path).astype(float)
            x0, y0, r = float(h["DISKX"]), float(h["DISKY"]), float(h["DISKR"])
            sl = (slice(int(y0 - 1.3 * r), int(y0 + 1.3 * r)), slice(int(x0 - 1.3 * r), int(x0 + 1.3 * r)))
            dx, dy = offset(d_img[i][sl], t[sl])
            disk = (x0 + dx, y0 + dy, r)
            ratio = past_cutoff(l_img[i], disk, 0.9 * cut[i]) / past_cutoff(d_img[i], disk, 0.9 * cut[i])
            ratios.append(ratio)
            print(f"{n} {c}: power past {0.9 * cut[i]:.3f} cycles a pixel, learned over derived {ratio:.3f}", flush=True)
    print(f"G3: {min(ratios):.3f} to {max(ratios):.3f} (rule: at most 1.05)")


def g6():
    text = subprocess.run([BIN, "planetary", "sharpen", REAL, *ds.COMMON, *ds.PUPIL, "--learned-gains", "--no-write"], capture_output=True, text=True).stdout
    open(f"{D}/real/g6-learned.log", "w", encoding="utf-8").write(text)
    print(LEARNED.search(text).group(0) if LEARNED.search(text) else "no learned-gains line")
    print(*(m.group(0) for m in ds.EDGES.finditer(text)), sep="\n")


if __name__ == "__main__":
    {"stepb": stepb, "train": train, "prefetch": prefetch, "check": check, "g2": g2, "g3": g3, "g4": g4, "g6": g6}[sys.argv[1]]()
