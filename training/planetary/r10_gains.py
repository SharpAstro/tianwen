"""R10 (#2112, docs/plans/planetary-restoration.md "R10: the learned gains at today's target"): the learned gains refitted at today's
aperture target. Glue only: every score is the CLI's, as in l_gains_s.py and the 2026-11-11 re-check (recheck-1111/run.py).

  python r10_gains.py step0 [twin]    the instrument: re-make one held twin (default d024) from its plan row and seed with the build in
                                      BIN (degrade, then stack), then score its master and the October master through `planetary
                                      sharpen` (the default arm, the aperture truth) and print the pixel difference and both band errors
  python r10_gains.py truths [twin ...]  step B: the default arm against the aperture truth on the 40 training twins (the truth's own gains
                                      at that target), logs in SCR/truths, two at a time; ends with SCR/truths.done
  python r10_gains.py train           step B: l_gains_s.py's ridge (glow, one a colour, strength by leave-one-out) fitted to those gains;
                                      the model JSON to SCR/gains-r10-model.json (then l_gains_s_model_cs.py writes the C# constant)
  python r10_gains.py score           the 14 held twins through the built CLI's --learned-gains, logs in SCR/refit; ends with SCR/refit.done
  python r10_gains.py check           the built CLI's learned gains against the script's arithmetic on every held twin
  python r10_gains.py restack [twin ...]  re-make every twin (or the named ones) into SCR/twins/<twin>/ (degrade + stack, no sharpen);
                                      used only when step 0 shows the stack moved
  python r10_gains.py limb [arm ...]  step A: the 14 held twins' October masters through the arms in ARMS (default all), logs kept in
                                      SCR/limb/<twin>-<arm>.log, two twins at a time; ends with SCR/limb.done
  python r10_gains.py b3 | b3-read          B3: the real EdgeHD master sharpened by default and by the learned gains, each judged against its _post
  python r10_gains.py limb-read [arm ...]  read those logs: band error (per band), limb profile error inside and outside, trough, rebound
                                      and undershoot per twin and arm, the means, and each arm's band error against the default's

Scratch is SCR (D:/temp/tianwen-scratch/i2112), never the checkout and never E:/Astro. A finished log or a finished master is never run
again. Stop: create SCR/r10.stop. Every child runs BELOW NORMAL with no window.
"""
import json, os, re, shutil, statistics as st, subprocess, sys
from concurrent.futures import ThreadPoolExecutor

HERE = os.path.dirname(os.path.abspath(__file__))
SCR = "D:/temp/tianwen-scratch/i2112"
BIN = f"{SCR}/bin/tianwen.exe"             # dotnet build src/TianWen.Cli/TianWen.Cli.csproj -c Release -o D:/temp/tianwen-scratch/i2112/bin
OCT = "D:/temp/tianwen-scratch/i1056/deploy"   # the October twins: masters, truths, the statistics (read only here)
STATS = f"{SCR}/stats/edgehd-600.statistics.json"  # a copy of OCT's, so a run can never write into the October folder
EDGE = "D:/Astro-Dataset/planetary/Jupiter-1100 EdgeHD + ASI678MC-f14-DS/2023-11-11-0244_2-MPO.ser"
OPAL = "D:/Astro-Dataset/planetary/opal"
STOP = f"{SCR}/r10.stop"
HELD = ["d024", "d025", "d026", "d027", "d028", "d029", "e002", "e005", "f002", "f005", "f008", "f011", "f014", "f017"]
# What the 2026-11-11 re-check scored with (recheck-1111/run.py BASE), the default arm against the aperture truth.
SCORE = ["--planet", "jupiter", "--wavelength", "610,535,460", "--aperture-mm", "280", "--obstruction", "0.375", "--no-write",
         "--score-against", "aperture"]
SEED_SPREAD = 0.021  # R9: the seed spread of a stack's score (#817 A2, A3), the line between a moved build and noise
PLANS = {"d": f"{HERE}/r9_deploy_twins_plan.json", "e": f"{HERE}/l_gains_s_warped_twins_plan.json",
         "f": f"{HERE}/l_gains_s_blurred_twins_plan.json"}
