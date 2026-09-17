"""Build a target-first view over the filter-first tree, using NTFS directory junctions.

  targets/<target>/<filter>/<date>   ->   lights/<camera>/<filter>/<target>/<date>

Junctions rather than symlinks: mklink /J needs no elevation, where mklink /D needs admin or
Developer Mode. Every tool follows a junction transparently, and Python reports it as a real
directory (isdir true, islink false), so nothing downstream has to know.

Deliberately links LIGHTS ONLY. Junctioning the shared bias and darks in here as well would be
convenient for a single target, but then a recursive scan of targets/ would meet the same bias
frame once per target and ingest it many times over. Calibration stays in exactly one place.
"""
import argparse, os, re, subprocess, sys
from collections import Counter


def is_junction(path):
    """A junction reports as a link to the OS even though Python's isdir follows it."""
    if not os.path.exists(path):
        return False
    try:
        return bool(os.readlink(path))
    except (OSError, ValueError):
        return False


def make_junction(link, target):
    r = subprocess.run(["cmd", "/c", "mklink", "/J", link.replace("/", "\\"),
                        target.replace("/", "\\")],
                       capture_output=True, text=True)
    return r.returncode == 0, (r.stdout + r.stderr).strip()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", default="D:/Astro-Organized")
    ap.add_argument("--apply", action="store_true")
    args = ap.parse_args()

    lights = os.path.join(args.root, "lights").replace("\\", "/")
    if not os.path.isdir(lights):
        print(f"no lights tree at {lights}", file=sys.stderr)
        return 2

    # lights/<camera>/<filter>/<target>/<date>
    plan = []
    for cam in sorted(os.listdir(lights)):
        cdir = f"{lights}/{cam}"
        if not os.path.isdir(cdir):
            continue
        for filt in sorted(os.listdir(cdir)):
            fdir = f"{cdir}/{filt}"
            if not os.path.isdir(fdir):
                continue
            for target in sorted(os.listdir(fdir)):
                tdir = f"{fdir}/{target}"
                if not os.path.isdir(tdir):
                    continue
                for date in sorted(os.listdir(tdir)):
                    ddir = f"{tdir}/{date}"
                    if not os.path.isdir(ddir):
                        continue
                    n = len([x for x in os.listdir(ddir) if re.search(r"(?i)\.fits?$", x)])
                    link = os.path.join(args.root, "targets", target, filt, date).replace("\\", "/")
                    plan.append((link, ddir, target, filt, date, n))

    per_target = Counter()
    for _, _, t, _, _, n in plan:
        per_target[t] += n

    print(f"{'APPLYING' if args.apply else 'DRY RUN'}  root = {args.root}")
    print(f"  junctions to create : {len(plan)}")
    print(f"  distinct targets    : {len(per_target)}\n")
    print(f"  {'target':<22} {'frames':>7}  filters and nights")
    print("  " + "-" * 86)
    bytarget = {}
    for link, src, t, f, d, n in plan:
        bytarget.setdefault(t, []).append((f, d, n))
    for t in sorted(bytarget, key=lambda x: -per_target[x]):
        rows = bytarget[t]
        filters = sorted({f for f, _, _ in rows})
        nights = sorted({d for _, d, _ in rows})
        fs = ", ".join(x.replace("Optolong-", "") for x in filters)
        print(f"  {t:<22} {per_target[t]:>7}  {fs}  ({len(nights)} night"
              f"{'s' if len(nights) != 1 else ''}: {', '.join(nights)})")

    if not args.apply:
        print("\n  nothing created. re-run with --apply.")
        return 0

    made = skipped = failed = 0
    for link, src, t, f, d, n in plan:
        if is_junction(link):
            skipped += 1
            continue
        if os.path.exists(link):
            print(f"  in the way, not a junction: {link}")
            failed += 1
            continue
        os.makedirs(os.path.dirname(link), exist_ok=True)
        ok, msg = make_junction(link, src)
        if ok:
            made += 1
        else:
            failed += 1
            print(f"  FAILED {link}: {msg}")
    print(f"\n  created {made}, already present {skipped}, failed {failed}")

    # prove the view resolves to the same files, not to a copy
    if plan:
        link, src, t, f, d, n = plan[0]
        via = sorted(os.path.basename(x) for x in os.listdir(link)) if os.path.isdir(link) else []
        direct = sorted(os.path.basename(x) for x in os.listdir(src))
        print(f"  spot check {t}/{f}/{d}: {len(via)} via junction, {len(direct)} direct, "
              f"identical={via == direct}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
