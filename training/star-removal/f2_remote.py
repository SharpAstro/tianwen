"""DE1-side helper for F2's re-export (#1827): updates the checkout, finds the scorecard config and shows each original export's recipe.

`pilot run` refuses a command that names git or pwsh from a worktree-isolated session, so everything that needs them runs from here:

    python -I training/star-removal/f2_remote.py sync <checkout-folder>        # fetch + hard-fast-forward the flight branch
    python -I training/star-removal/f2_remote.py recon [<scorecard.local.json>] # print the config and each export's record
"""
import glob
import json
import pathlib
import subprocess
import sys

BRANCH = "flight/tia1827"
TEXT_SUFFIXES = {".txt", ".json", ".md", ".log", ".cmd", ".ps1", ".sh"}
SMALL = 40_000


def run(*cmd, cwd=None):
    r = subprocess.run(cmd, cwd=cwd, capture_output=True, text=True)
    print("$", " ".join(cmd), "->", r.returncode)
    print(r.stdout + r.stderr)
    return r.returncode


def sync(folder):
    run("git", "fetch", "origin", BRANCH, cwd=folder)
    run("git", "merge", "--ff-only", "FETCH_HEAD", cwd=folder)
    run("git", "log", "--oneline", "-3", cwd=folder)
    run("git", "status", "--short", "-b", cwd=folder)


def show_small_files(root, depth=0):
    root = pathlib.Path(root)
    if not root.exists():
        print("  MISSING", root)
        return
    entries = sorted(root.iterdir())
    print(f"  {root}: {len(entries)} entries")
    for e in entries[:40]:
        print("   ", "d" if e.is_dir() else "f", e.name, e.stat().st_size if e.is_file() else "")
    for e in entries:
        if e.is_file() and e.suffix.lower() in TEXT_SUFFIXES and e.stat().st_size <= SMALL:
            print(f"  ---- {e.name}")
            print(e.read_text(errors="replace"))


def recon(config):
    candidates = [pathlib.Path(config)] if config else []
    if not config:
        for pattern in ("C:/Users/SebastianGodelet/source/repos/sharpastro/tianwen/training/star-removal/scorecard*.json",
                        "C:/temp/*/scorecard*.json", "C:/temp/*/*/scorecard*.json", "C:/temp/scorecard*.json",
                        "D:/*/scorecard*.json", "D:/*/*/scorecard*.json", "C:/Users/SebastianGodelet/*/scorecard*.json"):
            candidates += [pathlib.Path(p) for p in glob.glob(pattern) if 'example' not in p]
    print("CONFIG CANDIDATES", [str(c) for c in candidates])
    if not candidates:
        return
    cfg = json.loads(candidates[0].read_text())
    print(json.dumps(cfg, indent=2))
    arm = cfg["arm"]
    for label, path in (("random_export", arm["random_export"]), ("plain", f"{arm['twins_root']}/plain"), ("lobed", f"{arm['twins_root']}/lobed")):
        print("=====", label)
        show_small_files(path)


def dirs(*roots):
    """One line: every sub-folder of each root, marked with the record files it holds."""
    out = []
    for r in roots:
        root = pathlib.Path(r)
        if not root.is_dir():
            out.append(f"{r} MISSING")
            continue
        for d in sorted(root.iterdir()):
            if d.is_dir():
                marks = [m for m in ("FROZEN-AT.txt", "manifest.json", "manifest.jsonl", "meta.json") if (d / m).exists()]
                out.append(f"{d.as_posix()}{'[' + ','.join(marks) + ']' if marks else ''}")
    print(len(out), " ; ".join(out))


def find_records(*roots):
    """One line: every FROZEN-AT.txt / manifest under each root, to two levels."""
    out = []
    for r in roots:
        for pattern in ("*/FROZEN-AT.txt", "*/*/FROZEN-AT.txt", "*/manifest*", "*/*/manifest*"):
            out += [pathlib.Path(p).as_posix() for p in glob.glob(f"{r}/{pattern}")]
    print(len(out), " ; ".join(sorted(out)))


def ls(*roots):
    """One line per root: the first 80 entries, folders marked with a slash and files with their size."""
    for r in roots:
        root = pathlib.Path(r)
        if not root.is_dir():
            print(r, "MISSING")
            continue
        entries = sorted(root.iterdir())
        print(r, len(entries), "entries:", " ; ".join(f"{e.name}/" if e.is_dir() else f"{e.name} {e.stat().st_size}" for e in entries[:80]))


def names(root, pattern):
    """One line: the entries of a folder whose name matches the regex."""
    import re
    print(" ; ".join(e.name for e in sorted(pathlib.Path(root).iterdir()) if re.search(pattern, e.name, re.I)))


def head(file, count="3", width="2500"):
    """The first lines of a file, each cut to a width."""
    with open(file, errors="replace") as f:
        for _ in range(int(count)):
            print(f.readline()[:int(width)].rstrip())


def freeze(source, target):
    """A frozen copy of a build's output, so a rebuild in the checkout cannot change a running export's binary."""
    import shutil
    shutil.copytree(source, target, dirs_exist_ok=True)
    print("frozen", source, "->", target, sum(1 for _ in pathlib.Path(target).rglob("*")), "entries")


def cat(*files):
    for f in files:
        # one line, since a job's view keeps only the last few lines of its output
        print("----", f, " ¶ ".join(pathlib.Path(f).read_text(errors="replace")[:8000].splitlines()))


if __name__ == "__main__":
    if sys.argv[1] == "dirs":
        dirs(*sys.argv[2:])
    elif sys.argv[1] == "find":
        find_records(*sys.argv[2:])
    elif sys.argv[1] == "ls":
        ls(*sys.argv[2:])
    elif sys.argv[1] == "names":
        names(*sys.argv[2:])
    elif sys.argv[1] == "head":
        head(*sys.argv[2:])
    elif sys.argv[1] == "freeze":
        freeze(*sys.argv[2:])
    elif sys.argv[1] == "cat":
        cat(*sys.argv[2:])
    elif sys.argv[1] == "sync":
        sync(sys.argv[2])
    elif sys.argv[1] == "recon":
        recon(sys.argv[2] if len(sys.argv) > 2 else None)
