"""R9 L-GAINS-D (RULES.md): the learned gains at the EdgeHD 11 Jupiter's own sampling. Glue only: every score is the CLI's.

  python deploy_steps.py step0         the truth's own gains (each channel's) through the shipped sharpening, every finished twin
  python deploy_steps.py comparators [twin ...]   the classical options at the cutoff, on the held twins (or those named)
  python deploy_steps.py step1         the per-channel ridge, its strength by leave-one-out over the training twins; held twins scored
  python deploy_steps.py real          G6 (the real master's features against the training twins') and G5 (planetary judge vs its _post)

Rows are appended as they come (deploy-*.csv), so a stopped step resumes.
"""
import csv, json, os, re, subprocess, sys
import numpy as np

D = "D:/temp/tianwen-scratch/i1056/deploy"
T = "D:/temp/tianwen-scratch/i1056/bin/tianwen.exe"
PUPIL = ["--aperture-mm", "280", "--obstruction", "0.375"]
COMMON = ["--planet", "jupiter", "--wavelength", "610,535,460"]
CH = ("r", "g", "b")
SCORE = re.compile(r"\] (.*?), ([rgb]) against the truth: transfer ([^;]+); error ([^(]+) \(bands 1 to 4 ([0-9.]+)\).*?limb profile error ([0-9.]+)")
TRUTH = re.compile(r"the truth's own gains, ([rgb]), fitted jointly to it: gains ([0-9., -]+?); .*?\(bands 1 to 4 ([0-9.]+)\)")
GAINS = re.compile(r"each channel's gains r ([0-9., -]+?); g ([0-9., -]+?); b ([0-9., -]+?)$", re.M)
EDGES = re.compile(r"([rgb]) k [0-9.]+, radius [0-9.]+ px, core [0-9.]+ px, halo [0-9.]+ of [0-9.]+ px, edge ([0-9.]+) / ([0-9.]+)")
LAMBDAS = [1e-3, 3e-3, 1e-2, 3e-2, 0.1, 0.3, 1, 3, 10, 30, 100]
COMPARATORS = {"cutoff": ["--finish", "cutoff"], "edge-reach": ["--edge-reach", "0.2"], "finest-derived": ["--colour-finest", "derived"],
               "all-three": ["--finish", "cutoff", "--edge-reach", "0.2", "--colour-finest", "derived"]}


def plan():
    return {f"d{r['index']:03d}": r for r in json.load(open(f"{D}/plan.json"))}


def finished():
    return [n for n in sorted(plan()) if os.path.exists(f"{D}/{n}/derived/master_{n}_sharpened.fits")]


def read_log(n):
    text = open(f"{D}/{n}/sharpen.log", encoding="utf-8", errors="replace").read()
    derived = {m.group(2): (float(m.group(5)), float(m.group(6))) for m in SCORE.finditer(text) if m.group(1).startswith("derived")}
    truth = {m.group(1): [float(v) for v in m.group(2).split(",")] for m in TRUTH.finditer(text)}
    g = GAINS.search(text)
    gains = {c: [float(v) for v in g.group(i + 1).split(",")[:4]] for i, c in enumerate(CH)} if g else {}
    edges = {m.group(1): (float(m.group(2)), float(m.group(3))) for m in EDGES.finditer(text)}
    return derived, truth, gains, edges


def sharpen(master, truth, extra, label):
    out = subprocess.run([T, "planetary", "sharpen", master, "--truth", truth, *COMMON, *PUPIL, "--no-write", *extra], capture_output=True,
                         text=True).stdout
    return {m.group(2): (float(m.group(5)), float(m.group(6))) for m in SCORE.finditer(out) if label(m.group(1))}


def append(path, fields, row):
    new = not os.path.exists(path)
    with open(path, "a", newline="") as f:
        w = csv.DictWriter(f, fieldnames=fields)
        if new:
            w.writeheader()
        w.writerow(row)


def done(path):
    if not os.path.exists(path):
        return set()
    with open(path) as f:
        return {(r["twin"], r.get("arm", "")) for r in csv.DictReader(f)}


def twin_paths(n):
    return f"{D}/{n}/master_{n}.fits", f"{D}/{n}/{n}.truth.fits"


