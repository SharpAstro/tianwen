# Detection purity on the stacking path (2026-08-13)

Produced by `DetectionPurityProbe` (env-gated, `TIANWEN_PURITY_SUBS`). Answers the question task #7
says to answer before porting anything: does `StackingPipeline`'s detect site actually hurt, or are
the star and quad counts merely different?

`p` is the fraction of a frame's top-K detections that reproduce on another sub. `p^4` is the ceiling
on the quad match rate, because a quad matches only when the same four stars form it in both frames,
and that is the quantity registration actually spends.

Session: `2025-12-28/Segaull+Thors_Helmet`, HIP 42861 group, ZWO ASI533MC Pro, 60 s subs.
Chosen because it is the case task #7 names, and the one the dataset path still fails to register.

## The headline

| depth | mono real | mono **control** | debayered ch0 real | ch0 **control** |
|---|---|---|---|---|
| 50 | 92.0% | no match | **no match** | no match |
| 100 | 92.0% | 21.0% | **no match** | no match |
| 200 | 93.0% | 17.0% | **no match** | no match |
| 500 | 92.8% | 22.6% | 15.5-21.2% | no match |
| all | 94.7% | 90.5% | 16.4-23.1% | no match |

"mono" is `FindStarsAsync` on the calibrated pre-debayer image (the dataset path, which routes an
RGGB frame through `BilinearMono`). "debayered ch0" is channel 0 of a VNG-debayered frame, which is
the interpolated RED plane and what `StackingPipeline` does at all three of its detect sites today.

**At the depths that form quads, the debayered red plane cannot produce even 20 mutual matches
between consecutive subs of the same field, while mono reproduces at 92%.** In `p^4` terms that is
roughly 70% against 0.1%.

Detection counts say the same thing less starkly: mono returns 1046-1097 detections across six
consecutive subs, a 5% spread; the debayered plane returns 325-574, a 73% spread on the same field
minutes apart. A real star population does not change by 73% in five minutes.

## The control is mandatory, and this is why

These are uncalibrated subs, so fixed-pattern noise sits at identical sensor positions in every
frame and reproduces at ~100% by construction. Being the bulk of a faint-end detection list, it also
pins the estimated offset at exactly (0.00, 0.00) however far the sky moved, which is the tell.

Two checks established the contamination and then bounded it:

- **43-minute baseline** (`_0044` vs `_0051`): offset still exactly (0.00, 0.00), p unchanged at
  92-96%. No real field drifts that little over 43 minutes with no dither.
- **Negative control**, two frames of DIFFERENT TARGETS from the same night and camera, where
  nothing real can correspond: mono still scored **90.5%** at "all" depth against 94.7% for genuine
  consecutive subs. Indistinguishable, so the "all" row carries no information.

The same control separates cleanly at the bright end (21% / 17% / 23% against 92% / 93% / 93%),
because the contamination lives in the faint tail. **So read the top-K rows, treat ~20% as the
false-match floor, and discard "all".** The conclusion above survives this correction with a wide
margin; an earlier reading that took "all" at face value did not.

## What this does and does not justify

- **It justifies the port.** The gap at quad-forming depths is not marginal.
- **It does not predict HIP 42861 will register.** This session already fails in the dataset path
  WITH the fix applied (drizzle bake: `stars=89 quads=58, other subs' quads 26..72, skipped 0
  too-few-stars + 48 no-quad-fit`, 0 of 49 registered). Quads exist on both sides and still do not
  correspond, so by the self-diagnosing warning's own logic that is a separate problem from the
  detect site, and mono's 92% here says the same. Do not treat this session as the port's
  acceptance test.
- **It is one session, one camera.** Before the port ships, run the probe on a rich field and on a
  dual/quad-band filter session, where the argument for mono is different: under a narrowband filter
  the green photosites carry OIII plus continuum while red carries Ha alone, so the per-channel pick
  is filter-dependent in a way mono is not.
