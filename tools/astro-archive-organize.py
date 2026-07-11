#!/usr/bin/env python
"""Astro archive organizer: file BobbyBox-only sessions into the canonical root.

Consumes the header index produced by astro-archive-dedup.py (fits-index.jsonl) and
classifies every top-level directory under --source against --canonical:

  MOVE       has lights, NONE of them exist in canonical (by header identity), single year
  DUPLICATE  every light already exists in canonical (candidate for hardlink dedup, not move)
  MIXED      some lights unique, some duplicated -- needs a human decision
  NO_LIGHTS  no FITS lights in the index (cal-only, planetary SER, workspaces) -- manual
  MULTI_YEAR unique lights but spanning years -- manual filing

Default is a DRY-RUN that writes move-plan.csv; --apply executes the MOVE rows as
same-volume renames (instant, no data copy) into <canonical>\\<year>\\<original dir name>,
refusing if the target already exists, and records every rename in move-log.csv (the undo
record: swap columns to reverse). Nothing is ever deleted.

Usage:
  python tools/astro-archive-organize.py --index "D:\\Astro-Reports\\fits-index.jsonl" ^
      --source "D:\\BobbyBox-Temp" --canonical "D:\\Astro-Pics" --out "D:\\Astro-Reports"
  ... --apply     # execute the MOVE rows after reviewing move-plan.csv
"""

from __future__ import annotations

import argparse
import csv
import json
import os
import sys
from collections import defaultdict
from datetime import date, datetime, timedelta, timezone
from pathlib import Path

FRAME_DIR_NAMES = {"light", "lights", "dark", "darks", "bias", "biases", "flat", "flats",
                   "darkflat", "darkflats", "autosave", "rawframes"}


def night_of(date_obs: str) -> str:
    if not date_obs:
        return ""
    try:
        dt = datetime.fromisoformat(date_obs.rstrip("Z").split(".")[0])
    except ValueError:
        return ""
    return (dt - timedelta(hours=12)).date().isoformat()


def identity(rec: dict) -> tuple:
    return (rec["camera"], rec["date_obs"], rec["exptime"], rec["naxis1"], rec["naxis2"])


def top_dir(path: str, root: str) -> str | None:
    rel = os.path.relpath(path, root)
    if rel.startswith(".."):
        return None
    head = rel.replace("/", "\\").split("\\", 1)[0]
    return head if head and head != rel else None  # files directly in root have no session dir


def session_dir_of(path: str, root: str) -> str | None:
    """The directory that owns a frame: the parent of the shallowest frame-type folder in
    its path (…\\<session>\\LIGHT\\…), or the file's own directory when frames sit loose.
    A frame-type folder directly under the root is a shared library, not a session."""
    rel = os.path.relpath(path, root)
    if rel.startswith(".."):
        return None
    comps = rel.replace("/", "\\").split("\\")[:-1]
    for i, comp in enumerate(comps):
        if comp.lower() in FRAME_DIR_NAMES:
            return os.path.join(root, *comps[:i]) if i > 0 else None
    return os.path.join(root, *comps) if comps else None


def parse_night(night: str) -> date | None:
    try:
        return date.fromisoformat(night)
    except ValueError:
        return None


def best_elsewhere(candidates: list[dict], session: str, session_nights: set[str]) -> str:
    """Rank a calibration pool (minus in-session frames) by containing directory; returns a
    short human line like '40x in <dir> (±3 d)'."""
    session_dates = [d for n in session_nights if (d := parse_night(n))]
    by_dir: dict[str, dict] = defaultdict(lambda: {"count": 0, "dist": None})
    for r in candidates:
        dir_ = os.path.dirname(r["path"])
        info = by_dir[dir_]
        info["count"] += 1
        cal_date = parse_night(night_of(r["date_obs"]))
        if cal_date and session_dates:
            dist = min(abs((cal_date - s).days) for s in session_dates)
            info["dist"] = dist if info["dist"] is None else min(info["dist"], dist)
    if not by_dir:
        return "none found anywhere"
    ranked = sorted(by_dir.items(),
                    key=lambda kv: (kv[1]["dist"] if kv[1]["dist"] is not None else 9999,
                                    -kv[1]["count"]))
    dir_, info = ranked[0]
    dist = f", ±{info['dist']} d" if info["dist"] is not None else ""
    return f"{info['count']}x in {dir_}{dist}"


def cal_key(kind: str, r: dict) -> tuple:
    if kind == "DARK":
        return ("DARK", r["camera"], r["exptime"], r["gain"], r["binning"])
    if kind == "BIAS":
        return ("BIAS", r["camera"], r["gain"], r["binning"])
    if kind == "FLAT":
        return ("FLAT", r["camera"], r["filter"], r["binning"])
    return ("DARKFLAT", r["camera"], r["gain"], r["binning"])


