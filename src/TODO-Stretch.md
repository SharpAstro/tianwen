# Stretch Algorithm Improvements

Learnings from PixInsight Statistical Stretch (SetiAstro, v2.3).

## 1. Luma-only stretch mode

Biggest quality win for color images. Our linked stretch applies the same MTF to all RGB channels, which desaturates colors. Luma-only mode:

- [x] Compute luminance: `Y = 0.2126*R + 0.7152*G + 0.0722*B` (rec709)
- [x] Stretch Y → Y' using the standard MTF
- [x] Scale all channels by `Y'/Y`, preserving chrominance ratios
- [x] Add as a third `StretchMode` (Linked / Unlinked / **Luma**)
- Support rec601/rec2020 weighting options

## 2. HDR compression (GPU shader)

Hermite soft-knee compression for blown-out star cores and galaxy centers.
Only affects values above a configurable knee point — faint detail is untouched.

- [x] Add `uHdrAmount` and `uHdrKnee` uniforms to the image fragment shader
- [x] Hermite basis: cubic interpolation from knee to 1.0 with adjustable tangent
- [x] Toolbar button or keyboard shortcut to cycle amount/knee presets
- [x] Zero CPU cost — pure display-time transform like curves boost

## 3. Normalize after stretch

Simple `x / max(x)` to fill the full [0,1] range. After stretch the max may be
slightly below 1.0. Easy post-step in the stretch pipeline.

## 4. Iterative convergence

PI can run multiple stretch iterations until the median converges to the target.
Our single-pass may not land exactly on the target median. Less critical for a
viewer, more relevant for batch processing pipelines.

## 5. Luma blend

Smoothly blend between linked and luma-only results:
`result = (1 - blend) * Linked + blend * Luma`

Gives fine-grained control over color preservation vs uniform stretch.
Could be a slider or a few presets (0%, 60%, 100%).
