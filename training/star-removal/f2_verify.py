"""F2's checks on the three master-stretched exports (#1827), run on DE1 where the sets are:

    python -I training/star-removal/f2_verify.py counts <set-folder>...
    python -I training/star-removal/f2_verify.py same <set-a> <set-b> [--ignore Field,Field] [--sessions-of <set-c>]
    python -I training/star-removal/f2_verify.py stretch <set-folder>

`counts` prints the sessions, rows and tile files of each set. `same` compares the three manifests of two sets row by row in
order (the keys named by --ignore left out; --sessions-of keeps only the rows of the sessions that other set holds) and
prints how many rows differ and by which keys. `stretch` reads the Stretch and StretchSkySlopeRatio each row records.
Every verb prints few, long lines, since a node job shows only the last lines of its output.
"""
import collections
import json
import pathlib
import sys

MANIFESTS = ("degradations.jsonl", "tiles-manifest.jsonl", "injections.jsonl")


def rows(path):
    with open(path, encoding="utf-8") as f:
        for line in f:
            if line.strip():
                yield json.loads(line)


def session_of(row):
    return row.get("SessionId") or row.get("Session") or ""


def sessions(folder):
    return sorted({session_of(r) for r in rows(pathlib.Path(folder) / "degradations.jsonl")})


def counts(folder):
    root = pathlib.Path(folder)
    n = {m: sum(1 for _ in rows(root / m)) for m in MANIFESTS}
    deg = list(rows(root / "degradations.jsonl"))
    tiles = sum(1 for p in (root / "tiles").rglob("*") if p.is_file())
    print(f"{folder}: sessions {len(sessions(folder))}, degraded draw rows {len(deg)}, "
          f"rows {n['degradations.jsonl']}/{n['tiles-manifest.jsonl']}/{n['injections.jsonl']}, tile files {tiles}")


def compare(a, b, ignore, keep_sessions):
    out = []
    for name in MANIFESTS:
        total = differing = only_a = only_b = 0
        keys = collections.Counter()
        ra = (r for r in rows(pathlib.Path(a) / name) if not keep_sessions or session_of(r) in keep_sessions)
        rb = (r for r in rows(pathlib.Path(b) / name) if not keep_sessions or session_of(r) in keep_sessions)
        sentinel = object()
        while True:
            x = next(ra, sentinel)
            y = next(rb, sentinel)
            if x is sentinel and y is sentinel:
                break
            if x is sentinel:
                only_b += 1
                continue
            if y is sentinel:
                only_a += 1
                continue
            total += 1
            diff = {k for k in x.keys() | y.keys() if k not in ignore and x.get(k) != y.get(k)}
            if diff:
                differing += 1
                keys.update(diff)
        out.append(f"{name}: {total} paired, {differing} differ{dict(keys) if keys else ''}, only in first {only_a}, only in second {only_b}")
    print(f"{a} vs {b} (ignoring {sorted(ignore)}): " + " | ".join(out))


def stretch(folder):
    seen = collections.Counter()
    ratios = []
    for r in rows(pathlib.Path(folder) / "degradations.jsonl"):
        seen[r.get("Stretch")] += 1
        if r.get("StretchSkySlopeRatio") is not None:
            ratios.append(r["StretchSkySlopeRatio"])
    per_session = {}
    for r in rows(pathlib.Path(folder) / "degradations.jsonl"):
        if r.get("StretchSkySlopeRatio") is not None:
            per_session[session_of(r).split("|")[0][-40:]] = round(r["StretchSkySlopeRatio"], 4)
    print(f"{folder}: Stretch values {dict(seen)}, slope ratio on {len(ratios)} rows, per session {per_session}")


if __name__ == "__main__":
    verb, args = sys.argv[1], sys.argv[2:]
    if verb == "counts":
        for folder in args:
            counts(folder)
    elif verb == "same":
        ignore, keep = set(), None
        folders = []
        i = 0
        while i < len(args):
            if args[i] == "--ignore":
                ignore = set(args[i + 1].split(","))
                i += 2
            elif args[i] == "--sessions-of":
                keep = set(sessions(args[i + 1]))
                i += 2
            else:
                folders.append(args[i])
                i += 1
        compare(folders[0], folders[1], ignore, keep)
    elif verb == "stretch":
        stretch(args[0])
