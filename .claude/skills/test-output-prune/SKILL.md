---
name: test-output-prune
description: Delete old `yyyyMMdd` test-output date folders under `%TEMP%/TianWen.Lib.Tests/` and `%TEMP%/FC.SDK.Raw.Tests/`, keeping only the N most recent (default 3). These folders accumulate quickly because the test fixtures dump TIFFs/PNGs on every run; pruning keeps disk usage in check. Use when the user asks to clean up, prune, delete old, free disk, or trim test outputs.
---

Usage:

```
/test-output-prune
/test-output-prune --keep 5
/test-output-prune --dry-run
/test-output-prune --root <path>
```

Deletes every `yyyyMMdd` subdirectory under each test-output root except the
most-recent `--keep N` (default 3). Pass `--dry-run` to print what would be
deleted without touching the filesystem. Pass `--root <path>` to scope to a
single test-output root.

Output is a per-root summary:

```
TianWen.Lib.Tests:  keeping 3 of 8
  KEEP: 20260513 (12.4 MB)
  KEEP: 20260512 (10.1 MB)
  KEEP: 20260511 (5.6 MB)
  DELETE: 20260510 (4.2 MB)
  DELETE: 20260509 (3.8 MB)
  DELETE: 20260508 (2.1 MB)
  ...

FC.SDK.Raw.Tests:  keeping 3 of 4
  KEEP: 20260513 (53.4 MB)
  ...
  DELETE: 20260510 (27.6 MB)

Total freed: 47.8 MB across 5 directories
```

The skill is naturally idempotent — running it twice in a row is a no-op
after the first run. Companion to `/test-image-diff` (which expects at
least two date folders to compare; default `--keep 3` preserves enough
history for the diff to work and a bit of cushion).

Implementation: `python .claude/skills/test-output-prune/prune.py [args...]`.
Stdlib only — no Pillow/numpy needed.
