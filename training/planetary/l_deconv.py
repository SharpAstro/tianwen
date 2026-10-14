"""L-DECONV (RULES.md): a learned chain of the learned gains and a real deconvolution (Richardson-Lucy with the pupil's own diffraction,
`--finish deconvolve`). Glue only: every image is the CLI's, every score the CLI's (`--score-against aperture`) or `planetary judge`'s.

  python deconv.py run [parallel]   every sharpening the arm reads (logs kept, never re-run): training twins, real captures, held twins
  python deconv.py report           N_T from the training twins, the held twins' table, the posts' table, the verdicts as set
"""
import csv, json, os, re, subprocess, sys
from concurrent.futures import ThreadPoolExecutor

sys.path.insert(0, "D:/temp/tianwen-scratch/i1056/deploy")
sys.path.insert(0, "D:/temp/tianwen-scratch/i1056/deploy/chain")
import deploy_steps as ds  # noqa: E402
import gains_s as gs  # noqa: E402
import chain_grid as cg  # noqa: E402

ROOT = "D:/temp/tianwen-scratch/i1056/deploy/deconv"
BIN = "D:/temp/tianwen-scratch/i1056/bin-deconv/tianwen.exe"  # frozen 2026-10-15: the build every L-DECONV run used, kept apart from the branch
APERTURE = ["--score-against", "aperture", "--no-write"]


STEPS = "10,20,40,80"
TRAIN = ["--finish", "none,deconvolve", "--deconvolve-iterations", STEPS]
HELD = {"derived": ["--target", "both"],
        "learned": ["--learned-gains", "--finish", "none,kolivas,deconvolve", "--kolivas-amount", "15.6,31.2", "--deconvolve-iterations", STEPS]}
FINISHED = re.compile(r"finished (kolivas|deconvolve)([0-9.]+)")


def predicted_gains(twin):
    model = json.load(open(gs.MODEL))
    sets = []
    for i, c in enumerate(ds.CH):
        x = [gs.glow(twin, c) if name == "glow" else None for name in model["readings"]]
        sets.append(",".join(f"{g:.4f}" for g in gs.predict(model["colours"][i], x)))
    return ";".join(sets)


def arm_of(label, base):
    """The arm a sharpening's label (or a written file's stem) names: base, base-aperture, base-k<amount> or base-rl<steps>."""
    if (m := FINISHED.search(label)) is not None:
        return f"{base}-{'k' if m.group(1) == 'kolivas' else 'rl'}{m.group(2)}"
    return f"{base}-aperture" if "toward the aperture" in label or "_aperture" in label else base


def jobs():
    """One process a twin (every arm it asks in one run, so the stack's own scores and the truth carried to the aperture are made once),
    the training twins first, then the real captures, then the held twins."""
    out = [(f"twins/{n}/train", gs.ds.twin_paths(n), ["--gains", predicted_gains(n), *TRAIN]) for n in gs.training()]
    for name, (master, planet, _, pupil, waves) in cg.CAPTURES.items():
        common = [master, "--planet", planet, "--wavelength", waves, *pupil, "--finish", "none,deconvolve", "--deconvolve-iterations", STEPS]
        out.append((f"real/{name}/derived", None, common))
        if name == "jup-edgehd":
            out.append((f"real/{name}/learned", None, common + ["--learned-gains"]))
    out += [(f"twins/{n}/{base}", gs.ds.twin_paths(n), extra) for n in gs.held() for base, extra in HELD.items()]
    return out


EXPECTED = {"train": 5, "derived": 2, "learned": 7}


def scores(log):
    """Each arm's channel errors (bands 1 to 4) in a twin's log, the stack's own left out."""
    text = open(log, encoding="utf-8", errors="replace").read()
    base = "rl" if log.endswith("train.log") else os.path.basename(log)[:-4]
    out = {}
    for m in ds.SCORE.finditer(text):
        if m.group(1).startswith("the master as stacked"):
            continue
        arm = arm_of(m.group(1), base)
        if base == "rl":
            arm = "rl0" if arm == "rl" else arm.replace("rl-rl", "rl")
        out.setdefault(arm, {})[m.group(2)] = float(m.group(5))
    return out


