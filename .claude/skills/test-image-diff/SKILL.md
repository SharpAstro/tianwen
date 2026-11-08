---
name: test-image-diff
description: Compare test-output PNGs across yyyyMMdd run folders to flag visual regressions. Walks `%TEMP%/TianWen.Lib.Tests/` and `%TEMP%/FC.SDK.Raw.Tests/`, picks the two most recent date directories, diffs same-named PNGs (matched by test-name folder + filename), and reports per-file mean absolute pixel difference, max difference, changed-pixel percent, and byte-size delta. TianWen.Lib.Tests removes a test's output when the test ends, so run the suite with TIANWEN_KEEP_TEST_OUTPUT=1 first or there is nothing to compare. Use after a CPU/GPU pipeline change to confirm nothing else moved, or after refactoring the render pipeline to spot unintended regressions in the Canon CR2, FITS viewer, or stretch-test outputs.
---

**Run the tests with `TIANWEN_KEEP_TEST_OUTPUT=1` first.** Since #2035 a TianWen.Lib.Tests test deletes its own dated
output folder when it ends, pass or fail, and the first test process of a run sweeps dated folders before yesterday, so
`%TEMP%/TianWen.Lib.Tests/<yyyyMMdd>/` exists only where a run was asked to keep it. Set the variable for the runs
you want to compare (it also stops that run's start-up sweep, so kept folders survive):

```
$env:TIANWEN_KEEP_TEST_OUTPUT = '1'      # pwsh
TIANWEN_KEEP_TEST_OUTPUT=1 dotnet test ...   # Git Bash
```

A kept folder is named `<test-name>_<twelve random letters>`, so two runs never share a folder name; this skill drops
that suffix to match the same test across runs, and takes the newest when one date folder holds several runs of a test.
The comparison is still between two DATE folders, so the two kept runs must be on different days. A run without
the variable deletes the kept folders it finds that are dated before yesterday, so compare soon after the second run or
keep every run in between. The switch is TianWen's: `FC.SDK.Raw.Tests` is another repo's suite and does not read it.

Usage:

```
/test-image-diff
/test-image-diff --root <path>
/test-image-diff --threshold 2
/test-image-diff --dates 20260512 20260513
```

The skill compares two date-folder runs side-by-side and reports any PNGs whose
content changed. Default scope: both `%TEMP%/TianWen.Lib.Tests/` and
`%TEMP%/FC.SDK.Raw.Tests/`. Pass `--root <path>` to scan a specific test-output
root only. Pass `--dates <newer> <older>` to override auto-selection (defaults
to the two most-recent `yyyyMMdd` directories under each root). The
`--threshold N` arg sets the per-channel pixel-diff floor below which a pixel
is considered unchanged (default 1, exact match modulo 1 LSB of float-to-byte
rounding noise).

Output is a per-folder grouped table:

```
TianWen.Lib.Tests:  newer=20260514 (today)  vs  older=20260513 (yesterday)

  Cr2_OpensViaImageTryReadImageFile_WithMatrixAndRgbRender/
    raw_debayered.png         unchanged   (MAE=0.00, max=0, changed=0.0%, bytes Δ=+377, +iCCP[365])
    raw_debayered_matrix.png  CHANGED     (MAE=2.41, max=37, changed=18.2%, bytes Δ=+12345)

  ... other test folders ...

FC.SDK.Raw.Tests:  newer=20260514 (today)  vs  older=20260513 (yesterday)

  RawRendersToPng_WithSensibleDefaults/
    raw_ahd.png               unchanged   (MAE=0.00, max=0, changed=0.0%, bytes Δ=0)
    raw_bilinear.png          unchanged   (MAE=0.00, max=0, changed=0.0%, bytes Δ=0)
```

The auto-pick header spells out the absolute date and a freshness hint
(`today` / `yesterday` / `Nd ago`) so the comparison can't be misread as
"today vs yesterday" when it's actually older runs that lingered.

Status tags:
- `unchanged`: every pixel within ±threshold. Bytes may still differ
  if metadata changed; the chunk-diff column explains why.
- `CHANGED`: non-trivial pixel diff. MAE + max + changed-% give the magnitude.
- `RESHAPED`: image dimensions changed; can't pixel-diff. Treated as a change.
- `NEW`: file present in newer run only.
- `REMOVED`: file present in older run only.

When pixels are `unchanged` but the file size changed, the trailing column
decodes the PNG chunk-level delta so you can tell metadata-only changes
(adding sRGB v4 via `+iCCP[365]`, rewriting `~eXIf[100->150]`, dropping
`-tEXt[42]`) from encoder-output drift. Only ancillary chunks are reported;
`IHDR` / `IDAT` / `IEND` are always present and matched, so the chunk-diff
shows the *interesting* changes only.

Implementation: `python .claude/skills/test-image-diff/diff.py [args...]`.
Pillow + numpy required (already on the dev box; install via `pip install
Pillow numpy` elsewhere).
