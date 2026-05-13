"""Prune old yyyyMMdd test-output date folders, keeping the N most recent.

Default scope: scans %TEMP%/TianWen.Lib.Tests/ and %TEMP%/FC.SDK.Raw.Tests/.
See SKILL.md for full usage docs.
"""

from __future__ import annotations

import argparse
import os
import re
import shutil
import sys
from pathlib import Path

DATE_PATTERN = re.compile(r"^\d{8}$")  # yyyyMMdd


def find_test_roots() -> list[Path]:
    """Default roots: the test-output dirs we know ship yyyyMMdd folders."""
    temp = Path(os.environ.get("TEMP", os.environ.get("TMP", "/tmp")))
    candidates = [
        temp / "TianWen.Lib.Tests",
        temp / "FC.SDK.Raw.Tests",
    ]
    return [p for p in candidates if p.is_dir()]


def dir_size_bytes(path: Path) -> int:
    """Recursive size of a directory in bytes."""
    total = 0
    for entry in path.rglob("*"):
        if entry.is_file():
            try:
                total += entry.stat().st_size
            except OSError:
                # File might be locked or vanish mid-walk; skip silently.
                pass
    return total


def fmt_bytes(n: int) -> str:
    """Format byte count as KB / MB / GB."""
    for unit in ["B", "KB", "MB", "GB"]:
        if n < 1024:
            return f"{n:.1f} {unit}"
        n /= 1024.0
    return f"{n:.1f} TB"


def prune_root(root: Path, keep: int, dry_run: bool) -> tuple[int, int]:
    """Prune `root` down to `keep` most-recent yyyyMMdd dirs. Returns
    (deleted_count, freed_bytes)."""
    dates = sorted(
        (p for p in root.iterdir() if p.is_dir() and DATE_PATTERN.match(p.name)),
        key=lambda p: p.name,
        reverse=True,
    )
    if not dates:
        print(f"\n{root.name}: no yyyyMMdd folders present.")
        return 0, 0

    keep_dirs = dates[:keep]
    delete_dirs = dates[keep:]
    action_word = "DRY-RUN: would prune" if dry_run else "pruning"
    print(f"\n{root.name}: keeping {len(keep_dirs)} of {len(dates)} ({action_word} {len(delete_dirs)})")

    for d in keep_dirs:
        print(f"  KEEP:   {d.name}  ({fmt_bytes(dir_size_bytes(d))})")

    deleted_count = 0
    freed_bytes = 0
    for d in delete_dirs:
        size = dir_size_bytes(d)
        if dry_run:
            print(f"  WOULD-DELETE: {d.name}  ({fmt_bytes(size)})")
            freed_bytes += size
            deleted_count += 1
            continue
        try:
            shutil.rmtree(d)
            print(f"  DELETED: {d.name}  ({fmt_bytes(size)})")
            freed_bytes += size
            deleted_count += 1
        except OSError as e:
            print(f"  FAILED:  {d.name}  ({e})")

    return deleted_count, freed_bytes


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--keep", type=int, default=3,
                   help="Number of most-recent yyyyMMdd folders to keep per root (default 3).")
    p.add_argument("--root", type=Path, help="Single test-output root to scan "
                   "(default: %%TEMP%%/TianWen.Lib.Tests + %%TEMP%%/FC.SDK.Raw.Tests).")
    p.add_argument("--dry-run", action="store_true",
                   help="Report what would be deleted without touching the filesystem.")
    args = p.parse_args()

    if args.keep < 1:
        print("--keep must be >= 1 (always preserve at least the latest run).",
              file=sys.stderr)
        return 2

    roots = [args.root] if args.root else find_test_roots()
    if not roots:
        print("No test-output roots found. Nothing to prune.")
        return 0

    total_deleted = 0
    total_freed = 0
    for root in roots:
        if not root.is_dir():
            print(f"\n{root}: not a directory, skipping.")
            continue
        deleted, freed = prune_root(root, args.keep, args.dry_run)
        total_deleted += deleted
        total_freed += freed

    label = "would free" if args.dry_run else "freed"
    print(f"\n--- Total {label}: {fmt_bytes(total_freed)} across "
          f"{total_deleted} directories ---")
    return 0


if __name__ == "__main__":
    sys.exit(main())