BELOW_NORMAL, NO_WINDOW = 0x4000, 0x08000000
LINE = re.compile(r"^\[planetary\] (.*?), ([rgb]) against the truth: .*?\(bands 1 to 4 ([0-9.]+)\).*?limb profile error ([0-9.]+); rebound ([0-9.]+)", re.M)


def plan_row(n):
    return next(r for r in json.load(open(PLANS[n[0]])) if r["index"] == int(n[1:]))


def degrade_args(n):
    """The arguments of the maker scripts (r9_make_deploy_twins.sh, l_gains_s_warped_twins.sh, l_gains_s_blurred_twins.sh), per family."""
    r = plan_row(n)
    m = f"{OPAL}/hlsp_opal_hst_wfc3-uvis_jupiter-{r['epoch']}"
    a = ["--map", f"{m}_f631n_v1_globalmap.fits",
         "--bayer-maps", f"{m}_f631n_v1_globalmap.fits,{m}_f502n_v1_globalmap.fits,{m}_f467m_v1_globalmap.fits",
         "--bayer-k", "0.999,0.950,0.950", "--bayer-defocus-nm", ",".join(map(str, r["defocus"])),
         "--aperture-mm", "280", "--obstruction", "0.375", "--exposure-ms", "0", "--r0", str(r["r0"]), "--wind", str(r["wind"]),
         "--outer-scale", "4", "--local-r0", str(r["local"]), "--local-outer-scale", "0.12", "--local-wind", "1",
         "--scatter", str(r["scatter"]), "--scatter-core", "5"]
    if n[0] == "e":
        a += ["--warp-rms", str(r["warp"])]
    if n[0] == "f":
        a += ["--static-blur", str(r["blur"])]
    return a + ["--frames", "600", "--real-statistics", STATS, "--seed", str(r["seed"])]


def child(cmd, log):
    """Run cmd BELOW NORMAL with no window; its stdout and stderr to log (kept). A log that exists is a finished run."""
    if os.path.exists(log):
        return open(log, encoding="utf-8").readline().strip() == "exit 0"
    if os.path.exists(STOP):
        return False
    os.makedirs(os.path.dirname(log), exist_ok=True)
    out = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace",
                         env={**os.environ, "TIANWEN_LOCAL_ONLY": "1"}, creationflags=BELOW_NORMAL | NO_WINDOW)
    with open(log + ".tmp", "w", encoding="utf-8") as f:
        f.write(f"exit {out.returncode}\n{out.stdout}{out.stderr}")
    os.replace(log + ".tmp", log)
    return out.returncode == 0


def restack(n, out):
    """Degrade the twin from its plan row and seed, then stack it: out/<n>.truth.{r,g,b}.fits and out/master_<n>.fits."""
    master = f"{out}/master_{n}.fits"
    if os.path.exists(master):
        return master
    if not child([BIN, "planetary", "degrade", EDGE, *degrade_args(n), "-o", f"{out}/{n}.ser"], f"{out}/degrade.log"):
        raise SystemExit(f"{n}: degrade failed, see {out}/degrade.log")
    if not child([BIN, "planetary", "stack", f"{out}/{n}.ser", "--truth", f"{out}/{n}.truth.fits", "-o", out], f"{out}/stack.log"):
        raise SystemExit(f"{n}: stack failed, see {out}/stack.log")
    if os.path.exists(f"{out}/{n}.ser"):
        os.remove(f"{out}/{n}.ser")
    return master


def score(master, truth, log):
    return child([BIN, "planetary", "sharpen", master, "--truth", truth, *SCORE], log)


def bands(log):
    """{arm: {channel: (band error 1 to 4, limb profile error, rebound)}} from a sharpen log; arm is 'stack' or 'default'."""
    out = {}
    for label, c, err, limb, reb in LINE.findall(open(log, encoding="utf-8").read()):
        out.setdefault("stack" if label.startswith("the master as stacked") else "default", {})[c] = (float(err), float(limb), float(reb))
    return out


# Step A's arms. A: the default (derived gains, the aperture target). B: the truth's own gains fitted jointly to the truth (read off A's
# log) as the 2026-11-11 re-check gave them, nothing else, so the target is whatever --gains leaves it (the telescope). T: the same
# gains with --target aperture, the target they were fitted toward. Further arms of step A are added here, one line each.
ARMS = {"A": lambda own: [],
        "B": lambda own: ["--gains", own],
        "T": lambda own: ["--gains", own, "--target", "aperture"],
        # L: the shipped learned model as it is (it restores toward the telescope, the target it was fitted to). LT: the same model with
        # --target aperture, which is what the Auto rule would do if given and learned gains left the telescope.
        "L": lambda own: ["--learned-gains"],
        "LT": lambda own: ["--learned-gains", "--target", "aperture"]}
