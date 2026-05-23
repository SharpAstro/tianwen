# PLAN: Port Generalised Hyperbolic Stretch + iterative convergence

> Status: **NOT STARTED**. Captures the work to replace the current
> `Image.GeneralizedHyperbolicStretch` impl with a faithful port of
> Cranfield's reference PixInsight script, then layer iterative
> parameter convergence on top so `GhsStretchStarlessStep` works as
> a stretch-from-linear default rather than the visually-too-dim
> opt-in it ships as today.

## Goal

Make GHS work as Paul (Polymath Astro) demonstrates in his video:
take a linear plate, lift the histogram peak to ~0.25 with controlled
highlight roll-off, produce a "linear log-slope" histogram (an
empirical quality marker for a well-stretched image). Two pieces:

1. **Port** the curve math from
   [mikec1485/GHS](https://github.com/mikec1485/GHS) -- the official
   Cranfield/Payne PixInsight script -- into `Image.Stretch.cs`.
   Today's `BuildGhsLut` uses a different curve family (single
   `up^a / (up^a + b*(1-up)^a)` form) that produces darkened
   midtones for any plausible parameter combination against a
   linear input.
2. **Auto-tune** the curve via `Image.ConvergeGhsStretchFactor`:
   bisect `D` (the stretch factor) until the post-stretch
   histogram satisfies (a) median ~= 0.25 and (b) approximate linear
   log-slope across the [bg-peak, 0.95] band -- a "the stretch did
   the job" marker independent of any specific input. With the
   right curve, `GhsStretchStarlessStep` becomes a viable
   stretch-from-linear default; until then it stays opt-in per
   [[feedback_ghs_not_default]].

## Why the existing impl is wrong (gap analysis)

The codebase's `BuildGhsLut` does NOT implement Cranfield's GHS.
Empirical curve sweep against the production defaults
(`intensity=1.5, asymmetry=8, hp=0.8, sp=0.05`) showed the entire
curve sits *below* the identity line:

| Input | Output | Comment |
|-------|--------|---------|
| 0.05 (bg) | 0.0007 | crushed |
| 0.10 | 0.0021 | crushed |
| 0.20 | 0.0069 | crushed |
| 0.50 | 0.0500 | hinge -- equals SP |
| 0.90 | 0.8401 | mild compression |

The reference Cranfield curve, fed the parameters Paul demonstrates
(`ln(D+1)=1.30 b=-1.00 SP=0.57143 HP=0.80357`), lifts the
histogram peak as expected. The codebase's impl differs along five
axes that all matter:

| Axis | Codebase | Reference | Impact |
|---|---|---|---|
| Curve form | `up^a/(up^a+b*(1-up)^a)` | 4 piecewise branches keyed on `B` (log/power/exp/hyperbolic) | Wrong shape entirely |
| `B` parameter | Rejects negative via `ThrowIfNegativeOrZero` | Signed -- screenshot uses `b=-1` | Refuses Paul's settings |
| `SP` semantics | "output at input=0.5" (`(0.5, SP)` hinge) | "input value where curve hinges" -- the inflection point | Inverted meaning |
| `D` / `intensity` units | Direct exponent `a` | User enters `ln(D+1)`, script converts via `exp(slider) - 1` | Different sliding range |
| Missing knobs | -- | `BP` (black point), `CP` (clipping proportion) | Required parameters absent |

The fix is a full re-port, not a tuning adjustment. The existing
`BuildGhsLut` is throwaway code.

## Reference: the four B-branches

From `mikec1485/GHS/src/scripts/GeneralisedHyperbolicStretch/lib/GHSStretch.js`
lines 314..530 (forward direction) + lines 870..1040 (coefficient
derivations). Each branch is a 4-region piecewise function:

```
y = a1 + b1 * z                          if z < LP                   (linear shadow)
y = <branch-specific form>(a2, b2, c2, d2, e2, z)   if LP <= z < SP   (left of hinge)
y = <branch-specific form>(a3, b3, c3, d3, e3, z)   if SP <= z <= HP  (right of hinge)
y = a4 + b4 * z                          if z > HP                  (linear highlight)
```

Branches:

| `B` regime | Inner form | Branch label |
|---|---|---|
| `B == -1` | `y = a + b * ln(c + d * z)` | Logarithmic |
| `B < 0, B != -1` | `y = a + b * (c + d * z)^e` with `e = (B-1)/B` | Generalised hyperbolic (negative b) |
| `B == 0` | `y = a + b * exp(c + d * z)` | Exponential |
| `B > 0` | `y = a + b * (c + d * z)^e` with `e = -1/B` | Hyperbolic / harmonic |

