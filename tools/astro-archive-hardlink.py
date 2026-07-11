#!/usr/bin/env python
"""Astro archive hardlink dedup: collapse verified duplicate FITS copies into NTFS hardlinks.

Consumes fits-index.jsonl (astro-archive-dedup.py). Duplicate groups share header identity
(camera + DATE-OBS + exposure + dims); within each group one KEEPER is chosen (prefer the
canonical root, then non-processed paths, then the shortest path) and every other physically
distinct copy is replaced by a hardlink to it -- both directory layouts keep working, the
redundant blocks are freed, and nothing is deleted (content is identical by construction).

Safety model:
  - Copies are verified BEFORE linking. Default: SHA-256 of first + last MiB + exact size --
    astro frames are noise-dominated (high entropy), so same sub-second DATE-OBS + same size +
    same head/tail bytes is decisive, and the head MiB contains the whole FITS header (catches
    edited-header twins, e.g. WCS written back in place). --full-verify hashes entire files
    instead (mid-file bit-rot audit; ~10x the reads). Mismatches are flagged to mismatches.csv
    and never linked either way.
  - Already-hardlinked pairs (same st_ino) are detected via os.stat and skipped.
  - Links are created next to the dup then atomically swapped in with os.replace.
  - Same-volume only (hardlinks cannot cross volumes; guarded by st_dev).
  - Dry-run by default: writes hardlink-plan.csv, touches nothing.

Usage:
  python tools/astro-archive-hardlink.py --index "D:\\Astro-Reports\\fits-index.jsonl" ^
      --canonical "D:\\Astro-Pics" --out "D:\\Astro-Reports" [--apply] [--full-verify]
"""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
import os
import sys
from collections import defaultdict
from datetime import datetime, timezone
from pathlib import Path

MIB = 1024 * 1024


def sha256_of(path: str, quick: bool) -> str | None:
    h = hashlib.sha256()
    try:
        size = os.path.getsize(path)
        with open(path, "rb") as f:
            if quick and size > 2 * MIB:
                h.update(f.read(MIB))
                f.seek(size - MIB)
                h.update(f.read(MIB))
            else:
                for chunk in iter(lambda: f.read(8 * MIB), b""):
                    h.update(chunk)
    except OSError:
        return None
    return h.hexdigest()


