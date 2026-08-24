# Comet / moving-target integration

Status: **WIP.** One plumbing change is in (`StackingOptions.CometRatePxPerHour` plus the compose in
`StackingPipeline`). The rate it consumes is currently derived by hand. Everything in "Not done"
below is outstanding, and this doc exists so the measurements are not lost with the shell history
that produced them.

## What this is for

A comet is sharp in a stack only if the stack is registered on the comet. Registering on the stars
trails it, and registering on the comet trails the stars, so a finished image needs both and a
combine. The target is four artifacts:

1. **Star layer.** Ordinary star-aligned stack, then StarXTerminator keeping the STARS plate.
2. **Comet-aligned stack** of the raw lights (this is what the plumbing enables).
3. **Comet layer.** SXT each of the 135 raw lights keeping the STARLESS plate, then comet-align and
   stack those. Removing stars per frame before integrating is what keeps the trails out, rather
   than trying to reject them statistically afterwards.
4. **Combine** 1 and 3 with a screen blend.

RC-Astro CLI is present and does the SXT work: `C:/Program Files/RC-Astro/CLI/rc-astro` v2.6.5,
verbs `bxt` / `sxt` / `nxt`, driven through `RcAstroEnhancerBase`'s NDJSON protocol.

## The dataset this was measured on

| | |
|---|---|
| lights | `C:/temp/astro/2026-08 SV545/2026-08-16/LIGHT`, 135 x 60 s |
| camera | QHY294C Pro, gain 1600, offset 20, -5 C, RGGB 4164x2795 |
| optics | SV 545 (f/4.5 petzval), `FOCALLEN` 205 mm nominal |
| filter | IDAS LPS D3. **The frames carry no `FILTER` card**, so the curve we digitised cannot resolve |
| target | 10P/Tempel |
| site | -37.876389, 145.178056 |
| span | 10:53:18 to 14:25:34 UTC, 3.538 h |

Staging tree at `C:/temp/comet-stack` is **hard links**, not copies: LIGHT 135, BIAS 200, DARK 60,
DARKFLAT 60, FLAT 51, 506 files and 11.2 GB referenced with nothing duplicated. Directory junctions
were tried first and are invisible to recursive enumeration (`Get-ChildItem -Recurse` walks 0 files
through one), so hard links are the working answer.

`C:/temp/comet-out` holds the star-aligned master already:
`master_10pTemepl_light_60s_-5C_g1600_drizzle*.fits`, 4215x2884x3 float, `STACK_N=135`, plus a
`masters/` calibration cache.

## Measured

The comet track, from two independent routes that agree:

| | |
|---|---|
| rate | **dx +10.897 px/hr, dy +6.412 px/hr** (12.64 px/hr, PA 152 deg) |
| total drift | 44.7 px over 3.538 h |
| per 60 s sub | **0.211 px**, so no intra-frame trailing and no need to split subs |
| departure from linear | 0.185 px worst case, 0.080 rms (a quadratic term would give 0.019) |

The field, which is what says the rate is usable:

| | |
|---|---|
| solved plate scale | **4.7172"/px**, so true focal length is about 202.4 mm |
| header `PIXSCALE` | 4.6586, i.e. **1.2% wrong**; `FOCALLEN` is only ever a hint |
| dither / drift | 88.6 px, i.e. **twice the comet's own track** |
| field rotation | 0.0368 deg, monotonic across the run |
| scale stability | 0.028% |
| median FWHM | 2.15 px (HFD 2.65), already at critical sampling |
| SIP rms after the solver fix | 0.11 px |

## Two things that decide the design

**The compose happens in CANVAS space, after the star solution.** Dither is 88.6 px against a
44.7 px comet track, so in frame pixels the dither dominates and a frame-space shift is simply the
wrong quantity. Once the star solution has absorbed dither and field rotation, canvas space is the
one basis where the target's own motion is clean. A rate is also invariant under the later canvas
shift, which is a constant translation, so it needs no knowledge of the final canvas origin. The
reference frame needs no special case because its dt is zero.

`System.Numerics.Matrix3x2` uses the row-vector convention, so `starSolution * translation` applies
the star solution first and then translates. That ordering IS the canvas-space property; reversing
the operands silently gives a frame-space shift that looks plausible and is wrong.

**A geocentric ephemeris is not good enough.** Topocentric minus geocentric moves by 2.74 px across
this run, which is 25x the registration residual. So `CometEphemeris.TryGetEquatorialJ2000`, which is
geocentric, cannot drive this; it needs a Horizons OBSERVER ephemeris at the site in the header's
`SITELAT`/`SITELONG`. One is cached at `C:/temp/eph-1min.txt`, 221 samples on a 1-minute grid.

Worth being precise about why the mount being polar aligned does not remove this: diurnal parallax
is a change in the OBSERVER's position, not a rotation of the field, so no amount of tracking
accuracy addresses it.

## Not done

Roughly in dependency order.

1. **Derive the rate in code.** Today it is a hand-computed constant. Wants: read `SITELAT`/`SITELONG`
   and the exposure epochs from the frames, fetch a Horizons OBSERVER ephemeris, convert two sky
   positions to canvas pixels through the reference frame's WCS, and divide. The plate-solve fix that
   makes the WCS trustworthy here is merged (SIP rms 0.11 px), which is why this is now worth doing.
2. **Treat the ephemeris as a SEED, not the answer.** Then centroid the nucleus per frame as if it
   were a star, fit a smooth track through the centroids with outlier rejection, and use that. The
   fit residuals double as a per-frame quality gate, which the ephemeris alone cannot give.
3. **A CLI surface.** `StackingOptions.CometRatePxPerHour` has no flag. Probably
   `stack --comet <designation>` deriving the rate, with an explicit `--comet-rate <dx>,<dy>` escape
   hatch, parsed where the other option strings are.
4. **Per-frame SXT over 135 lights**, keeping the starless plate, then integrate those comet-aligned.
5. **The screen combine** of artifacts 1 and 3.
6. **Tests.** Nothing pins any of this yet. The cheap and valuable one is the compose itself: a
   synthetic pair with a known rate and a known dither, asserting the target lands on the same canvas
   pixel and the stars do not. That would have caught an operand-order slip.
7. **The missing `FILTER` card.** Adding `FILTER = 'IDAS LPS D3'` to these frames would let the curve
   we digitised resolve, which matters for the colour path rather than for registration.

## Also worth knowing

The nonlinearity number (0.185 px) is the argument for keeping a single linear rate for now, and the
place a quadratic term would go is documented on the option itself. Do not add one speculatively:
it is currently below the registration's own residual, so it would be fitting noise.