def complete(log, twin):
    """Whether a kept log is a whole run's: a twin's scores every channel of every arm it asked, a real run says it wrote all five (a run
    killed mid-way, 2026-10-14 19:48, left a log of the stack's scores alone)."""
    if not os.path.exists(log):
        return False
    if twin:
        s = scores(log)
        return len(s) == EXPECTED["train" if log.endswith("train.log") else os.path.basename(log)[:-4]] and all(len(v) == 3 for v in s.values())
    return open(log, encoding="utf-8", errors="replace").read().count("[planetary] wrote ") == 5


def run(job):
    arm, twin, extra = job
    log = f"{ROOT}/{arm}.log"
    if complete(log, twin is not None):
        return job
    os.makedirs(os.path.dirname(log), exist_ok=True)
    if twin is not None:
        master, truth = twin
        cmd = [BIN, "planetary", "sharpen", master, "--truth", truth, *ds.COMMON, *ds.PUPIL, *APERTURE, *extra]
    else:
        out = f"{ROOT}/{arm}"
        os.makedirs(out, exist_ok=True)
        cmd = [BIN, "planetary", "sharpen", *extra, "-o", out]
    # No console for the run: every death on 2026-10-14 was STATUS_CONTROL_C_EXIT, a console event that reached each process on the
    # job's console at once; a process with none cannot receive one.
    done = subprocess.run(cmd, capture_output=True, text=True, creationflags=subprocess.CREATE_NO_WINDOW)
    # stderr kept after the run's own output, with its exit code: six runs ended together at 21:29:31 with nothing said
    tail = f"[stderr, exit {done.returncode}]" + chr(10) + done.stderr
    text = done.stdout + (chr(10) + tail if done.returncode != 0 or done.stderr else "")
    open(log + ".part", "w", encoding="utf-8").write(text)
    os.replace(log + ".part", log)
    return job


def real_arm(name, base, file):
    """A real capture's arm from its written file's stem: _fnone the gains alone, _fdeconvolve<N> with N steps."""
    m = re.search(r"_fdeconvolve(\d+)", file)
    return f"real/{name}/{base}-rl{m.group(1) if m else 0}"


def another_driver():
    """Whether another pass is still running (its python, by the pid it wrote): an orphaned pass whose task shell died must not run beside
    the pass the task starts next."""
    lock = f"{ROOT}/driver.pid"
    if os.path.exists(lock):
        pid = open(lock).read().strip()
        listed = subprocess.run(["tasklist", "/FI", f"PID eq {pid}", "/FO", "CSV", "/NH"], capture_output=True, text=True).stdout
        if "python" in listed.lower():
            return True
    open(lock, "w").write(str(os.getpid()))
    return False


def main_run(par):
    if another_driver():
        print("another pass is running; this one leaves it be", flush=True)
        return
    order = jobs()
    with ThreadPoolExecutor(par) as pool:
        for i, job in enumerate(pool.map(run, order)):
            print(f"{i + 1}/{len(order)} {job[0]}", flush=True)
    judge_reals(order)
    # A run ended from outside (STATUS_CONTROL_C_EXIT, 2026-10-14) leaves its log incomplete: the scheduled task then runs another pass,
    # which redoes only what is missing, until nothing is.
    missing = [a for a, t, _ in order if not complete(f"{ROOT}/{a}.log", t is not None)]
    if missing:
        print(f"{len(missing)} runs to do again: {', '.join(missing)}", flush=True)
    else:
        open(f"{ROOT}/run.done", "w").write("done")


def judge_reals(order):
    """Every real output of a complete run through the judge, one row each in posts.csv (a row already there is never judged again)."""
    out = f"{ROOT}/posts.csv"
    seen = set()
    if os.path.exists(out):
        seen = {r["arm"] for r in csv.DictReader(open(out))}
    for arm, twin, _ in order:
        if twin is not None or not complete(f"{ROOT}/{arm}.log", False):
            continue
        name, base = arm.split("/")[1], arm.split("/")[2]
        for f in sorted(os.listdir(f"{ROOT}/{arm}")):
            if not f.endswith(".fits"):
                continue
            key = real_arm(name, base, f)
            if key in seen:
                continue
            row = cg.judge((name, base, 0.0, f"{ROOT}/{arm}/{f}"))
            if row is None:
                print(f"{arm}/{f}: the judge read nothing", flush=True)
                continue
            c24 = sum(row[f"b{b}_c"] for b in range(2, 5)) / 3
            new = not os.path.exists(out)
            with open(out, "a", newline="") as fh:
                w = csv.writer(fh)
                if new:
                    w.writerow(["arm", "file", "loss24", "loss14", "c24", *[f"b{b}_{k}" for b in range(1, 6) for k in ("c", "e", "g")]])
                w.writerow([key, f, row["loss24"], row["loss14"], c24, *[row[f"b{b}_{k}"] for b in range(1, 6) for k in ("c", "e", "g")]])
            print(f"judged {key} ({f})", flush=True)


