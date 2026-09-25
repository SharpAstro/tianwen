"""The one way to read D:/Astro-Reports/digests.jsonl: each path's LATEST record, gone paths left out.

The store (astro-digest-store.py) is append-only. A path is recorded again whenever its size or mtime
moves, so its last record is its current one; and a path a full walk finds gone gets a TOMBSTONE,
{"path": ..., "gone": true}, rather than having its history rewritten. Keeping the last record per
path is therefore only half a reader. Without the tombstone half, a file that was withdrawn or deleted
keeps answering "filed": on 2026-09-25, 650 records of the withdrawn QHY183M frames made 1,300 raw
names of the Eta Car SII 2024-03 nights read as filed to every reader here. gap_by_digest.py in the
archive's _provenance had learnt this on 2026-09-20 and checked each path on disk; the readers in this
folder had not, which is why the rule now lives in one place.

Paths are compared as Windows compares them (os.path.normcase), so a record and its tombstone match
however the walk spelled the path, and are returned with forward slashes, the form callers key on.
"""
import json
import os

LEDGER = "D:/Astro-Reports/digests.jsonl"


def records(path=LEDGER):
    """Every record in file order. A line that does not parse is skipped: an append that a killed run
    cut short leaves one partial line, and the records before it are still good."""
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            try:
                r = json.loads(line)
            except json.JSONDecodeError:
                continue
            if "path" in r:
                yield r


def latest(path=LEDGER):
    """{normcase path: last record}, tombstones included, for a caller that must know what went."""
    last = {}
    for r in records(path):
        last[os.path.normcase(r["path"])] = r
    return last


def load(path=LEDGER):
    """{forward-slash path: record} for every path whose last record is not a tombstone."""
    return {r["path"].replace("\\", "/"): r for r in latest(path).values() if not r.get("gone")}