OWN = re.compile(r"the truth's own gains, ([rgb]), fitted jointly to it: gains ([0-9., -]+?); ")
BAND = re.compile(r"^\[planetary\] (.*?), ([rgb]) against the truth: .*?error ([0-9., ]+) \(bands 1 to 4 ([0-9.]+)\); undershoot ([0-9.]+); "
                  r"limb profile error ([0-9.]+); rebound ([0-9.]+)", re.M)
TROUGH = re.compile(r"^\[planetary\] (.*?), ([rgb]): the limb's trough below the truth ([0-9.]+), below the stack ([0-9.NaN]+)", re.M)
SIDES = re.compile(r"^\[planetary\] (.*?), ([rgb]): the limb profile's error within \d+ px inside it ([0-9.]+), outside ([0-9.]+)", re.M)


def score_arm(master, truth, log, extra):
    return child([BIN, "planetary", "sharpen", master, "--truth", truth, *SCORE, *extra], log)


def limb_twin(n, arms):
    log = lambda a: f"{SCR}/limb/{n}-{a}.log"
    truth, master = f"{OCT}/{n}/{n}.truth.fits", f"{OCT}/{n}/master_{n}.fits"
    if not score_arm(master, truth, log("A"), []):
        return
    found = dict(OWN.findall(open(log("A"), encoding="utf-8").read()))
    own = ";".join(found[c].replace(" ", "") for c in "rgb")
    for a in arms:
        if a != "A":
            score_arm(master, truth, log(a), ARMS[a](own))


def limb_all(arms):
    with ThreadPoolExecutor(2) as pool:
        list(pool.map(lambda n: limb_twin(n, arms), HELD))
    open(f"{SCR}/limb.done", "w").write("done\n")


def limb_log(log):
    """{channel: dict} for the one sharpened arm of a limb log (the stack's own lines skipped)."""
    text = open(log, encoding="utf-8").read()
    out = {}
    for label, c, errs, band, under, limb, reb in BAND.findall(text):
        if not label.startswith("the master as stacked"):
            out[c] = dict(band=float(band), bands=[float(x) for x in errs.split(",")[:4]], under=float(under), limb=float(limb), rebound=float(reb))
    for label, c, tr, _ in TROUGH.findall(text):
        if not label.startswith("the master as stacked"):
            out[c]["trough"] = float(tr)
    for label, c, i, o in SIDES.findall(text):
        if not label.startswith("the master as stacked"):
            out[c].update(inside=float(i), outside=float(o))
    return out


def limb_read(arms):
    table = {}
    for a in arms:
        rows = {}
        for n in HELD:
            f = f"{SCR}/refit/{n}.log" if a == "R" else f"{SCR}/limb/{n}-{a}.log"
            if os.path.exists(f):
                ch = limb_log(f)
                rows[n] = dict(band=st.mean(v["band"] for v in ch.values()), limb=st.mean(v["limb"] for v in ch.values()),
                               inside=st.mean(v["inside"] for v in ch.values()), outside=st.mean(v["outside"] for v in ch.values()),
                               trough=max(v["trough"] for v in ch.values()), rebound=max(v["rebound"] for v in ch.values()),
                               under=max(v["under"] for v in ch.values()), bands=[st.mean(v["bands"][k] for v in ch.values()) for k in range(4)])
        table[a] = rows
    print(f"{'twin':5s} " + " | ".join(f"{a}: band  limb   in     out    trough rebound under" for a in arms))
    for n in HELD:
        cells = []
        for a in arms:
            r = table[a].get(n)
            cells.append(f"   {r['band']:6.3f} {r['limb']:.4f} {r['inside']:.4f} {r['outside']:.4f} {r['trough']:.4f} {r['rebound']:.4f} {r['under']:.4f}" if r else " " * 50)
        print(f"{n:5s} " + " | ".join(cells))
    for a in arms:
        rows = list(table[a].values())
        if not rows:
            continue
        m = lambda k: st.mean(r[k] for r in rows)
        per = ", ".join(f"{st.mean(r['bands'][k] for r in rows):.3f}" for k in range(4))
        print(f"{a}: {len(rows)} twins, band {m('band'):.3f} (bands 1 to 4: {per}), limb {m('limb'):.4f}, inside {m('inside'):.4f}, "
              f"outside {m('outside'):.4f}, worst trough {max(r['trough'] for r in rows):.4f}, worst rebound {max(r['rebound'] for r in rows):.4f}, "
              f"worst undershoot {max(r['under'] for r in rows):.4f}")
    if table.get("A"):
        base = st.mean(r["band"] for r in table["A"].values())
        for a in arms:
            if a != "A" and table[a]:
                print(f"{a}: band error {100 * (st.mean(r['band'] for r in table[a].values()) / base - 1):+.1f} % against A")


