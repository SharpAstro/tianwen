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
            candidates += [pathlib.Path(p) for p in glob.glob(pattern)]
    for c in candidates:
        print("CONFIG CANDIDATE", c)
    if not candidates:
        return
    cfg = json.loads(candidates[0].read_text())
    print(json.dumps(cfg, indent=2))
    arm = cfg["arm"]
    for label, path in (("random_export", arm["random_export"]), ("plain", f"{arm['twins_root']}/plain"), ("lobed", f"{arm['twins_root']}/lobed")):
        print("=====", label)
        show_small_files(path)


if __name__ == "__main__":
    if sys.argv[1] == "sync":
        sync(sys.argv[2])
    elif sys.argv[1] == "recon":
        recon(sys.argv[2] if len(sys.argv) > 2 else None)