NS = [0, 10, 20, 40, 80]


def loss(channels):
    return sum(channels[c] for c in ds.CH) / 3


def report():
    train = {n: scores(f"{ROOT}/twins/{n}/train.log") for n in gs.training()}
    mean = {k: sum(loss(train[n][f"rl{k}"]) for n in train) / len(train) for k in NS}
    nt = min(mean, key=mean.get)
    print(f"training twins ({len(train)}), mean loss against the aperture truth by N:", ", ".join(f"{k}: {v:.4f}" for k, v in mean.items()), f"-> N_T = {nt}")
    held = {n: {**scores(f"{ROOT}/twins/{n}/derived.log"), **scores(f"{ROOT}/twins/{n}/learned.log")} for n in gs.held()}
    chain = f"learned-rl{nt}" if nt else "learned"
    arms = ["derived", "derived-aperture", "learned", "learned-k15.6", "learned-k31.2"] + [f"learned-rl{k}" for k in NS if k]
    m = {a: sum(loss(held[n][a]) for n in held) / len(held) for a in arms}
    print()
    print(f"held twins ({len(held)}), mean loss against the aperture truth (each twin's after it):")
    for a in arms:
        print(f"  {a:18s} {m[a]:.4f}   " + " ".join(f"{loss(held[n][a]):.3f}" for n in held) + ("   <- the chain" if a == chain else ""))
    v1 = all(m[chain] < m[a] for a in ["derived", "derived-aperture", "learned", "learned-k15.6", "learned-k31.2"] if a != chain)
    print(f"verdict 1 (held twins, {chain}): {'holds' if v1 else 'FAILS'}")
    posts = {r["arm"]: r for r in csv.DictReader(open(f"{ROOT}/posts.csv"))}
    print()
    print("posts: L24 / C24 (bands 2 to 4: correlation / gain)")
    for arm, r in posts.items():
        print(f"  {arm:32s} {float(r['loss24']):.3f} / {float(r['c24']):.4f}   " + "  ".join(f"{float(r[f'b{b}_c']):.3f}/{float(r[f'b{b}_g']):.3f}" for b in range(2, 5)))
    e = posts[f"real/jup-edgehd/learned-rl{nt}"]
    v2 = float(e["c24"]) > 0.9483 and float(e["c24"]) > 0.9333 and float(e["loss24"]) < 0.292 and float(e["loss24"]) < 0.487
    print(f"verdict 2 (EdgeHD Jupiter against its post, learned + RL {nt}): C24 {float(e['c24']):.4f}, L24 {float(e['loss24']):.3f}: {'holds' if v2 else 'FAILS'}")
    print(f"L-DECONV as set: {'holds' if v1 and v2 else 'FAILS'}")
    print()
    print("transfer (derived + RL N_T against derived alone, the other three captures):")
    for name in cg.CAPTURES:
        if name == "jup-edgehd":
            continue
        a, b = posts[f"real/{name}/derived-rl{nt}"], posts[f"real/{name}/derived-rl0"]
        print(f"  {name}: C24 {float(a['c24']):.4f} against {float(b['c24']):.4f}, L24 {float(a['loss24']):.3f} against {float(b['loss24']):.3f}")
    print()
    print("N_P, from the other three captures' mean C24 (derived):")
    for held_name in cg.CAPTURES:
        others = [c for c in cg.CAPTURES if c != held_name]
        np_ = max(NS, key=lambda k: sum(float(posts[f"real/{c}/derived-rl{k}"]["c24"]) for c in others))
        print(f"  {held_name}: N_P {np_}, C24 {float(posts[f'real/{held_name}/derived-rl{np_}']['c24']):.4f} against derived {float(posts[f'real/{held_name}/derived-rl0']['c24']):.4f}")


if __name__ == "__main__":
    if sys.argv[1] == "run":
        main_run(int(sys.argv[2]) if len(sys.argv) > 2 else 6)
    elif sys.argv[1] == "judge":
        judge_reals(jobs())
    else:
        report()
