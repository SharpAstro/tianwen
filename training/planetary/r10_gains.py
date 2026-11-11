"""R10 (#2112, docs/plans/planetary-restoration.md "R10: the learned gains at today's target"): the learned gains refitted at today's
aperture target. Glue only: every score is the CLI's, as in l_gains_s.py and the 2026-11-11 re-check (recheck-1111/run.py).

  python r10_gains.py step0 [twin]    the instrument: re-make one held twin (default d024) from its plan row and seed with the build in
                                      BIN (degrade, then stack), then score its master and the October master through `planetary
                                      sharpen` (the default arm, the aperture truth) and print the pixel difference and both band errors
  python r10_gains.py restack [twin ...]  re-make every twin (or the named ones) into SCR/twins/<twin>/ (degrade + stack, no sharpen);
                                      used only when step 0 shows the stack moved

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


if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else ""
    if cmd == "step0":
        step0(sys.argv[2] if len(sys.argv) > 2 else "d024")
    elif cmd == "restack":
        restack_all(sys.argv[2:] or all_twins())
    else:
        sys.exit(__doc__)
