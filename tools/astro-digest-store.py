#!/usr/bin/env python
"""Content digests for the astro archive, with paths, into a resumable store (READ-ONLY on data).

Companion to astro-archive-dedup.py, which identifies files by header fields and by name+size. That
is not good enough: PixInsight master names collide (masterBias_BIN-1_3008x3008.xisf exists at three
different sizes), and a header amended in place makes a whole-file hash call a corrected file a new
file. A digest over the DATA SECTION is exact, global, and indifferent to header edits.

Nothing here writes to the archive. Stamping the digest INTO each file's header is a separate, later
pass -- see D:/Astro-Organized/_provenance/digest-and-header-merge.md -- and needs an in-place FITS
card writer, because an atomic-replace write would unwind 270.6 GB of hard-link dedup.

THREE THINGS THIS GETS RIGHT, each of which cost something to learn:

1. FITS files are digested over the DATA SECTION ONLY, matching TianWen's
   StackManifest.DigestData / ContentDigest (xxh128:HEX). Verified byte-for-byte against the .NET
   implementation on 31 files spanning mono, colour, .fz and masters. Data-only is not a preference:
   a whole-file digest would be invalidated by the act of stamping it into the header.

2. Work is done ONCE PER INODE, not once per path. This archive is heavily hard-linked -- 18,500 of
   21,882 duplicate groups are already a single file under several names, 380.9 GB worth. Hashing
   per path would re-read all of it to learn nothing. (dev, ino) is the key; every extra name is a
   dictionary hit.

3. It is resumable and append-only. A path whose size and mtime are unchanged is not re-read, so an
   interrupted run resumes cheaply and a later run over a mostly-unchanged archive is fast.

Usage:
  python tools/astro-digest-store.py --root "D:/Astro-Pics" --root "C:/temp/astro" --out "D:/Astro-Reports"
  ... --fits-only          # skip .xisf/.tif/.png etc, digest only FITS
  ... --limit 500          # smoke test
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import time

try:
    import xxhash
except ImportError:
    print("needs xxhash:  python -m pip install xxhash", file=sys.stderr)
    raise SystemExit(2)

BLOCK = 2880
ALGO = "xxh128"
FITS_EXTS = {".fits", ".fit", ".fts", ".fz"}
# Everything else worth identifying. Deliberately a list rather than "all files": the archive holds
# logs, .xnml sidecars and thumbnails whose identity nobody will ever ask about, and reading them
# costs the same per byte as reading a frame.
OTHER_EXTS = {".xisf", ".tif", ".tiff", ".png", ".jpg", ".jpeg", ".exr", ".ser",
              ".cr2", ".cr3", ".avi", ".nef", ".arw", ".dng"}
G = 1 << 30


def digest_data(path, chunk=1 << 20):
    """xxh128 over the first HDU that HAS a data unit. '' when unreadable or dataless.

    Mirrors StackManifest.DigestData: walk 2880-byte blocks and 80-byte cards accumulating BITPIX
    and the NAXISn product until END; an HDU with NAXIS <= 0 has no data unit so the walk continues.
    That last clause is what makes a tile-compressed .fz work -- its primary is always empty, and
    stopping there would digest zero bytes and make every .fz identical.
    """
    try:
        with open(path, "rb") as f:
            while True:
                naxis, bitpix, npix = -1, 0, 1
                saw_end = False
                while not saw_end:
                    block = f.read(BLOCK)
                    if len(block) < BLOCK:
                        return ""
                    for i in range(0, BLOCK, 80):
                        card = block[i:i + 80].decode("ascii", "replace")
                        key = card[:8].strip()
                        if key == "END":
                            saw_end = True
                            break
                        if len(card) < 9 or card[8] != "=":
                            continue
                        val = card[10:30].strip()
                        try:
                            if key == "BITPIX":
                                bitpix = int(val)
                            elif key == "NAXIS":
                                naxis = int(val)
                            elif key.startswith("NAXIS"):
                                npix *= int(val)
                        except ValueError:
                            pass
                if naxis <= 0:
                    continue
                h = xxhash.xxh128()
                remaining = abs(bitpix) // 8 * npix
                while remaining > 0:
                    got = f.read(min(chunk, remaining))
                    if not got:
                        break
                    h.update(got)
                    remaining -= len(got)
                return ALGO + ":" + h.hexdigest().upper()
    except OSError:
        return ""


def digest_file(path, chunk=1 << 20):
    """Whole-file xxh128, for anything that is not FITS."""
    try:
        h = xxhash.xxh128()
        with open(path, "rb") as f:
            while True:
                b = f.read(chunk)
                if not b:
                    break
                h.update(b)
        return ALGO + ":" + h.hexdigest().upper()
    except OSError:
        return ""


def load_store(path):
    """(by_path, by_inode). Append-only, so later records for a path win."""
    by_path, by_inode = {}, {}
    if not os.path.exists(path):
        return by_path, by_inode
    with open(path, "r", encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            try:
                r = json.loads(line)
            except json.JSONDecodeError:
                continue
            if "path" in r and r.get("digest"):
                by_path[os.path.normcase(r["path"])] = r
                if r.get("ino"):
                    by_inode[(r.get("dev"), r["ino"])] = r["digest"]
    return by_path, by_inode


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", action="append", required=True, help="archive root (repeatable)")
    ap.add_argument("--out", required=True, help="directory holding digests.jsonl")
    ap.add_argument("--limit", type=int, default=None)
    ap.add_argument("--fits-only", action="store_true")
    args = ap.parse_args()

    out_dir = args.out
    os.makedirs(out_dir, exist_ok=True)
    store_path = os.path.join(out_dir, "digests.jsonl")
    by_path, by_inode = load_store(store_path)
    print(f"[store] {len(by_path):,} existing records, {len(by_inode):,} known inodes", flush=True)

    wanted = set(FITS_EXTS) if args.fits_only else (FITS_EXTS | OTHER_EXTS)
    files = []
    for root in args.root:
        n0 = len(files)
        for dirpath, _, names in os.walk(root):
            for nm in names:
                if os.path.splitext(nm)[1].lower() in wanted:
                    files.append(os.path.join(dirpath, nm))
        print(f"[scan] {len(files) - n0:,} under {root}", flush=True)
    if args.limit:
        files = files[:args.limit]
    print(f"[scan] {len(files):,} files to consider\n", flush=True)

    hashed = reused_path = reused_inode = failed = 0
    bytes_read = 0
    t0 = time.time()
    last = t0

    with open(store_path, "a", encoding="utf-8") as out:
        for i, path in enumerate(files, 1):
            try:
                st = os.stat(path)
            except OSError:
                failed += 1
                continue

            key = os.path.normcase(path)
            prev = by_path.get(key)
            if prev and prev.get("size") == st.st_size and abs(prev.get("mtime", 0) - st.st_mtime) < 2:
                reused_path += 1
                continue

            ino_key = (st.st_dev, st.st_ino)
            # A hard link to something already hashed: the bytes are literally the same bytes, so the
            # digest is known without reading them. This is where the run's time is won.
            known = by_inode.get(ino_key) if st.st_ino else None
            if known:
                digest, kind = known, "hardlink"
                reused_inode += 1
            else:
                ext = os.path.splitext(path)[1].lower()
                if ext in FITS_EXTS:
                    digest, kind = digest_data(path), "fits-data"
                else:
                    digest, kind = digest_file(path), "whole-file"
                if not digest:
                    failed += 1
                    continue
                hashed += 1
                bytes_read += st.st_size
                if st.st_ino:
                    by_inode[ino_key] = digest

            rec = {
                "path": path.replace("\\", "/"),
                "size": st.st_size,
                "mtime": st.st_mtime,
                "dev": st.st_dev,
                "ino": st.st_ino,
                "nlink": getattr(st, "st_nlink", 1),
                "digest": digest,
                "kind": kind,
            }
            out.write(json.dumps(rec) + "\n")
            by_path[key] = rec

            now = time.time()
            if i % 500 == 0 or now - last > 60:
                out.flush()
                last = now
                el = max(now - t0, 1e-6)
                rate = bytes_read / el / (1 << 20)
                print(f"  {i:,}/{len(files):,}  hashed {hashed:,} ({bytes_read/G:.1f} GB, "
                      f"{rate:.0f} MB/s)  hardlink-reuse {reused_inode:,}  cached {reused_path:,}",
                      flush=True)

    el = time.time() - t0
    print(f"\n[done] {len(files):,} considered in {el/60:.1f} min")
    print(f"  hashed fresh        {hashed:,}  ({bytes_read/G:.2f} GB read)")
    print(f"  reused via hardlink {reused_inode:,}   (bytes never re-read)")
    print(f"  already in store    {reused_path:,}")
    print(f"  failed / skipped    {failed:,}")
    print(f"  store: {store_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