def write_summaries(records: list[dict], roots: list[str]) -> int:
    """Drop a machine-written _session-summary.md into every session dir that owns raw
    lights: light inventory + in-session calibration counts + most-likely matches elsewhere."""
    raw = [r for r in records if not r["processed_path"] and not r["stack_n"]]
    sessions: dict[str, dict] = defaultdict(lambda: {"lights": [], "cal": defaultdict(list)})
    pools: dict[tuple, list[dict]] = defaultdict(list)
    for r in raw:
        root = next((rt for rt in roots if os.path.normpath(r["root"]) == os.path.normpath(rt)), None)
        if root is None:
            continue
        session = session_dir_of(r["path"], root)
        kind = r["type"]
        if kind == "LIGHT":
            if session:
                sessions[session]["lights"].append(r)
            continue
        if kind == "DARK":
            pools[("DARK", r["camera"], r["exptime"], r["gain"], r["binning"])].append(r)
        elif kind == "BIAS":
            pools[("BIAS", r["camera"], r["gain"], r["binning"])].append(r)
        elif kind == "FLAT":
            pools[("FLAT", r["camera"], r["filter"], r["binning"])].append(r)
        elif kind == "DARKFLAT":
            pools[("DARKFLAT", r["camera"], r["gain"], r["binning"])].append(r)
        if session:
            sessions[session]["cal"][kind].append(r)

    written = 0
    today = datetime.now(timezone.utc).date().isoformat()
    for session, info in sessions.items():
        lights = info["lights"]
        if not lights or not os.path.isdir(session):
            continue
        nights = sorted({n for r in lights if (n := night_of(r["date_obs"]))})
        cameras = sorted({r["camera"] for r in lights if r["camera"]})
        bayer = sorted({r["bayer"] for r in lights if r["bayer"]})
        groups: dict[tuple, int] = defaultdict(int)
        for r in lights:
            groups[(r["filter"] or "-", r["exptime"], r["gain"], r["binning"], r["camera"])] += 1

        in_session = {kind: info["cal"].get(kind, []) for kind in ("DARK", "BIAS", "FLAT", "DARKFLAT")}
        lines = [
            f"# Session summary - {os.path.basename(session)}",
            "",
            f"> Machine-written by `tools/astro-archive-organize.py --write-summaries` on {today};",
            "> safe to delete, regenerated on the next organize run. Matching is by FITS header",
            "> (camera + exposure + gain + binning [+ filter]); sensor temperature is NOT compared.",
            "",
            f"- Nights: {nights[0]} .. {nights[-1]} ({len(nights)})" if nights else "- Nights: unknown",
            f"- Camera: {', '.join(cameras) or 'unknown'}" + (f" ({', '.join(bayer)})" if bayer else ""),
            "",
            "## Lights",
            "",
            "| Filter | Exp (s) | Gain | Bin | Count | Integration |",
            "|---|---|---|---|---|---|",
        ]
        for (filt, exptime, gain, binning, camera), count in sorted(groups.items(), key=lambda kv: str(kv[0])):
            integ = f"{count * exptime / 3600:.1f} h" if exptime else "?"
            lines.append(f"| {filt} | {exptime if exptime is not None else '?'} | "
                         f"{gain if gain is not None else '?'} | {binning or '?'} | {count} | {integ} |")
        lines += ["", "## Calibration", "",
                  "| Kind | For | In session | Most likely elsewhere |", "|---|---|---|---|"]
        seen_cal_rows: set[tuple] = set()
        for (filt, exptime, gain, binning, camera), _count in sorted(groups.items(), key=lambda kv: str(kv[0])):
            for kind, key in (("DARK", ("DARK", camera, exptime, gain, binning)),
                              ("BIAS", ("BIAS", camera, gain, binning)),
                              ("FLAT", ("FLAT", camera, filt if filt != "-" else "", binning)),
                              ("DARKFLAT", ("DARKFLAT", camera, gain, binning))):
                if key in seen_cal_rows:
                    continue
                seen_cal_rows.add(key)
                own = [r for r in in_session[kind] if cal_key(kind, r) == key]
                label = {"DARK": f"{exptime if exptime is not None else '?'} s g{gain} b{binning}",
                         "BIAS": f"g{gain} b{binning}",
                         "FLAT": f"{filt} b{binning}",
                         "DARKFLAT": f"g{gain} b{binning}"}[kind]
                if own:
                    lines.append(f"| {kind.title()} | {label} | {len(own)} | - |")
                else:
                    elsewhere = best_elsewhere(pools.get(key, []), session, set(nights))
                    lines.append(f"| {kind.title()} | {label} | 0 | {elsewhere} |")
        lines.append("")
        (Path(session) / "_session-summary.md").write_text("\n".join(lines), encoding="utf-8")
        written += 1
    return written


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--index", required=True, help="fits-index.jsonl from astro-archive-dedup.py")
    ap.add_argument("--source", required=True, help="root whose unique sessions get filed (BobbyBox)")
    ap.add_argument("--canonical", required=True, help="canonical archive root (Astro-Pics)")
    ap.add_argument("--out", required=True, help="directory for move-plan.csv / move-log.csv")
    ap.add_argument("--apply", action="store_true", help="execute MOVE rows (default: dry-run)")
    ap.add_argument("--write-summaries", action="store_true",
                    help="write _session-summary.md into every session dir that owns raw lights")
    args = ap.parse_args()

    out_dir = Path(args.out)
    out_dir.mkdir(parents=True, exist_ok=True)
    source = os.path.normpath(args.source)
    canonical = os.path.normpath(args.canonical)

    records = []
    with open(args.index, "r", encoding="utf-8") as f:
        for line in f:
            try:
                records.append(json.loads(line))
            except json.JSONDecodeError:
                continue

    # Header-identity set of every raw light already in the canonical root.
    canonical_lights = set()
    for r in records:
        if os.path.normpath(r["root"]) == canonical and r["type"] == "LIGHT" and not r["processed_path"]:
            canonical_lights.add(identity(r))

    # Per source top-level dir: light/dup counts, frame types, nights, years.
    dirs: dict[str, dict] = defaultdict(lambda: {
        "lights": 0, "dup_lights": 0, "frames": defaultdict(int), "nights": set(), "years": set()})
    for r in records:
        if os.path.normpath(r["root"]) != source:
            continue
        d = top_dir(r["path"], source)
        if d is None:
            continue
        info = dirs[d]
        info["frames"][r["type"]] += 1
        if r["type"] == "LIGHT" and not r["processed_path"]:
            info["lights"] += 1
            if identity(r) in canonical_lights:
                info["dup_lights"] += 1
            night = night_of(r["date_obs"])
            if night:
                info["nights"].add(night)
                info["years"].add(night[:4])

    plan_rows = []
    for d, info in sorted(dirs.items()):
        lights, dups = info["lights"], info["dup_lights"]
        years = sorted(info["years"])
        if lights == 0:
            cls, target = "NO_LIGHTS", ""
        elif dups == lights:
            cls, target = "DUPLICATE", ""
        elif dups > 0:
            cls, target = "MIXED", ""
        elif len(years) != 1:
            cls, target = "MULTI_YEAR", ""
        else:
            cls = "MOVE"
            target = os.path.join(canonical, years[0], d)
        plan_rows.append({
            "class": cls,
            "dir": os.path.join(source, d),
            "target": target,
            "lights": lights,
            "dup_lights": dups,
            "nights": len(info["nights"]),
            "years": ";".join(years),
            "frame_types": ";".join(f"{k}:{v}" for k, v in sorted(info["frames"].items())),
        })

    plan_path = out_dir / "move-plan.csv"
    with open(plan_path, "w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=["class", "dir", "target", "lights", "dup_lights",
                                          "nights", "years", "frame_types"])
        w.writeheader()
        w.writerows(plan_rows)

    by_class = defaultdict(list)
    for row in plan_rows:
        by_class[row["class"]].append(row)
    print(f"[plan] {plan_path}")
    for cls in ("MOVE", "MIXED", "DUPLICATE", "MULTI_YEAR", "NO_LIGHTS"):
        rows = by_class.get(cls, [])
        n_lights = sum(r["lights"] for r in rows)
        print(f"  {cls:<10} {len(rows):>3} dirs, {n_lights:>6} lights")
        if cls in ("MOVE", "MIXED", "MULTI_YEAR"):
            for r in rows:
                print(f"    {r['dir']}  (lights={r['lights']} dup={r['dup_lights']} years={r['years']})"
                      + (f" -> {r['target']}" if r["target"] else ""))

    if not args.apply:
        print("[dry-run] no moves executed; re-run with --apply to execute the MOVE rows")
        if args.write_summaries:
            written = write_summaries(records, [source, canonical])
            print(f"[summaries] {written} _session-summary.md files written")
        return 0

    log_path = out_dir / "move-log.csv"
    moved = 0
    renames: list[tuple[str, str]] = []
    with open(log_path, "a", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        if f.tell() == 0:
            w.writerow(["when_utc", "from", "to"])
        for row in by_class.get("MOVE", []):
            src, dst = row["dir"], row["target"]
            if not os.path.isdir(src):
                print(f"[skip] source vanished: {src}")
                continue
            if os.path.exists(dst):
                print(f"[skip] target exists: {dst}")
                continue
            os.makedirs(os.path.dirname(dst), exist_ok=True)
            os.rename(src, dst)
            w.writerow([datetime.now(timezone.utc).isoformat(), src, dst])
            renames.append((src, dst))
            moved += 1
            print(f"[moved] {src} -> {dst}")
    print(f"[apply] {moved} directories moved; undo record: {log_path}")

    # Keep the header index consistent without a rescan: append updated records with the
    # moved paths (and root swapped to canonical). The cache loader keeps the LAST record
    # per path, and stale old-path records simply stop matching anything on disk.
    if renames:
        updated = 0
        with open(args.index, "a", encoding="utf-8") as f:
            for r in records:
                for src, dst in renames:
                    if r["path"].startswith(src + os.sep):
                        r["path"] = dst + r["path"][len(src):]  # in-memory too: summaries below
                        r["root"] = canonical
                        f.write(json.dumps(r) + "\n")
                        updated += 1
                        break
        print(f"[index] {updated} index records re-pointed to their new paths")

    if args.write_summaries:
        written = write_summaries(records, [source, canonical])
        print(f"[summaries] {written} _session-summary.md files written")
    return 0


if __name__ == "__main__":
    sys.exit(main())
