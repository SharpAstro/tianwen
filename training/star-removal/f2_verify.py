"""F2's checks on the three master-stretched exports (#1827), run on DE1 where the sets are:

    python -I training/star-removal/f2_verify.py counts <set-folder>...
    python -I training/star-removal/f2_verify.py same <set-a> <set-b> [--ignore Field,Field] [--sessions-of <set-c>]
    python -I training/star-removal/f2_verify.py stretch <set-folder>

`counts` prints the sessions, rows and tile files of each set. `same` compares the three manifests of two sets row by row in
order (the keys named by --ignore left out; --sessions-of keeps only the rows of the sessions that other set holds) and
prints how many rows differ and by which keys. `lobes` reads what the lobed twin adds to the plain one. `stretch` reads the Stretch and StretchSkySlopeRatio each row records.
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


def strip(value, ignore):
    """A row without the ignored keys at any depth (the Lobe flag sits on each star inside an injection row)."""
    if isinstance(value, dict):
        return {k: strip(v, ignore) for k, v in value.items() if k not in ignore}
    if isinstance(value, list):
        return [strip(v, ignore) for v in value]
    return value


def first_difference(a, b, path=""):
    """The path and the two values at the first place two parsed rows differ, or None."""
    if isinstance(a, dict) and isinstance(b, dict):
        for k in sorted(a.keys() | b.keys()):
            if k not in a or k not in b:
                return f"{path}/{k}: missing in {'first' if k not in a else 'second'}"
            found = first_difference(a[k], b[k], f"{path}/{k}")
            if found:
                return found
        return None
    if isinstance(a, list) and isinstance(b, list):
        if len(a) != len(b):
            return f"{path}: length {len(a)} <> {len(b)}"
        for i, (p, q) in enumerate(zip(a, b)):
            found = first_difference(p, q, f"{path}[{i}]")
            if found:
                return found
        return None
    return None if a == b else f"{path}: {str(a)[:80]} <> {str(b)[:80]}"


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
        examples = []
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
            x, y = strip(x, ignore), strip(y, ignore)
            diff = {k for k in x.keys() | y.keys() if x.get(k) != y.get(k)}
            if diff:
                differing += 1
                keys.update(diff)
                if not examples:
                    k = sorted(diff)[0]
                    examples.append(f"{k}: {first_difference(x.get(k), y.get(k))}")
        out.append(f"{name}: {total} paired, {differing} differ{dict(keys) if keys else ''}, only in first {only_a}, only in second {only_b}{'; first: ' + examples[0] if examples else ''}")
    print(f"{a} vs {b} (ignoring {sorted(ignore)}): " + " | ".join(out))


def lobes(plain, lobed):
    """What the lobed twin adds to the plain one: per injection row, the stars that are not lobes against the plain row's stars."""
    rows_n = rows_with_lobe = lobe_total = same_stars = same_count = 0
    for p, l in zip(rows(pathlib.Path(plain) / "injections.jsonl"), rows(pathlib.Path(lobed) / "injections.jsonl")):
        rows_n += 1
        ls = [st for st in l["Stars"] if st.get("Lobe")]
        wholes = [st for st in l["Stars"] if not st.get("Lobe")]
        lobe_total += len(ls)
        rows_with_lobe += bool(ls)
        same_count += len(wholes) == len(p["Stars"])
        same_stars += strip(wholes, {"Lobe"}) == strip(p["Stars"], {"Lobe"})
    print(f"{lobed} against {plain}: {rows_n} injection rows, {rows_with_lobe} with a lobe, {lobe_total} lobes, "
          f"non-lobe star count equals the plain row's in {same_count}, the stars equal in {same_stars}")


def stretch_params(new, old, wanted=3):
    """The stretch parameters the tile manifests record for the same tiles in a new (master-stretched) and an old (plate-stretched) set."""
    old_by_tile = {r["Tile"]: r for r in rows(pathlib.Path(old) / "tiles-manifest.jsonl")}
    shown = same = differ = 0
    for r in rows(pathlib.Path(new) / "tiles-manifest.jsonl"):
        o = old_by_tile.get(r["Tile"])
        if o is None or r.get("StretchOrigMin") is None:
            continue
        if (r["StretchOrigMin"], r["StretchBalance"]) == (o["StretchOrigMin"], o["StretchBalance"]):
            same += 1
        else:
            differ += 1
        if shown < wanted and r["Frame"] == "master":
            shown += 1
            print("  ", r["Tile"][-60:], "new", r["StretchOrigMin"], r["StretchBalance"], "old", o["StretchOrigMin"], o["StretchBalance"])
    print(f"{new} against {old}: stretch parameters equal on {same} tiles, differ on {differ}")


def star_diff(a, b, wanted=2):
    """How the star lists of two injection manifests differ, star by star, ignoring the Lobe flag."""
    keys = collections.Counter()
    rows_n = counts_differ = stars_n = stars_differ = 0
    examples = []
    for x, y in zip(rows(pathlib.Path(a) / "injections.jsonl"), rows(pathlib.Path(b) / "injections.jsonl")):
        rows_n += 1
        sx, sy = strip(x["Stars"], {"Lobe"}), strip(y["Stars"], {"Lobe"})
        if len(sx) != len(sy):
            counts_differ += 1
        for p, q in zip(sx, sy):
            stars_n += 1
            diff = {k for k in p.keys() | q.keys() if p.get(k) != q.get(k)}
            if diff:
                stars_differ += 1
                keys.update(diff)
                if len(examples) < wanted:
                    examples.append({k: (p.get(k), q.get(k)) for k in sorted(diff)})
        other = {k for k in x.keys() | y.keys() if k != "Stars" and x.get(k) != y.get(k)}
        keys.update("row." + k for k in other)
    print(f"{a} vs {b}: {rows_n} rows, star count differs in {counts_differ}, {stars_differ} of {stars_n} paired stars differ in {dict(keys)}; examples {examples}")


def tiles(new, old, every="200"):
    """Every Nth tile of a new set against the same tile of an old one: do the stretched values differ, and by how much on average."""
    import numpy as np
    out = []
    for i, r in enumerate(rows(pathlib.Path(new) / "tiles-manifest.jsonl")):
        if i % int(every):
            continue
        a = np.fromfile(pathlib.Path(new) / r["Tile"], dtype=np.float16).astype(np.float32)
        b = np.fromfile(pathlib.Path(old) / r["Tile"], dtype=np.float16).astype(np.float32)
        out.append(f"{r['Frame']} {r['Tile'][-34:]}: {'same size' if a.shape == b.shape else 'SIZE DIFFERS'}, "
                   f"median {float(np.median(a)):.5f} new against {float(np.median(b)):.5f} old, mean |difference| {float(np.abs(a - b).mean()):.5f}")
    print(f"{new} against {old}:", " | ".join(out))


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
            v = r["StretchSkySlopeRatio"]
            per_session[session_of(r)[-60:]] = [round(x, 4) for x in v] if isinstance(v, list) else round(v, 4)
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
    elif verb == "lobes":
        lobes(args[0], args[1])
    elif verb == "stretchparams":
        stretch_params(args[0], args[1])
    elif verb == "stardiff":
        star_diff(args[0], args[1])
    elif verb == "tiles":
        tiles(*args)
    elif verb == "stretch":
        stretch(args[0])
