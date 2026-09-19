"""Build the gallery's row table from a bake store: one row per retained session master.

    python build_rows.py <bake store> <rows.json out> [--sky-patch 320]

THE INVENTORY IS THE PRODUCT'S. `tianwen dataset masters` answers, by the code that made the files,
every question this script used to answer by restating a rule of its own: which `.fits` beside a
master is its coverage sidecar, which suffix marks one pier side of a flipped night and which master
is the combined one, how a stats record's session id becomes a file name, whether the header carries
a plate solution, and where the sky is. Each of those restatements had produced a wrong number once
(278 masters in a 139-master store; 92 rows joined to no stats; a "quietest" patch inside blue
reflection nebulosity the render had correctly not called sky). None of them lives here now, and
neither does a FITS reader: this file turns the verb's entries into the page's rows and nothing else.

The sky patch is `Image.FindBackgroundRegion`, the same square background neutralisation measures,
so the card's 1:1 patch and the render's own sky are the same pixels by construction.
"""
import json
import os
import subprocess
import sys
import tempfile

PATCH = 320

# The CLI. Override with TIANWEN_EXE; the default is this repo's own Release build output.
EXE = os.environ.get('TIANWEN_EXE') or os.path.normpath(os.path.join(
    os.path.dirname(os.path.abspath(__file__)), '..', '..', 'src', 'TianWen.Cli',
    'bin', 'Release', 'net10.0', 'tianwen.exe'))


def web_slug(text):
    """A page-safe slug: alphanumerics, '-' and '_' survive, everything else becomes '-'."""
    return "".join(c if c.isalnum() or c in "-_" else "-" for c in text)


def inventory(store, patch):
    """The verb's JSON array, or a loud failure: a half-listed store is worse than none.

    Through a file, never stdout: the CLI's logger shares the console, so a JSON array on stdout can
    arrive with a log line in front of it (every `dbug:` line of a Debug build did exactly that)."""
    out = os.path.join(tempfile.gettempdir(), 'tw-masters-%d.json' % os.getpid())
    proc = subprocess.run([EXE, 'dataset', 'masters', '--store', store, '--sky-patch', str(patch), '--out', out],
                          capture_output=True, text=True, timeout=3600)
    if proc.returncode != 0 or not os.path.exists(out):
        raise SystemExit("tianwen dataset masters failed: " + (proc.stderr or proc.stdout)[-400:])
    try:
        return json.load(open(out, encoding='utf-8'))
    finally:
        os.remove(out)


def row(i, e):
    """One page row from one inventory entry. Field names are the page's; values are the verb's."""
    return {
        "slug": web_slug(e["Name"])[:110],
        "name": e["Name"],
        "object": e["Object"] or e["Name"],
        "date": (e["DateObs"] or "")[:10],
        "camera": e["Camera"] or "unknown camera",
        "filter": e["Filter"] or "None",
        "exposure": float(e["ExposureSeconds"] or 0),
        "w": e["Width"],
        "h": e["Height"],
        "cropX": e["SkyPatchX"] if e["SkyPatchX"] is not None else max(0, (e["Width"] - PATCH) // 2),
        "cropY": e["SkyPatchY"] if e["SkyPatchY"] is not None else max(0, (e["Height"] - PATCH) // 2),
        "strategy": e["Strategy"] or "Float16Staged",
        "subs": e["StackedFrames"],
        "lights": e["Lights"] if e["Lights"] is not None else e["StackedFrames"],
        "fwhm": round(e["MasterFwhm"], 3) if e["MasterFwhm"] is not None else None,
        "train": e["OpticalTrain"],
        "solved": e["Solved"],
        "coverage": e["HasCoverageSidecar"],
        # 'a' / 'b' = one pier side; 'both' = the combined master of a night that WAS split;
        # null = a night that never flipped. flipGroup ties the three together.
        "flip": e["FlipSide"],
        "flipGroup": web_slug(e["FlipGroup"])[:110] if e["FlipGroup"] else None,
        "id": i,
    }


def main():
    store, out_path = sys.argv[1], sys.argv[2]
    patch = int(sys.argv[sys.argv.index('--sky-patch') + 1]) if '--sky-patch' in sys.argv else PATCH
    entries = inventory(store, patch)
    rows = [row(i, e) for i, e in enumerate(entries)]
    json.dump(rows, open(out_path, "w", encoding="utf-8"), separators=(",", ":"))
    solved = sum(1 for r in rows if r["solved"])
    cov = sum(1 for r in rows if r["coverage"])
    split = sum(1 for r in rows if r["flip"] in ("a", "b"))
    both = sum(1 for r in rows if r["flip"] == "both")
    print("%d masters -> %s   solved %d   with a coverage plane %d   "
          "%d pier-side masters over %d flipped nights (+%d combined)"
          % (len(rows), out_path, solved, cov, split, split // 2, both))


if __name__ == "__main__":
    main()