# Step B. The training twins are l_gains_s.py's (its plan files and finished-twin test, in the October folder); the fit is l_gains_s.fit,
# imported, with the truth's own gains read from the aperture-truth logs of `truths` instead of the October logs' telescope-truth ones.
MODEL = f"{SCR}/gains-r10-model.json"


def _ls():
    sys.path.insert(0, HERE)
    import l_gains_s
    return l_gains_s


def truths(names):
    """The default arm (derived gains, the aperture target) against the aperture truth on every training twin, logs kept in
    SCR/truths/<twin>.log: each carries the truth's own gains at that target (the line `limb` also reads)."""
    ls = _ls()
    names = names or ls.training()
    def one(n):
        score_arm(f"{OCT}/{n}/master_{n}.fits", f"{OCT}/{n}/{n}.truth.fits", f"{SCR}/truths/{n}.log", [])
    with ThreadPoolExecutor(2) as pool:
        list(pool.map(one, names))
    open(f"{SCR}/truths.done", "w").write("done\n")


def own_gains(n):
    """{channel: [bands 1 to 4]} the truth's own gains of twin n at the aperture target, read off its truths log."""
    text = open(f"{SCR}/truths/{n}.log", encoding="utf-8").read()
    got = {c: [float(v) for v in g.split(",")[:4]] for c, g in OWN.findall(text)}
    if sorted(got) != ["b", "g", "r"]:
        raise SystemExit(f"{n}: the truth's own gains are not in {SCR}/truths/{n}.log")
    return got


def train():
    """The ridge of L-GAINS-S (l_gains_s.fit: the glow, one a colour, strength by leave-one-out) on the training twins, to the aperture
    truth's own gains. The model JSON goes to MODEL (l_gains_s_model_cs.py reads it)."""
    ls = _ls()
    tr = ls.training()
    missing = [n for n in tr if not os.path.exists(f"{SCR}/truths/{n}.log")]
    if missing:
        raise SystemExit(f"run truths first, no log for {missing}")
    original = ls.ds.read_log
    def aperture_read(n):
        derived, _, gains, edges = original(n)
        return derived, own_gains(n), gains, edges
    ls.ds.read_log = aperture_read
    colours, report = ls.fit(tr, ("glow",))
    ls.ds.read_log = original
    json.dump({"name": f"the EdgeHD 11 Jupiter of 2023-11-11, {len(tr)} twins", "readings": ["glow"], "twins": tr, "colours": colours},
              open(MODEL, "w"), indent=1)
    print(f"{len(tr)} training twins ({sum(n[0] in 'ef' for n in tr)} warped or blurred), the aperture truth's own gains", *report, sep="\n")


def refit(n):
    return f"{SCR}/refit/{n}.log"


GAINS = re.compile(r"\] (.*?): each channel's gains r ([0-9., -]+?); g ([0-9., -]+?); b ([0-9., -]+?)$", re.M)
LEARNED = re.compile(r"the learned gains \(#1552\), (.*)$", re.M)


def score_refit():
    """The 14 held twins through the built CLI's --learned-gains (the refit compiled in, BIN), the aperture truth, logs in SCR/refit."""
    ls = _ls()
    def one(n):
        score_arm(f"{OCT}/{n}/master_{n}.fits", f"{OCT}/{n}/{n}.truth.fits", refit(n), ["--learned-gains"])
    with ThreadPoolExecutor(2) as pool:
        list(pool.map(one, ls.held()))
    open(f"{SCR}/refit.done", "w").write("done\n")


