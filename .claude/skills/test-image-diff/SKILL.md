---
name: test-image-diff
description: Compare test-output PNGs across yyyyMMdd run folders to flag visual regressions. Walks `%TEMP%/TianWen.Lib.Tests/` and `%TEMP%/FC.SDK.Raw.Tests/`, picks the two most recent date directories, diffs same-named PNGs (matched by test-name folder + filename), and reports per-file mean absolute pixel difference, max difference, changed-pixel percent, and byte-size delta. Use after a CPU/GPU pipeline change to confirm nothing else moved, or after refactoring the render pipeline to spot unintended regressions in the Canon CR2, FITS viewer, or stretch-test outputs.
---

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
is considered unchanged (default 1 — exact match modulo 1 LSB of float-to-byte
rounding noise).

Output is a per-folder grouped table:

```
TianWen.Lib.Tests:  20260513 vs 20260512

  Cr2_RoundTripsThroughTianWenImagePipeline_WithAndWithoutMatrix/
    raw_debayered.png         unchanged   (MAE=0.00, max=0, changed=0.0%, bytes Δ=0)
    raw_debayered_matrix.png  CHANGED     (MAE=2.41, max=37, changed=18.2%, bytes Δ=+12345)

  ... other test folders ...

FC.SDK.Raw.Tests:  20260513 vs 20260512

  RawRendersToPng_WithSensibleDefaults/
    raw_ahd.png               unchanged
    raw_bilinear.png          unchanged
```

Status tags:
- `unchanged` — every pixel within ±threshold, byte-size identical-ish.
- `CHANGED` — non-trivial pixel diff. MAE + max + changed-% give the magnitude.
- `RESHAPED` — image dimensions changed; can't pixel-diff. Treated as a change.
- `NEW` — file present in newer run only.
- `REMOVED` — file present in older run only.

Implementation: `python .claude/skills/test-image-diff/diff.py [args...]`.
Pillow + numpy required (already on the dev box; install via `pip install
Pillow numpy` elsewhere).
