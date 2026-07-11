#!/usr/bin/env python
"""Astro archive dedup + organization report (READ-ONLY).

Step 0 of docs/plans/ai-denoise-deconv.md: reconcile D:\\BobbyBox-Temp against
D:\\Astro-Pics before the dataset builder exists. Identity is FITS-header-based
(camera + DATE-OBS + exposure + dimensions), never filename/path-based, because
the same sub can be filed twice under different names/layouts. Nothing on disk
is modified; all output goes to --out.

Outputs (in --out):
  fits-index.jsonl          per-file header index (cache; resumable by size+mtime)
  dup-files.csv             exact-duplicate groups (same identity key, hash-confirmed)
  nights-rollup.csv         per (camera, night): light counts per root, dup/unique split
  calibration-coverage.csv  per light (camera, exptime, gain, bin): matching darks found where
  summary.txt               human summary (also printed to console)

Usage:
  python tools/astro-archive-dedup.py --root "D:\\Astro-Pics" --root "D:\\BobbyBox-Temp" --out "D:\\Astro-Reports"
  ... --limit 500          # smoke test on the first N FITS files found
"""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
import os
import re
import sys
from collections import defaultdict
from datetime import datetime, timedelta
from pathlib import Path

FITS_EXTS = {".fits", ".fit", ".fts"}
BLOCK = 2880
MAX_HEADER_BLOCKS = 12  # 12 * 36 = 432 cards, plenty for capture software headers

# Path fragments that mark processed/non-raw content (report tag, not an exclusion --
# dupes among processed files are still worth knowing about).
PROCESSED_DIR_RE = re.compile(
    r"(?i)(?:^|[\\/])(proc[^\\/]*|reproc|pixinsight|pi_swap|autosave|master[^\\/]*|output|process(?:ed|ing)?)(?:[\\/]|$)"
)


def parse_fits_header(path: str) -> dict | None:
    """Read primary-HDU header cards until END. Returns {} keys uppercased, or None on I/O error."""
    try:
        with open(path, "rb") as f:
            raw = f.read(BLOCK * MAX_HEADER_BLOCKS)
    except OSError:
        return None
    if len(raw) < BLOCK or not raw.startswith(b"SIMPLE"):
        return None
    header: dict[str, str] = {}
    for off in range(0, len(raw) - 79, 80):
        card = raw[off : off + 80].decode("ascii", "replace")
        key = card[:8].strip()
        if key == "END":
            break
        if not key or card[8:10] != "= ":
            continue
        value = card[10:]
        if value.lstrip().startswith("'"):  # quoted string; comment slash may appear later
            m = re.match(r"\s*'((?:[^']|'')*)'", value)
            value = m.group(1).replace("''", "'").strip() if m else value.strip()
        else:
            value = value.split("/", 1)[0].strip()
        header[key.upper()] = value
    return header


def ffloat(v: str | None) -> float | None:
    if v is None:
        return None
    try:
        return float(v)
    except ValueError:
        return None


def night_of(date_obs: str | None) -> str:
    """Bucket DATE-OBS into an observing night (local-ish: shift -12h so an
    evening-to-morning session lands on one date). Empty string when unknown."""
    if not date_obs:
        return ""
    try:
        dt = datetime.fromisoformat(date_obs.rstrip("Z").split(".")[0])
    except ValueError:
        return ""
    return (dt - timedelta(hours=12)).date().isoformat()


def frame_type(header: dict, path: str) -> str:
    t = (header.get("IMAGETYP") or header.get("FRAMETYP") or "").upper()
    for kind in ("LIGHT", "DARKFLAT", "FLAT", "DARK", "BIAS"):
        if kind in t:
            # DARKFLAT before FLAT/DARK so 'DarkFlat' doesn't misclassify.
            return kind
    # Fall back to folder names for header-less/odd files.
    up = path.upper()
    for kind in ("DARKFLAT", "LIGHT", "FLAT", "DARK", "BIAS"):
        if f"\\{kind}" in up or f"/{kind}" in up:
            return kind
    return "UNKNOWN"


def sha_first_mib(path: str) -> str:
    h = hashlib.sha256()
    try:
        with open(path, "rb") as f:
            h.update(f.read(1024 * 1024))
    except OSError:
        return ""
    return h.hexdigest()