def check():
    """The built CLI's learned gains (from its --learned-gains logs) against this script's arithmetic (l_gains_s.predict on MODEL), every
    held twin and colour the guard did not refuse. Printed to 0.01, so a worst difference of 0.005 is rounding."""
    ls = _ls()
    model, worst = json.load(open(MODEL)), 0.0
    for n in ls.held():
        text = open(refit(n), encoding="utf-8").read()
        g, answers = GAINS.search(text), LEARNED.search(text).group(1)
        _, _, _, edges = ls.ds.read_log(n)
        for i, c in enumerate(ls.ds.CH):
            if f"{c} learned" not in answers:
                continue
            built = [float(v) for v in g.group(i + 2).split(",")[:4]]
            mine = ls.predict(model["colours"][i], [ls.glow(n, c)])
            worst = max(worst, max(abs(a - b) for a, b in zip(built, mine)))
        print(f"{n}: {answers}")
    print(f"the built CLI's gains against this script's, the worst band: {worst:.3f} (printed to 0.01)")


def planes(path):
    import numpy as np
    from astropy.io import fits
    with fits.open(path) as h:
        return np.asarray(next(x.data for x in h if x.data is not None), dtype="float64")


def pixel_report(a, b, what):
    import numpy as np
    x, y = planes(a), planes(b)
    if x.shape != y.shape:
        return f"{what}: shapes differ, {x.shape} against {y.shape}"
    d = np.abs(x - y)
    peak = float(np.nanmax(np.abs(y)))
    return (f"{what}: shape {x.shape}, bit-identical {bool(np.array_equal(x, y, equal_nan=True))}, {100 * float(np.mean(d != 0)):.2f} % of pixels differ, "
            f"max |difference| {float(np.nanmax(d)):.3e} ({100 * float(np.nanmax(d)) / peak:.4f} % of the October peak {peak:.4g}), "
            f"mean {float(np.nanmean(d)):.3e}, rms {float(np.sqrt(np.nanmean(d * d))):.3e} against the October rms {float(np.sqrt(np.nanmean(y * y))):.3e}")


def step0(n):
    out = f"{SCR}/step0/{n}"
    os.makedirs(f"{SCR}/stats", exist_ok=True)
    for c in "rgb":
        if not os.path.exists(f"{STATS}.{c}"):
            shutil.copyfile(f"{OCT}/edgehd-600.statistics.json.{c}", f"{STATS}.{c}")
    new = restack(n, out)
    old, truth = f"{OCT}/{n}/master_{n}.fits", f"{OCT}/{n}/{n}.truth.fits"
    # Both masters against the October truth, so only the master differs. The truth the new run drew is compared as well.
    with ThreadPoolExecutor(2) as pool:
        list(pool.map(lambda a: score(*a), [(old, truth, f"{out}/score-october.log"), (new, truth, f"{out}/score-new.log")]))
    lines = [pixel_report(new, old, f"{n} master, today's stack against the October stack")]
    for c in "rgb":
        lines.append(pixel_report(f"{out}/{n}.truth.{c}.fits", f"{OCT}/{n}/{n}.truth.{c}.fits", f"{n} truth {c}, redrawn against October's"))
    a, b = bands(f"{out}/score-october.log"), bands(f"{out}/score-new.log")
    lines.append(f"{'arm':8s} {'ch':2s} {'October':>8s} {'today':>8s} {'change':>8s}   limb profile error (Oct, today)   rebound (Oct, today)")
    mean = {}
    for arm in ("stack", "default"):
        for c in "rgb":
            o, t = a[arm][c], b[arm][c]
            lines.append(f"{arm:8s} {c:2s} {o[0]:8.3f} {t[0]:8.3f} {100 * (t[0] / o[0] - 1):+7.2f} %   {o[1]:.4f} {t[1]:.4f}   {o[2]:.4f} {t[2]:.4f}")
        mean[arm] = (st.mean(a[arm][c][0] for c in "rgb"), st.mean(b[arm][c][0] for c in "rgb"))
        lines.append(f"{arm:8s} mean {mean[arm][0]:8.3f} {mean[arm][1]:8.3f} {100 * (mean[arm][1] / mean[arm][0] - 1):+7.2f} %")
    d = 100 * (mean["default"][1] / mean["default"][0] - 1)
    lines.append(f"the default arm's band error moved {d:+.2f} % against the seed spread of {100 * SEED_SPREAD:.1f} %: "
                 + ("MOVED (re-stack every twin)" if abs(d) > 100 * SEED_SPREAD else "inside the noise (the October masters stand)"))
    text = "\n".join(lines)
    with open(f"{out}/step0.txt", "w", encoding="utf-8") as f:
        f.write(text + "\n")
    print(text)
    open(f"{SCR}/step0.done", "w").write("done\n")


