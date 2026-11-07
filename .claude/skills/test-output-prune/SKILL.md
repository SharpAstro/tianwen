---
name: test-output-prune
description: Delete old `yyyyMMdd` test-output date folders under `%TEMP%/TianWen.Lib.Tests/` and `%TEMP%/FC.SDK.Raw.Tests/`, keeping only the N most recent (default 3). TianWen.Lib.Tests now removes a test's output when the test ends, so what is left is output a run kept with TIANWEN_KEEP_TEST_OUTPUT=1 and what a crashed or killed run left; the next test run's start sweeps TianWen's own dated folders older than a day, so this skill matters mainly for kept output and for FC.SDK.Raw.Tests. Use when the user asks to clean up, prune, delete old, free disk, or trim test outputs.
---

Since #2035 TianWen.Lib.Tests does most of this itself, so run `--dry-run` first and expect little:

- A test deletes its own dated folder when it ends, pass or fail, **unless the run was started with
  `TIANWEN_KEEP_TEST_OUTPUT=1`**. Only kept output, and what a crashed or killed run could not delete, remains.
- The first test process of a run (unit, functional and GPU alike) sweeps the dated folders under
  `%TEMP%/TianWen.Lib.Tests/` that are dated before yesterday and were last written over a day ago. A run with
  `TIANWEN_KEEP_TEST_OUTPUT=1` sweeps nothing, so kept folders stay until a later run without it.
- `FC.SDK.Raw.Tests` is another repo's suite and has neither the cleanup nor the sweep, so its dated folders still
  accumulate and are what `--keep N` is for.

Usage:

```
/test-output-prune
/test-output-prune --keep 5
/test-output-prune --dry-run
/test-output-prune --root <path>
```

Deletes every `yyyyMMdd` subdirectory under each test-output root except the
most-recent `--keep N` (default 3). It never touches `TianWen.Lib.Tests/t/` beside them: the tests' temporary
folders (`TempFolders`), which each test deletes as it ends and which the first test process of a day sweeps of
anything older (#1197). Pass `--dry-run` to print what would be
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

The skill is naturally idempotent; running it twice in a row is a no-op
after the first run. Companion to `/test-image-diff` (which expects at
least two kept date folders to compare, so run the suite with `TIANWEN_KEEP_TEST_OUTPUT=1` for the runs to compare;
default `--keep 3` preserves enough history for the diff to work and a bit of cushion).

Implementation: `python .claude/skills/test-output-prune/prune.py [args...]`.
Stdlib only; no Pillow/numpy needed.