def scan_files(roots: list[str], limit: int | None) -> list[tuple[str, str, int, float]]:
    """Yield (root, path, size, mtime) for every FITS file under the roots."""
    out = []
    for root in roots:
        for dirpath, dirnames, filenames in os.walk(root):
            for name in filenames:
                if os.path.splitext(name)[1].lower() in FITS_EXTS:
                    full = os.path.join(dirpath, name)
                    try:
                        st = os.stat(full)
                    except OSError:
                        continue
                    out.append((root, full, st.st_size, st.st_mtime))
                    if limit and len(out) >= limit:
                        return out
    return out


def load_cache(cache_path: Path) -> dict[str, dict]:
    cache: dict[str, dict] = {}
    if cache_path.exists():
        with open(cache_path, "r", encoding="utf-8") as f:
            for line in f:
                try:
                    rec = json.loads(line)
                    cache[rec["path"]] = rec
                except (json.JSONDecodeError, KeyError):
                    continue
    return cache


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--root", action="append", required=True, help="archive root (repeatable)")
    ap.add_argument("--out", required=True, help="report/cache output directory (created if missing)")
    ap.add_argument("--limit", type=int, default=None, help="smoke test: stop after N files")
    ap.add_argument("--rehash", action="store_true", help="ignore the index cache and re-read all headers")
    args = ap.parse_args()

    out_dir = Path(args.out)
    out_dir.mkdir(parents=True, exist_ok=True)
    cache_path = out_dir / "fits-index.jsonl"
    cache = {} if args.rehash else load_cache(cache_path)

    print(f"[scan] enumerating FITS under: {', '.join(args.root)}")
    files = scan_files(args.root, args.limit)
    print(f"[scan] {len(files)} FITS files found; {len(cache)} cached header records")

    records: list[dict] = []
    fresh = 0
    with open(cache_path, "a", encoding="utf-8") as cache_out:
        for i, (root, path, size, mtime) in enumerate(files):
            cached = cache.get(path)
            if cached and cached.get("size") == size and abs(cached.get("mtime", 0) - mtime) < 2:
                records.append(cached)
            else:
                header = parse_fits_header(path) or {}
                rec = {
                    "path": path,
                    "root": root,
                    "size": size,
                    "mtime": mtime,
                    "camera": header.get("INSTRUME", ""),
                    "date_obs": header.get("DATE-OBS", ""),
                    "exptime": ffloat(header.get("EXPTIME") or header.get("EXPOSURE")),
                    "gain": ffloat(header.get("GAIN")),
                    "binning": header.get("XBINNING", ""),
                    "naxis1": header.get("NAXIS1", ""),
                    "naxis2": header.get("NAXIS2", ""),
                    "filter": header.get("FILTER", ""),
                    "bayer": header.get("BAYERPAT", ""),
                    "set_temp": ffloat(header.get("SET-TEMP")),
                    "ccd_temp": ffloat(header.get("CCD-TEMP")),
                    "type": frame_type(header, path),
                    "swcreate": header.get("SWCREATE", ""),
                    "stack_n": header.get("STACK_N", ""),
                    "processed_path": bool(PROCESSED_DIR_RE.search(path)),
                }
                cache_out.write(json.dumps(rec) + "\n")
                records.append(rec)
                fresh += 1
                if fresh % 1000 == 0:
                    cache_out.flush()
                    print(f"[index] {fresh} headers read ({i + 1}/{len(files)} files seen)")
    print(f"[index] done: {fresh} headers read fresh, {len(records) - fresh} from cache")

    # ---- duplicate detection: identity = camera + DATE-OBS + exptime + dims ----
    by_key: dict[tuple, list[dict]] = defaultdict(list)
    for r in records:
        if r["date_obs"] and r["camera"]:
            key = (r["camera"], r["date_obs"], r["exptime"], r["naxis1"], r["naxis2"])
        else:
            key = ("<no-header>", r["size"], os.path.basename(r["path"]).lower(), "", "")
        by_key[key].append(r)

    dup_rows: list[dict] = []
    n_dup_groups = n_dup_files = dup_bytes = 0
    n_cross_root_groups = 0
    for key, group in by_key.items():
        if len(group) < 2:
            continue
        hashes = {r["path"]: sha_first_mib(r["path"]) for r in group}
        by_hash: dict[str, list[dict]] = defaultdict(list)
        for r in group:
            by_hash[hashes[r["path"]] + f":{r['size']}"].append(r)
        for hsh, same in by_hash.items():
            if len(same) < 2:
                continue
            n_dup_groups += 1
            n_dup_files += len(same) - 1
            dup_bytes += sum(r["size"] for r in same[1:])
            roots_in_group = {r["root"] for r in same}
            if len(roots_in_group) > 1:
                n_cross_root_groups += 1
            for r in same:
                dup_rows.append({
                    "group": n_dup_groups,
                    "camera": key[0],
                    "date_obs": key[1],
                    "type": r["type"],
                    "size": r["size"],
                    "cross_root": len(roots_in_group) > 1,
                    "path": r["path"],
                })

    with open(out_dir / "dup-files.csv", "w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=["group", "camera", "date_obs", "type", "size", "cross_root", "path"])
        w.writeheader()
        w.writerows(dup_rows)

    # ---- per-night rollup (lights only, raw paths only) ----
    nights: dict[tuple, dict] = defaultdict(lambda: defaultdict(int))
    dup_paths = {row["path"] for row in dup_rows}
    for r in records:
        if r["type"] != "LIGHT" or r["processed_path"] or r["stack_n"]:
            continue
        key = (r["camera"], night_of(r["date_obs"]))
        nights[key][f"lights@{Path(r['root']).name}"] += 1
        nights[key]["dup" if r["path"] in dup_paths else "unique"] += 1

    with open(out_dir / "nights-rollup.csv", "w", newline="", encoding="utf-8") as f:
        root_cols = [f"lights@{Path(r).name}" for r in args.root]
        w = csv.writer(f)
        w.writerow(["camera", "night", *root_cols, "dup", "unique"])
        for (camera, night), counts in sorted(nights.items()):
            w.writerow([camera, night, *(counts.get(c, 0) for c in root_cols),
                        counts.get("dup", 0), counts.get("unique", 0)])

    # ---- calibration coverage: darks/bias are shared across sessions, resolve by header ----
    dark_sets: dict[tuple, dict] = defaultdict(lambda: {"count": 0, "roots": set(), "nights": set()})
    for r in records:
        if r["type"] in ("DARK", "BIAS") and not r["processed_path"]:
            k = (r["type"], r["camera"], r["exptime"] if r["type"] == "DARK" else None, r["gain"], r["binning"])
            dark_sets[k]["count"] += 1
            dark_sets[k]["roots"].add(Path(r["root"]).name)
            dark_sets[k]["nights"].add(night_of(r["date_obs"]))

    cov_rows = []
    light_groups: dict[tuple, int] = defaultdict(int)
    for r in records:
        if r["type"] == "LIGHT" and not r["processed_path"] and not r["stack_n"]:
            light_groups[(r["camera"], r["exptime"], r["gain"], r["binning"], night_of(r["date_obs"]))] += 1
    # exptime/gain are None for headerless lights; sort None-last instead of crashing the <.
    def none_last(v):
        return (v is None, v if v is not None else 0.0)

    for (camera, exptime, gain, binning, night), count in sorted(
            light_groups.items(),
            key=lambda kv: (kv[0][0], kv[0][4], none_last(kv[0][1]), none_last(kv[0][2]), str(kv[0][3]))):
        dk = dark_sets.get(("DARK", camera, exptime, gain, binning))
        bk = dark_sets.get(("BIAS", camera, None, gain, binning))
        cov_rows.append({
            "camera": camera, "night": night, "exptime": exptime, "gain": gain, "binning": binning,
            "lights": count,
            "darks_found": dk["count"] if dk else 0,
            "darks_roots": ";".join(sorted(dk["roots"])) if dk else "",
            "bias_found": bk["count"] if bk else 0,
        })
    with open(out_dir / "calibration-coverage.csv", "w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=["camera", "night", "exptime", "gain", "binning",
                                          "lights", "darks_found", "darks_roots", "bias_found"])
        w.writeheader()
        w.writerows(cov_rows)

    uncovered = [c for c in cov_rows if c["lights"] >= 10 and c["darks_found"] == 0]
    summary = [
        f"files indexed:        {len(records)}",
        f"duplicate groups:     {n_dup_groups} ({n_dup_files} redundant files, {dup_bytes / 1e9:.1f} GB reclaimable)",
        f"  cross-root groups:  {n_cross_root_groups} (BobbyBox <-> Astro-Pics)",
        f"light (camera,night) groups: {len(light_groups)}",
        f"  with NO matching darks anywhere (>=10 lights): {len(uncovered)}",
        "reports: dup-files.csv, nights-rollup.csv, calibration-coverage.csv",
    ]
    text = "\n".join(summary)
    (out_dir / "summary.txt").write_text(text, encoding="utf-8")
    print("\n[summary]\n" + text)
    return 0


if __name__ == "__main__":
    sys.exit(main())