def row(n, arm, scores, derived):
    r = {"twin": n, "arm": arm, "held": plan()[n]["held"]}
    for c in CH:
        r[f"{c}_derived"], r[f"{c}_derived_limb"] = derived[c]
        r[f"{c}_arm"], r[f"{c}_arm_limb"] = scores.get(c, (np.nan, np.nan))
    r["mean_derived"] = np.mean([derived[c][0] for c in CH])
    r["mean_arm"] = np.mean([scores.get(c, (np.nan,))[0] for c in CH])
    return r


FIELDS = ["twin", "arm", "held", *[f"{c}_{k}" for c in CH for k in ("derived", "derived_limb", "arm", "arm_limb")], "mean_derived", "mean_arm"]


def step0():
    out = f"{D}/deploy-step0.csv"
    seen = done(out)
    for n in finished():
        if (n, "truth") in seen:
            continue
        derived, truth, _, _ = read_log(n)
        given = ";".join(",".join(f"{v:.4f}" for v in truth[c]) for c in CH)
        master, t = twin_paths(n)
        scores = sharpen(master, t, ["--gains", given], lambda label: "gains given" in label)
        r = row(n, "truth", scores, derived)
        append(out, FIELDS, r)
        print(f"{n}: derived {r['mean_derived']:.3f}, the truth's gains through the sharpening {r['mean_arm']:.3f} "
              f"({100 * (r['mean_arm'] / r['mean_derived'] - 1):+.1f} %)", flush=True)


def comparators():
    out = f"{D}/deploy-comparators.csv"
    seen = done(out)
    p = plan()
    only = sys.argv[2:]  # twins named on the command line, so several makers can split the held twins between them
    for n in [k for k in finished() if p[k]["held"] and (not only or k in only)]:  # the held twins: where the rule compares the arms
        derived, _, _, _ = read_log(n)
        master, t = twin_paths(n)
        for arm, extra in COMPARATORS.items():
            if (n, arm) in done(out):  # read again before each run: another maker may have scored it meanwhile
                continue
            scores = sharpen(master, t, extra, lambda label: label.startswith("derived"))
            r = row(n, arm, scores, derived)
            append(out, FIELDS, r)
            print(f"{n} {'held' if p[n]['held'] else 'train'} {arm}: derived {r['mean_derived']:.3f}, {r['mean_arm']:.3f} "
                  f"({100 * (r['mean_arm'] / r['mean_derived'] - 1):+.1f} %)", flush=True)


def ridge(x, y, lam):
    mu, sd = x.mean(0), x.std(0) + 1e-12
    z1 = np.hstack([(x - mu) / sd, np.ones((len(x), 1))])
    pen = lam * np.eye(z1.shape[1]); pen[-1, -1] = 0
    w = np.linalg.solve(z1.T @ z1 + pen, z1.T @ y)
    return lambda q: np.hstack([(q - mu) / sd, np.ones((len(q), 1))]) @ w


def fit_channels(train):
    """One ridge per channel: features the derived gains and the edge's two readings, target the truth's own gains."""
    models, report = {}, []
    logs = {n: read_log(n) for n in train}
    for c in CH:
        x = np.array([[*logs[n][2][c], *logs[n][3][c]] for n in train])
        y = np.array([logs[n][1][c][:4] for n in train])
        errs = {}
        for lam in LAMBDAS:
            pred = np.array([ridge(np.delete(x, i, 0), np.delete(y, i, 0), lam)(x[i:i + 1])[0] for i in range(len(x))])
            errs[lam] = float(((pred - y) ** 2).sum())
        lam = min(errs, key=errs.get)
        models[c] = (ridge(x, y, lam), lam, x)
        report.append(f"{c}: strength {lam}, leave-one-out squared error {errs[lam]:.3f}, the derived gains as they are {float(((x[:, :4] - y) ** 2).sum()):.3f}")
    return models, report


