"""Compare PNG outputs across two test-run date folders for visual regressions.

Default scope: scans %TEMP%/TianWen.Lib.Tests/ and %TEMP%/FC.SDK.Raw.Tests/, picks
the two most-recent yyyyMMdd subdirectories under each, and diffs PNG outputs
matched by (test-name folder, filename). See SKILL.md for full usage docs.
"""

from __future__ import annotations

import argparse
import os
import re
import sys
from pathlib import Path

import numpy as np
from PIL import Image

DATE_PATTERN = re.compile(r"^\d{8}$")  # yyyyMMdd


def find_test_roots() -> list[Path]:
    """Default roots: %TEMP%/<TestProject>/ for the test projects we know ship
    yyyyMMdd-stamped output. Returns existing roots only."""
    temp = Path(os.environ.get("TEMP", os.environ.get("TMP", "/tmp")))
    candidates = [
        temp / "TianWen.Lib.Tests",
        temp / "FC.SDK.Raw.Tests",
    ]
    return [p for p in candidates if p.is_dir()]


def latest_two_dates(root: Path) -> tuple[Path | None, Path | None]:
    """Return (newer, older) date subdirs under `root`. Either may be None if
    not enough dated folders exist yet."""
    dates = sorted(
        (p for p in root.iterdir() if p.is_dir() and DATE_PATTERN.match(p.name)),
        key=lambda p: p.name,
        reverse=True,
    )
    if len(dates) == 0:
        return None, None
    if len(dates) == 1:
        return dates[0], None
    return dates[0], dates[1]


def collect_pngs(date_dir: Path) -> dict[tuple[str, str], Path]:
    """Walk `date_dir`/<test-name>/*.png and return a dict keyed by
    (test-name, png-filename) -> absolute path."""
    out: dict[tuple[str, str], Path] = {}
    if date_dir is None or not date_dir.is_dir():
        return out
    for test_dir in date_dir.iterdir():
        if not test_dir.is_dir():
            continue
        for png in test_dir.glob("*.png"):
            out[(test_dir.name, png.name)] = png
    return out


def diff_pair(a_path: Path, b_path: Path, threshold: int) -> dict:
    """Pixel-diff two PNGs. Returns a stats dict with `status` and metrics.
    Assumes both files are valid PNGs; caller catches IO errors."""
    with Image.open(a_path) as ai, Image.open(b_path) as bi:
        # Normalise to RGBA for shape comparison; preserves alpha if present.
        a = np.asarray(ai.convert("RGBA"), dtype=np.int32)
        b = np.asarray(bi.convert("RGBA"), dtype=np.int32)

    bytes_delta = a_path.stat().st_size - b_path.stat().st_size

    if a.shape != b.shape:
        return {
            "status": "RESHAPED",
            "mae": None,
            "max": None,
            "changed_pct": None,
            "bytes_delta": bytes_delta,
            "shape_a": a.shape,
            "shape_b": b.shape,
        }

    diff = np.abs(a - b)
    # Per-pixel max across channels. A pixel is "changed" if any channel
    # exceeds the threshold.
    per_pixel_max = diff.max(axis=-1)
    mae = float(diff.mean())
    max_diff = int(diff.max())
    n_pixels = per_pixel_max.size
    n_changed = int((per_pixel_max > threshold).sum())
    changed_pct = 100.0 * n_changed / n_pixels if n_pixels else 0.0

    status = "unchanged" if n_changed == 0 else "CHANGED"
    return {
        "status": status,
        "mae": mae,
        "max": max_diff,
        "changed_pct": changed_pct,
        "bytes_delta": bytes_delta,
    }


def format_stats(stats: dict) -> str:
    """Render the metrics line for a single PNG comparison."""
    s = stats["status"]
    bd = stats["bytes_delta"]
    bd_str = f"bytes Δ={bd:+d}" if bd else "bytes Δ=0"
    if s == "RESHAPED":
        return f"{s:11s}  ({stats['shape_a']} vs {stats['shape_b']}, {bd_str})"
    if s == "NEW" or s == "REMOVED":
        return f"{s:11s}"
    mae = stats["mae"]
    mx = stats["max"]
    pct = stats["changed_pct"]
    return f"{s:11s}  (MAE={mae:5.2f}, max={mx:3d}, changed={pct:5.2f}%, {bd_str})"


def compare_root(root: Path, newer: Path | None, older: Path | None,
                 threshold: int) -> int:
    """Compare all PNGs between two date dirs under `root`. Returns the count
    of CHANGED+RESHAPED entries (so the caller can `exit n` for CI)."""
    print(f"\n{root.name}:  {newer.name if newer else '?'} vs "
          f"{older.name if older else '?'}")
    if newer is None or older is None:
        print(f"  (only {sum(1 for _ in [newer, older] if _)} date folder(s) "
              f"available — need two to compare)")
        return 0

    a_files = collect_pngs(newer)
    b_files = collect_pngs(older)
    all_keys = sorted(set(a_files) | set(b_files))

    # Group by test-folder name for readable output.
    grouped: dict[str, list[str]] = {}
    for test_name, filename in all_keys:
        grouped.setdefault(test_name, []).append(filename)

    changed_count = 0
    for test_name, filenames in grouped.items():
        print(f"\n  {test_name}/")
        for fn in sorted(filenames):
            key = (test_name, fn)
            in_a = key in a_files
            in_b = key in b_files
            if in_a and in_b:
                try:
                    stats = diff_pair(a_files[key], b_files[key], threshold)
                except Exception as e:  # noqa: BLE001
                    print(f"    {fn:40s} ERROR: {e}")
                    changed_count += 1
                    continue
                if stats["status"] != "unchanged":
                    changed_count += 1
                print(f"    {fn:40s} {format_stats(stats)}")
            elif in_a:
                print(f"    {fn:40s} {format_stats({'status': 'NEW', 'bytes_delta': 0})}")
                changed_count += 1
            else:
                print(f"    {fn:40s} {format_stats({'status': 'REMOVED', 'bytes_delta': 0})}")
                changed_count += 1
    return changed_count


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--root", type=Path, help="Single test-output root to scan "
                   "(default: %%TEMP%%/TianWen.Lib.Tests + %%TEMP%%/FC.SDK.Raw.Tests).")
    p.add_argument("--threshold", type=int, default=1,
                   help="Per-channel pixel-diff floor (default 1 = exact match "
                        "modulo 1 LSB rounding).")
    p.add_argument("--dates", nargs=2, metavar=("NEWER", "OLDER"),
                   help="Override auto-selection of the two yyyyMMdd dirs.")
    args = p.parse_args()

    roots = [args.root] if args.root else find_test_roots()
    if not roots:
        print("No test-output roots found. Run the tests first to generate "
              "%TEMP%/<TestProject>/yyyyMMdd/ output.")
        return 0

    total_changed = 0
    for root in roots:
        if not root.is_dir():
            print(f"\n{root}: not a directory, skipping.")
            continue
        if args.dates:
            newer = root / args.dates[0]
            older = root / args.dates[1]
            if not newer.is_dir():
                print(f"\n{root}: --dates newer '{args.dates[0]}' not found.")
                continue
            if not older.is_dir():
                print(f"\n{root}: --dates older '{args.dates[1]}' not found.")
                continue
        else:
            newer, older = latest_two_dates(root)
        total_changed += compare_root(root, newer, older, args.threshold)

    print(f"\n--- {total_changed} changed entries across all roots ---")
    return 0  # CHANGED is informational, not an error exit


if __name__ == "__main__":
    sys.exit(main())
