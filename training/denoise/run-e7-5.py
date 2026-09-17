"""E7.5: the ring target's field dependence, and the window size the field term wants.

Pre-registered in `docs/plans/deconvolver-training.md`, "E7.5, pre-registered". Runs
`n2n_operator_real.py` once per (crop, kernel scale) and files each read as its own text block under
`C:/temp/e2/e7-5/`, the way E7.4's sweep did, so the run is restartable and a single read can be
re-taken without redoing the sweep.

Three legs:

  * the Statue 3x3 grid of 512 px crops, the centre cell being E7.4's nebula crop, all on the frame's
    one base kernel so the scale axis is an ABSOLUTE kernel and comparable between cells;
  * the same centre PLACE at 1024 px, which with E7.4's primary crop makes two places measured at two
    sizes and is the only thing that separates a size effect from a place effect;
  * the Orion E2.10c pair on three crops of its diagonal, its round trip zoom set so its truth width
    lands where the Statue's does (3.245 px native against the Statue's 1.825, so DOWN to 0.715, not
    up to 1.285: the rule is "resample into the prior's band", and Orion sits the other side of it).

Nothing here decides anything; the read is a separate pass over these files.
"""
import argparse
import os
import subprocess
import sys
import time

DENOISE = os.path.dirname(os.path.abspath(__file__))
CACHE = "C:/temp/tianwen-scratch/n2n-p2-blur-clamped-sh61"
CHECKPOINT = "e34d_s0_final.pt"
OUT = "C:/temp/e2/e7-5"

STATUE_SHARP = "C:/temp/e2/e210b-statue/sharp/master_StatueofLibertyNebula_light_60s_-5C_g120.fits"
STATUE_SOFT = "C:/temp/e2/e210b-statue/soft/master_StatueofLibertyNebula_light_60s_-5C_g120.fits"
STATUE_MAPS = "C:/temp/e2/sources-statue"
STATUE_KERNELS = (0.77, 0.91, 0.98)      # the pair probe's est-c, the frame's one base kernel
STATUE_ZOOM = 1.285                      # truth 1.825 px -> 2.34, inside the prior's 2.1 to 2.6 band

ORION_SHARP = "C:/temp/e2/e210c-orion/sharp/master_GreatOrionNebula_light_120s_12C_g120.fits"
ORION_SOFT = "C:/temp/e2/e210c-orion/soft/master_GreatOrionNebula_light_120s_12C_g120.fits"
ORION_MAPS = "C:/temp/e2/sources-orion"
ORION_KERNELS = (1.21, 1.03, 1.05)       # est-c per channel from the E2.10c pair probe
ORION_ZOOM = 0.715                       # truth 3.245 px -> 2.32, the same band from the other side

# Third-octave, symmetric in the ratio the answer is in, spanning both E7.4 crops' picks and the
# left-edge cell that was still asking for a larger kernel at 1.0x.
SCALES = (0.5, 0.63, 0.79, 1.0, 1.26, 1.58, 2.0)

# x in {0, 1256, 2512}, y in {141, 1323, 2505}; (1256, 1323) IS E7.4's nebula crop. The outer cells
# sit at the last origin both masters cover, so the grid spans the frame.
STATUE_GRID = [(x, y) for y in (141, 1323, 2505) for x in (0, 1256, 2512)]
ORION_GRID = [(27, 0), (1339, 1277), (2675, 2547)]


def jobs():
    out = []
    for cx, cy in STATUE_GRID:
        out.append(("statue", STATUE_SHARP, STATUE_SOFT, STATUE_MAPS, STATUE_KERNELS,
                    STATUE_ZOOM, cx, cy, 512))
    out.append(("statue", STATUE_SHARP, STATUE_SOFT, STATUE_MAPS, STATUE_KERNELS,
                STATUE_ZOOM, 1000, 1067, 1024))
    # E7.4's own primary crop, re-read on the full ladder and with the field line, so its pick is
    # taken by the same rule as the grid's rather than read off the published table. The grid's
    # left-middle cell (0, 1323, 512) is a sub-window of it, which makes it the second place
    # measured at both sizes.
    out.append(("statue", STATUE_SHARP, STATUE_SOFT, STATUE_MAPS, STATUE_KERNELS,
                STATUE_ZOOM, 0, 1024, 1024))
    for cx, cy in ORION_GRID:
        out.append(("orion", ORION_SHARP, ORION_SOFT, ORION_MAPS, ORION_KERNELS,
                    ORION_ZOOM, cx, cy, 512))
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dry-run", action="store_true")
    args = ap.parse_args()
    os.makedirs(OUT, exist_ok=True)
    todo = [(j, s) for j in jobs() for s in SCALES]
    print(f"{len(todo)} runs over {len(jobs())} crops x {len(SCALES)} scales")
    if args.dry_run:
        for (frame, _, _, _, k, zoom, cx, cy, size), s in todo[:4]:
            print(f"  {frame} {cx},{cy},{size} x{s} zoom {zoom} kernels "
                  f"{','.join(f'{v * s:.4f}' for v in k)}")
        return 0

    t_all = time.perf_counter()
    done = skipped = failed = 0
    for (frame, sharp, soft, maps, base, zoom, cx, cy, size), scale in todo:
        tag = f"{frame}-{cx}-{cy}-{size}-x{scale:.2f}"
        path = os.path.join(OUT, tag + ".txt")
        if os.path.exists(path) and "ring.self is the ring excess" in open(path, encoding="utf-8").read():
            skipped += 1
            continue
        kernels = ",".join(f"{v * scale:.4f}" for v in base)
        cmd = [sys.executable, "n2n_operator_real.py",
               "--cache", CACHE, "--checkpoint", CHECKPOINT,
               "--sharp", sharp, "--soft", soft,
               "--crop", f"{cx},{cy},{size}", "--kernels", kernels,
               "--zoom", str(zoom), "--roundtrip", "--source-maps", maps]
        t0 = time.perf_counter()
        r = subprocess.run(cmd, cwd=DENOISE, capture_output=True, text=True)
        body = (f"### {frame} crop {cx},{cy},{size} kernel x{scale} ({kernels}) zoom {zoom}\n"
                + r.stdout + (("\n[stderr]\n" + r.stderr) if r.returncode else ""))
        with open(path, "w", encoding="utf-8") as fh:
            fh.write(body)
        if r.returncode:
            failed += 1
            print(f"  FAILED {tag} ({r.returncode}); see {path}", flush=True)
        else:
            done += 1
        print(f"  {done + skipped + failed}/{len(todo)} {tag} {time.perf_counter() - t0:.0f} s",
              flush=True)
    print(f"\n{done} run, {skipped} already present, {failed} failed, "
          f"{(time.perf_counter() - t_all) / 60:.1f} min")
    if not failed:
        open(os.path.join(OUT, "sweep.done"), "w").write(time.strftime("%Y-%m-%d %H:%M:%S\n"))
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