def step1():
    p = plan()
    fin = finished()
    train = [n for n in fin if not p[n]["held"]]
    held = [n for n in fin if p[n]["held"]]
    models, report = fit_channels(train)
    print(f"{len(train)} training twins, {len(held)} held", *report, sep="\n", flush=True)
    json.dump({c: {"strength": models[c][1]} for c in CH}, open(f"{D}/deploy-ridge.json", "w"))
    out = f"{D}/deploy-step1.csv"
    seen = done(out)
    for n in held:
        if (n, "learned") in seen:
            continue
        derived, truth, gains, edges = read_log(n)
        pred = {c: models[c][0](np.array([[*gains[c], *edges[c]]]))[0] for c in CH}
        given = ";".join(",".join(f"{v:.4f}" for v in pred[c]) for c in CH)
        master, t = twin_paths(n)
        scores = sharpen(master, t, ["--gains", given], lambda label: "gains given" in label)
        r = row(n, "learned", scores, derived)
        append(out, FIELDS, r)
        print(f"{n} held: derived {r['mean_derived']:.3f}, learned {r['mean_arm']:.3f} ({100 * (r['mean_arm'] / r['mean_derived'] - 1):+.1f} %); "
              + "; ".join(f"{c} {derived[c][0]:.3f} to {scores.get(c, (np.nan,))[0]:.3f}" for c in CH), flush=True)


def real():
    """G6 and G5 on the real EdgeHD master: its own features (the --auto read), inside the training twins' range?; then both sharpenings
    made by this build with the same settings, each judged against the capture's _post."""
    p = plan()
    train = [n for n in finished() if not p[n]["held"]]
    models, _ = fit_channels(train)
    real_dir = f"{D}/real"
    os.makedirs(real_dir, exist_ok=True)
    master = "D:/temp/tianwen-scratch/final-1011/out/jup-edgehd/master_2023-11-11-0244_2-MPO_auto.fits"
    post = "D:/Astro-Dataset/planetary/Jupiter-1100 EdgeHD + ASI678MC-f14-DS/2023-11-11-0244_2-MPO_post.png"
    pupil = ["--aperture-mm", "279", "--obstruction", "0.33"]  # what --auto identified for this master
    base = [T, "planetary", "sharpen", master, *COMMON, *pupil, "-o"]
    derived_log = subprocess.run(base + [f"{real_dir}/derived"], capture_output=True, text=True).stdout
    open(f"{real_dir}/derived.log", "w").write(derived_log)
    g = GAINS.search(derived_log)
    gains = {c: [float(v) for v in g.group(i + 1).split(",")[:4]] for i, c in enumerate(CH)}
    edges = {m.group(1): (float(m.group(2)), float(m.group(3))) for m in EDGES.finditer(derived_log)}
    inside = True
    for c in CH:
        x = models[c][2]
        f = np.array([*gains[c], *edges[c]])
        lo, hi = x.min(0), x.max(0)
        out = [(i, f[i], lo[i], hi[i]) for i in range(len(f)) if not lo[i] <= f[i] <= hi[i]]
        inside &= not out
        print(f"G6 {c}: features {np.round(f, 3).tolist()}; outside the training range: {[(i, round(v, 3), round(a, 3), round(b, 3)) for i, v, a, b in out]}")
    print(f"G6: {'inside' if inside else 'OUTSIDE'} the training twins' range", flush=True)
    pred = {c: models[c][0](np.array([[*gains[c], *edges[c]]]))[0] for c in CH}
    given = ";".join(",".join(f"{v:.4f}" for v in pred[c]) for c in CH)
    learned_log = subprocess.run(base + [f"{real_dir}/learned", "--gains", given], capture_output=True, text=True).stdout
    open(f"{real_dir}/learned.log", "w").write(learned_log)
    print(f"learned gains: {given}")
    for arm in ("derived", "learned"):
        sharpened = [f for f in os.listdir(f"{real_dir}/{arm}") if f.endswith("_sharpened.fits")][0]
        judge = subprocess.run([T, "planetary", "judge", "--planet", "jupiter", f"{real_dir}/{arm}/{sharpened}", post], capture_output=True, text=True).stdout
        open(f"{real_dir}/judge-{arm}.log", "w").write(judge)
        bands = re.findall(r"band (\d):\s+([0-9.-]+)\s+([0-9.-]+)\s+([0-9.-]+)", judge)
        print(f"G5 {arm}: " + "; ".join(f"band {b} correlation {c}, energy {e}, gain on what both hold {g}" for b, c, e, g in bands), flush=True)


if __name__ == "__main__":
    {"step0": step0, "comparators": comparators, "step1": step1, "real": real}[sys.argv[1]]()
