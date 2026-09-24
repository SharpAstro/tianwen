"""The prune `prune_audit.py` measures: drop a raw path only while its payload stays reachable.

Dry run unless given `--apply`. Reads the same `deletion-candidates.csv` the audit reads, and removes
an `archive_path` only when ALL of these hold, re-checked live the instant before the unlink:

  1. the path is outside Astro-Organized (the curated side is never a candidate, whatever a row says);
  2. its `represented_by_rel` copy exists under Astro-Organized;
  3. the two are the SAME FILE (`os.path.samefile`: same volume, same inode), not merely equal content;
  4. `st_nlink > 1`.

(4) is the owner's rule and is what makes the prune unable to remove a last path. (3) is stricter than
the audit and is deliberate: `st_nlink > 1` only says SOME other name exists, and the earlier pass found
6,408 of 6,467 eligible paths whose other name was inside Astro-Pics too, which is not a bake root.
Pruning those leaves the payload reachable only from a tree no bake reads. Since `dataset relink`
(2026-09-23) every curated frame shares its inode with its raw twin, so (3) should now hold for the
rows that matter; a row where it does not is reported as blocked, never removed.

What it frees is next to nothing: unlinking one of several names returns no bytes, and the extent is
released only by the last name, which (4) forbids. The payoff is walk cost and duplicate ingestion.
The byte column below is payload no longer reachable through Astro-Pics, not space reclaimed.

Every removal is appended, flushed, to `_provenance/prune-journal-<utc>.csv` as it happens, so an
interrupted run leaves an exact record. `--limit N` stops after N removals, for a trial run.
"""
import argparse
import csv
import datetime
import os
import sys

PROV = "D:/Astro-Organized/_provenance"
CAND = os.path.join(PROV, "deletion-candidates.csv")
ORG = "D:/Astro-Organized"


def under(path, root):
    p = os.path.normcase(os.path.abspath(path))
    r = os.path.normcase(os.path.abspath(root))
    return p == r or p.startswith(r.rstrip("\\/") + os.sep)


def verdict(p, rep):
    """None when `p` may go, else the reason it may not. Called again right before the unlink."""
    if under(p, ORG):
        return "inside Astro-Organized"
    try:
        st = os.stat(p)
    except FileNotFoundError:
        return "already gone"
    except OSError as e:
        return f"stat failed: {e.strerror}"
    if not os.path.exists(rep):
        return "Organized copy missing"
    try:
        if not os.path.samefile(p, rep):
            return "Organized copy is a different file"
    except OSError as e:
        return f"samefile failed: {e.strerror}"
    if st.st_nlink <= 1:
        return "only link (nlink == 1)"
    return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--apply", action="store_true")
    ap.add_argument("--limit", type=int, default=0, help="stop after N removals (0 = no limit)")
    args = ap.parse_args()

    rows = list(csv.DictReader(open(CAND, encoding="utf-8")))
    print(f"{'APPLYING' if args.apply else 'DRY RUN'}  {len(rows):,} candidate rows from {CAND}\n")

    journal = writer = None
    if args.apply:
        stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%SZ")
        jpath = os.path.join(PROV, f"prune-journal-{stamp}.csv")
        journal = open(jpath, "w", newline="", encoding="utf-8")
        writer = csv.writer(journal)
        writer.writerow(["archive_path", "represented_by", "dev", "ino", "nlink_before", "bytes"])
        journal.flush()
        print(f"journal: {jpath}\n")

    removed = removed_bytes = 0
    blocked = {}
    failed = []
    try:
        for r in rows:
            if args.limit and removed >= args.limit:
                break
            p = r["archive_path"]
            rep = os.path.join(ORG, r["represented_by_rel"].replace("/", os.sep))
            why = verdict(p, rep)
            if why is not None:
                blocked[why] = blocked.get(why, 0) + 1
                continue
            size = int(r["bytes"])
            if not args.apply:
                removed += 1
                removed_bytes += size
                continue
            # The live re-check: the first verdict may be minutes old on a 30k-row pass.
            why = verdict(p, rep)
            if why is not None:
                blocked[why] = blocked.get(why, 0) + 1
                continue
            try:
                st = os.stat(p)
                if st.st_nlink <= 1:
                    blocked["only link (nlink == 1)"] = blocked.get("only link (nlink == 1)", 0) + 1
                    continue
                os.unlink(p)
            except OSError as e:
                failed.append((p, e.strerror))
                continue
            writer.writerow([p, rep, st.st_dev, st.st_ino, st.st_nlink, size])
            journal.flush()
            removed += 1
            removed_bytes += size
            if not os.path.exists(rep):
                # Cannot happen if samefile held; stop rather than find out how.
                print(f"!! {rep} is gone after unlinking {p}; stopping", file=sys.stderr)
                return 2
    finally:
        if journal is not None:
            journal.close()

    verb = "removed" if args.apply else "would remove"
    print(f"{verb}: {removed:,} paths, {removed_bytes / 2**30:.1f} GiB of payload "
          f"(NOT reclaimed: the Organized name still holds it)")
    print(f"blocked: {sum(blocked.values()):,}")
    for k, v in sorted(blocked.items(), key=lambda kv: -kv[1]):
        print(f"    {v:6,d}  {k}")
    if failed:
        print(f"\nunlink FAILED on {len(failed):,} (left in place), e.g.")
        for p, e in failed[:5]:
            print(f"     {p}: {e}")
    if not args.apply:
        print("\n  nothing removed. re-run with --apply (try --limit 50 first).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