def identity(rec: dict) -> tuple | None:
    if rec["date_obs"] and rec["camera"]:
        return (rec["camera"], rec["date_obs"], rec["exptime"], rec["naxis1"], rec["naxis2"])
    return None  # headerless files: content-only identity is too weak to auto-link


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--index", required=True, help="fits-index.jsonl from astro-archive-dedup.py")
    ap.add_argument("--canonical", required=True, help="root whose copies are kept as the primary")
    ap.add_argument("--out", required=True, help="directory for plan/log/mismatch CSVs")
    ap.add_argument("--apply", action="store_true", help="create the hardlinks (default: dry-run)")
    ap.add_argument("--full-verify", action="store_true",
                    help="hash entire files instead of first+last MiB + size (~10x the reads)")
    args = ap.parse_args()
    quick = not args.full_verify

    out_dir = Path(args.out)
    out_dir.mkdir(parents=True, exist_ok=True)
    canonical = os.path.normpath(args.canonical)

    # Last record per path wins (the organize step appends re-pointed records).
    by_path: dict[str, dict] = {}
    with open(args.index, "r", encoding="utf-8") as f:
        for line in f:
            try:
                rec = json.loads(line)
                by_path[rec["path"]] = rec
            except (json.JSONDecodeError, KeyError):
                continue

    groups: dict[tuple, list[dict]] = defaultdict(list)
    for rec in by_path.values():
        key = identity(rec)
        if key is not None:
            groups[key].append(rec)

    def keeper_rank(rec: dict, st: os.stat_result) -> tuple:
        under_canonical = os.path.normpath(rec["path"]).startswith(canonical + os.sep)
        return (0 if under_canonical else 1, 1 if rec["processed_path"] else 0, len(rec["path"]))

    plan: list[dict] = []          # pending link actions
    mismatches: list[dict] = []
    already_linked = 0
    stat_cache: dict[str, os.stat_result] = {}
    for key, recs in groups.items():
        if len(recs) < 2:
            continue
        live: list[tuple[dict, os.stat_result]] = []
        for rec in recs:
            try:
                st = os.stat(rec["path"])
            except OSError:
                continue  # stale index path
            stat_cache[rec["path"]] = st
            live.append((rec, st))
        if len(live) < 2:
            continue
        # Size differences within a header-identity group are treated as distinct files.
        by_size: dict[int, list[tuple[dict, os.stat_result]]] = defaultdict(list)
        for rec, st in live:
            by_size[st.st_size].append((rec, st))
        for size, members in by_size.items():
            if len(members) < 2:
                continue
            members.sort(key=lambda m: keeper_rank(m[0], m[1]))
            keeper, kst = members[0]
            for rec, st in members[1:]:
                if st.st_dev != kst.st_dev:
                    continue  # cannot hardlink across volumes
                if st.st_ino == kst.st_ino:
                    already_linked += 1
                    continue
                plan.append({"keeper": keeper["path"], "dup": rec["path"], "size": size,
                             "dup_nlink": st.st_nlink, "camera": key[0], "date_obs": key[1]})

    plan_path = out_dir / "hardlink-plan.csv"
    with open(plan_path, "w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=["keeper", "dup", "size", "dup_nlink", "camera", "date_obs"])
        w.writeheader()
        w.writerows(plan)
    total_bytes = sum(p["size"] for p in plan)
    print(f"[plan] {len(plan)} link candidates, {total_bytes / 1e9:.1f} GB reclaimable, "
          f"{already_linked} already hardlinked; plan: {plan_path}")

    if not args.apply:
        print("[dry-run] nothing linked; re-run with --apply "
              f"({'FULL' if args.full_verify else 'quick'}-content verification before each link)")
        return 0

    log_path = out_dir / "hardlink-log.csv"
    mm_path = out_dir / "mismatches.csv"
    linked = freed = verified_bytes = 0
    keeper_hashes: dict[str, str | None] = {}
    with open(log_path, "a", newline="", encoding="utf-8") as lf, \
         open(mm_path, "a", newline="", encoding="utf-8") as mf:
        lw, mw = csv.writer(lf), csv.writer(mf)
        if lf.tell() == 0:
            lw.writerow(["when_utc", "dup_replaced", "keeper", "size"])
        if mf.tell() == 0:
            mw.writerow(["keeper", "dup", "size", "note"])
        for i, p in enumerate(plan):
            keeper, dup, size = p["keeper"], p["dup"], p["size"]
            if keeper not in keeper_hashes:
                keeper_hashes[keeper] = sha256_of(keeper, quick)
            kh = keeper_hashes[keeper]
            dh = sha256_of(dup, quick)
            verified_bytes += size * 2
            if kh is None or dh is None:
                mw.writerow([keeper, dup, size, "unreadable"])
                mismatches.append(p)
                continue
            if kh != dh:
                mw.writerow([keeper, dup, size, "content differs (bit-rot or false dup) - NOT linked"])
                mismatches.append(p)
                continue
            tmp = dup + ".twlink.tmp"
            try:
                if os.path.exists(tmp):
                    os.remove(tmp)
                os.link(keeper, tmp)
                os.replace(tmp, dup)
            except OSError as e:
                mw.writerow([keeper, dup, size, f"link failed: {e}"])
                mismatches.append(p)
                continue
            lw.writerow([datetime.now(timezone.utc).isoformat(), dup, keeper, size])
            linked += 1
            if p["dup_nlink"] == 1:
                freed += size
            if (i + 1) % 500 == 0:
                lf.flush()
                print(f"[link] {i + 1}/{len(plan)} processed, {freed / 1e9:.1f} GB freed, "
                      f"{verified_bytes / 1e9:.0f} GB verified")
    print(f"[apply] {linked} hardlinks created, {freed / 1e9:.1f} GB freed, "
          f"{len(mismatches)} skipped (see {mm_path}); log: {log_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