Coefficients are derived once per `(D, B, LP, SP, HP)` invocation
via the `qlp / q0 / qwp / q1 / q` chain in the reference; they
guarantee the curve passes through (0, 0) and (1, 1) and is
continuous at LP / SP / HP. The math is verbatim portable from the
JavaScript -- only the typing changes.

Parameter conversion:

```
D_actual = exp(convFacD * D_user) - 1     // GHSStretchParameters.js line 54
                                          // convFacD = 1.0 in the reference
```

User-facing knob is the `ln(D+1)` slider value (matches the
screenshot's `1.30`).

## Quality marker: "linear log-slope" convergence target

A well-stretched astro histogram approximates exponential decay
above the bg peak -- equivalently, *linear* on a log-y axis from
roughly the bg peak through the upper tail. The user's screenshot
confirmed this as their personal quality check. We adopt it as the
convergence target's secondary metric:

1. Locate the post-stretch bg peak (mode of the post-stretch
   histogram).
2. Sample the histogram from `peak + 1*MAD` through `0.95` at
   uniform log-spacing.
3. Compute the slope of `log(count)` vs `value` via least squares.
4. The score is `R^2` of that linear fit, or equivalently the
   variance of `log(count) - linear_fit(value)`.

A score above ~0.9 means the stretch produced an exponential decay;
below ~0.7 means the stretch is over- or under-shooting. For
narrowband or steep nebula inputs this metric naturally fails (real
signal violates the exponential decay assumption); the test corpus
needs to span broadband + narrowband cases to map out where the
metric is meaningful.

## Phasing

| Phase | Scope | Status |
|---|---|---|
| 0 | Cleanup: delete broken convergence draft (`ConvergeGhsIntensity` + `ConvergeGhsIntensityTests` + `GhsCurveProbeTests`); commit the empirical curve-output table from the probe to this plan's "Why the existing impl is wrong" section so the rationale is preserved | NOT STARTED |
| 1 | Reference math port: replace `BuildGhsLut` body with the four B-branch forms; expose signed `b`, add `BP` + `CP` parameters, fix SP semantics (input hinge). Curve API becomes `(LnD, B, SP, LP, HP, BP, CP) -> Image`. Old `intensity` parameter renamed to `LnD` to match the reference and force callers to recognise the unit change | NOT STARTED |
| 2 | Reference validation: golden-table tests against the four branch values at known `(D, b)` from the screenshot. Cross-check curve values at LP / SP / HP for continuity (the coefficient derivations should produce piecewise-continuous output by construction; tests catch off-by-one errors in the port) | NOT STARTED |
| 3 | `GhsStretchStarlessStep` rewire: parameters renamed to match (`LnD`, `B`, `SP`, `LP`, `HP`, `BP`, `CP`). Default values from the screenshot (`LnD=1.30 B=-1.00 SP=0.57143 HP=0.80357 LP=0 BP=0 CP=0`). CLI flag rename: `--ghs-d` (was `--ghs-intensity`), `--ghs-b` (was `--ghs-asymmetry` -- now allows negative). Per [[feedback_ghs_not_default]] this remains opt-in via `--ghs-starless` until Phase 6 quality-gates it | NOT STARTED |
| 4 | `Image.ConvergeGhsStretchFactor`: bisects `D_user = LnD` over `[0.1, 3.0]` with `B`, `SP`, `LP`, `HP` fixed at the caller-supplied values. Per iteration: build LUT, walk histogram bins, accumulate post-stretch median + clip rate + log-slope score. Returns the converged `LnD` plus the achieved metrics. Mirrors `Image.ConvergeStretchFactor` structurally | NOT STARTED |
| 5 | Convergence tests: (a) synthetic dark sky converges median to 0.25 +/- 0.01 with log-slope `R^2 >= 0.9`; (b) synthetic narrowband-style steep input converges median but log-slope score drops below 0.7 (documents the metric's failure mode); (c) deterministic output for identical input; (d) HP escalation kicks in when clip rate exceeded; (e) golden-table values against a known-good reference (run the reference JS script on a small synthetic, capture its output, compare to ours) | NOT STARTED |
| 6 | Wire `AutoConverge` into `GhsStretchStarlessStep` + `--ghs-starless-auto` CLI flag. When set, runs `ConvergeGhsStretchFactor` before the multi-pass loop and uses the converged `LnD` for all passes. Log resolved `LnD`, achieved median, clip rate, log-slope score on the timing line | NOT STARTED |
| 7 | Real-image smoke: run `tianwen image sharpen --dual-stretch --ghs-starless --ghs-starless-auto` against the SoL drizzle autocrop at `/c/temp/stack/output/master_StatueofLibertyNebula_light_60s_-5C_g120_drizzle_autocrop.fits`. Capture post-convergence parameters + log-slope score + visual diff against the existing MTF + bg-reduce + compress-highlights path. If passing across the {broadband, narrowband, drizzle, single-light} corpus, promote to the default starless stretch in `SharpenRequest.Canonical()` and update [[feedback_ghs_not_default]] | NOT STARTED |

## Risk register

| Risk | Mitigation |
|---|---|
| Branch boundaries (LP / SP / HP) introduce floating-point discontinuities | Reference's coefficient derivation makes the curve continuous by construction; verify with continuity tests in Phase 2. The post-build monotonic correction in the current `BuildGhsLut` (lines 502-507) is a safety net; keep it. |
| Test corpus too narrow to catch failure modes | Phase 5 builds five distinct synthetic histograms (dark-sky / narrowband / steep galaxy / faint diffuse / over-exposed). Real-image smoke in Phase 7 adds three actual masters across the strategy spectrum. |
| Log-slope metric over-fits to broadband cases and rejects valid narrowband stretches | Phase 5 case (b) explicitly tests this; if confirmed, the metric becomes optional (a hint that triggers a warning rather than a convergence failure) |
| Bisection runs to bounds without converging | `ConvergeGhsStretchFactor` returns `ConvergedBothTargets=false` with best-effort knobs; caller decides whether to use the best effort or fall back to a fixed `LnD=1.30`. Pattern matches `Image.ConvergeStretchFactor` |
| Reference math has bugs we silently inherit | The reference has been in production with PixInsight users since 2021. Trust it; verify our port matches reference output to floating-point tolerance via Phase 5 case (e) |

## Open questions

1. **`BP` / `CP` integration with the existing
   `BackgroundReduceStep`.** The reference's BP (black point) is a
   clipping operation applied before the curve; our pipeline has a
   separate `BackgroundReduceStep` that does an S-curve bg pull
   *after* the stretch. Decide whether to use BP within GHS or rely
   on the existing post-step. **Vote:** keep them independent --
   BP=0 in GHS, let `BackgroundReduceStep` handle bg shaping
   post-stretch since it's already orthogonal to GHS in the
   step list.
2. **Default `B` direction.** Screenshot shows `B=-1.00`
   (logarithmic branch); Paul's transcript suggests `B=8` for the
   "Local Stretch Intensity" slider in his case-1 walkthrough.
   The two are different recipes for different image cases. The
   `AutoConverge` flow needs a default `B`; using `-1` makes the
   logarithmic branch the default. **Vote:** ship `B=-1` as the
   default (logarithmic stretch, well-conditioned across linear
   inputs); make `B=8` accessible via `--ghs-b` when callers want
   the hyperbolic family.
3. **Use the existing arcsinh branch as an additional curve
   choice?** The reference script also implements `Arcsinh Stretch`
   and `Histogram Transformation` in the same dispatch. Worth
   adding a `--ghs-type` switch to expose them? **Vote:** not in
   v1. Arcsinh is requested in
   [[PLAN-ai-enhancement-next]] as a separate
   `AsinhStretchTransform`; HT is what `Image.MtfStretch` already
   provides. Keep GHS focused on the GHS-Integral family.

## Cross-references

- [`PLAN-ai-enhancement-next.md`](PLAN-ai-enhancement-next.md) --
  the "Productionise GHS starless stretch" entry there is
  superseded by this plan
- [`TODO.md`](TODO.md) -- the productionisation hypothesis list under
  "AI Enhancement" predates the reference-impl discovery; once
  this plan ships, that entry collapses to "DONE per PLAN-ghs.md"
- [`CLAUDE.md`](CLAUDE.md) "Stretch Pipeline: CPU/GPU Mirror" --
  GHS is CPU-only (no GPU mirror) and stays that way; it's a
  one-time pipeline step, not a live viewer mode

## Reference files

- `mikec1485/GHS/src/scripts/GeneralisedHyperbolicStretch/lib/GHSStretch.js`
  -- the forward + inverse curve dispatch
- `mikec1485/GHS/src/scripts/GeneralisedHyperbolicStretch/lib/GHSStretchParameters.js`
  -- parameter conversion (`convertD = exp(D) - 1`) and defaults
- Paul / Polymath Astro YouTube video "Generalised Hyperbolic
  Stretch - Will this change your workflow" -- case-1 (linear ->
  display) walkthrough using `b=8, hp=0.8, sp=lift-off, intensity=variable`
- Screenshot from the user's working PixInsight session shipping a
  good case-1 stretch:
  `LnD=1.30 b=-1.00 SP=0.57143 LP=0 HP=0.80357 BP=0.0350 CP=0`