def restack_all(names):
    with ThreadPoolExecutor(2) as pool:
        list(pool.map(lambda n: restack(n, f"{SCR}/twins/{n}"), names))
    open(f"{SCR}/restack.done", "w").write("done\n")


def all_twins():
    """The 54 twins made in October (30 deploy, 6 of the extension's 18 planned, 18 blurred): the ones with a master in OCT."""
    return sorted(n for k, p in PLANS.items() for r in json.load(open(p))
                  for n in [f"{k}{r['index']:03d}"] if os.path.exists(f"{OCT}/{n}/master_{n}.fits"))


# B3. The real EdgeHD master, sharpened by this build with the default gains and with the learned ones, each judged against the capture's own
# _post (planetary judge); the post is read for the score only, never fitted to.
REAL = "D:/temp/tianwen-scratch/final-1011/out/jup-edgehd/master_2023-11-11-0244_2-MPO_auto.fits"
POST = "D:/Astro-Dataset/planetary/Jupiter-1100 EdgeHD + ASI678MC-f14-DS/2023-11-11-0244_2-MPO_post.png"
JUDGE_ROW = re.compile(r"band (\d):\s+([0-9.-]+)\s+([0-9.-]+)\s+([0-9.-]+)")


def b3():
    pupil = ["--aperture-mm", "279", "--obstruction", "0.33"]
    os.makedirs(f"{SCR}/b3", exist_ok=True)
    for arm, extra in (("default", []), ("refit", ["--learned-gains"])):
        out = f"{SCR}/b3/{arm}"
        child([BIN, "planetary", "sharpen", REAL, "--planet", "jupiter", "--wavelength", "610,535,460", *pupil, *extra, "-o", out], f"{SCR}/b3/{arm}.log")
        sharpened = next(f for f in os.listdir(out) if f.endswith("_sharpened.fits"))
        child([BIN, "planetary", "judge", "--planet", "jupiter", f"{out}/{sharpened}", POST], f"{SCR}/b3/judge-{arm}.log")
    open(f"{SCR}/b3.done", "w").write("done\n")


def b3_read():
    rows = {}
    for arm in ("default", "refit"):
        rows[arm] = {int(b): (float(c), float(e), float(g)) for b, c, e, g in JUDGE_ROW.findall(open(f"{SCR}/b3/judge-{arm}.log", encoding="utf-8").read())}
        c24 = st.mean(rows[arm][b][0] for b in (2, 3, 4))
        print(f"{arm}: C24 {c24:.4f}; " + "; ".join(f"band {b} correlation {v[0]:.3f}, energy {v[1]:.3f}, gain {v[2]:.3f}" for b, v in rows[arm].items()))
    print(f"band 2's gain: refit {rows['refit'][2][2]:.3f} against default {rows['default'][2][2]:.3f} ({100 * (rows['refit'][2][2] / rows['default'][2][2] - 1):+.1f} %)")


if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else ""
    if cmd == "step0":
        step0(sys.argv[2] if len(sys.argv) > 2 else "d024")
    elif cmd == "limb":
        limb_all(sys.argv[2:] or list(ARMS))
    elif cmd == "limb-read":
        limb_read(sys.argv[2:] or list(ARMS))
    elif cmd == "truths":
        truths(sys.argv[2:])
    elif cmd == "train":
        train()
    elif cmd == "score":
        score_refit()
    elif cmd == "check":
        check()
    elif cmd == "b3":
        b3()
    elif cmd == "b3-read":
        b3_read()
    elif cmd == "restack":
        restack_all(sys.argv[2:] or all_twins())
    else:
        sys.exit(__doc__)
