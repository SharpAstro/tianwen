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
| lights | `C:/temp/10p_tempel/2026-08-16/LIGHT`, 135 x 60 s (was `C:/temp/astro/2026-08 SV545/...` on the other box) |
| camera | QHY294C Pro, gain 1600, offset 20, -5 C, RGGB 4164x2795 |
| optics | SV 545 (f/4.5 petzval), **203 mm**; the frames' `FOCALLEN` says 205, entered by mistake |
| filter | IDAS LPS-D3, **now stamped into the 135 lights and the 51 flats** (see "The headers were amended") |
| target | 10P/Tempel 2 |
| site | -37.876389, 145.178056 |
| span | 10:53:18 to 14:25:34 UTC, 3.538 h |

**Only the raw data crossed to the second machine, so everything derived has to be rebuilt** (checked
2026-08-25). `C:/temp/10p_tempel` holds the lights, the calibration frames, Astro Pixel Processor's
own masters under `MASTER/` and its integration under `PROC/`, plus `C:/temp/10ptempel` with the
`comet.tif` / `stars.tif` plates. Absent here: the `C:/temp/comet-stack` staging tree, the
`C:/temp/comet-out` star-aligned master, and the cached ephemeris below. Treat the three paths that
follow as a record of what the first box had, not as inputs you can open.

The staging tree was **hard links**, not copies: LIGHT 135, BIAS 200, DARK 60, DARKFLAT 60, FLAT 51,
506 files and 11.2 GB referenced with nothing duplicated. Directory junctions were tried first and
are invisible to recursive enumeration (`Get-ChildItem -Recurse` walks 0 files through one), so hard
links are the working answer when rebuilding it.

`C:/temp/comet-out` held the star-aligned master, `master_10pTemepl_light_60s_-5C_g1600_drizzle*.fits`,
4215x2884x3 float, `STACK_N=135`, plus a `masters/` calibration cache. **Its name preserves the
`OBJECT` typo, which is the tell that it predates the header fix**: a re-run now produces
`master_10PTempel2_...` and, more to the point, a different white balance, since SPCC can finally see
the filter. So artifact 1 has to be rebuilt regardless of which machine you are on.

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
| solved plate scale | **4.7172"/px**, i.e. a focal length of 202.4 mm against an actual **202.5 mm** |
| header `FOCALLEN` | 205, entered by mistake, so anything derived from it (4.6586"/px) is 1.2% out |
| dither / drift | 88.6 px, i.e. **twice the comet's own track** |
| field rotation | 0.0368 deg, monotonic across the run |
| scale stability | 0.028% |
| median FWHM | 2.15 px (HFD 2.65), already at critical sampling |
| SIP rms after the solver fix | 0.11 px |

**That first row is an independent check on the solve, not a complaint about the header.** The
solver recovered 202.4 mm knowing only the pixel size and the stars; the optics are 202.5. Agreeing
to 0.05% with a number it never saw is the strongest evidence here that the WCS can be trusted
quantitatively -- which is exactly what the ephemeris-to-pixel step in item 1 needs of it. The 205 in
`FOCALLEN` was a configuration slip rather than an optical fact, so the 1.2% was never the glass
disagreeing with the solve; it was the profile disagreeing with both. Treat 203 mm as the figure of
record for this rig.

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

**A geocentric ephemeris is not good enough, and this is now measured rather than asserted.** Two
frozen Horizons tracks of the same body over the same instants, differing only in where the observer
stood, are pinned by `CometRateSolverTests.AGeocentricEphemerisWouldGetTheRateWrong`: the fitted
rates differ by **0.780 px/hr, i.e. 2.73 px accumulated over the run** (the hand-derived figure was
2.74) against a 0.11 px SIP residual.

**The error is in HEADING, not speed**, which is the opposite of what the numbers first suggest and
the reason a magnitude comparison would wave it through. The two speeds are 12.646 and 12.805 px/hr,
within 0.16 of each other; the rate VECTORS differ by 0.780 px/hr because the parallax term sweeps
ACROSS the field rather than along the comet's path. That is **3.44 degrees** of heading error, and a
heading error is precisely what smears a stack over three and a half hours. Note also that what
corrupts a rate is the parallax term's CHANGE, not its size: the offset itself runs 3.27 px down to
0.68 px here, and a constant offset would simply shift where the comet sits on the canvas, which
registration absorbs without complaint.

**And the solver's own residual will not catch a heading error -- it PREFERS one.** `MaxResidualPx`
measures how straight a track was, never whether it pointed the right way, and the geocentric track
is *straighter*: parallax is exactly what bends the topocentric one, so removing the observer removes
the curvature along with the correctness. Measured, **0.0142 px geocentric against 0.1589 px
topocentric** -- a factor of ten in favour of the answer that is wrong by 3.44 degrees. A quality gate
on that number alone would not merely fail to reject a bad ephemeris, it would rank it first.

The budget is tight enough to make this matter: holding the cross-track smear under the WCS's own
0.11 px residual over a 44.7 px track allows a heading error of **0.141 degrees**, and geocentric is
24x that. Two defences follow, and neither is the residual. Ask topocentrically by construction,
which is what `HorizonsObserverSource` does; and check the prediction against the nucleus's own
measured centroids (item 2 below), where a heading error appears as a GROWING cross-track offset --
about 2.7 px by the end of this run against a 2.15 px FWHM, which is unmissable.

So `CometEphemeris.TryGetEquatorialJ2000`, which is
geocentric, cannot drive this; it needs a Horizons OBSERVER ephemeris at the site in the header's
`SITELAT`/`SITELONG`. One WAS cached at `C:/temp/eph-1min.txt`, 221 samples on a 1-minute grid; it did
not cross to the second machine, so it needs re-fetching. The target to ask about can now be read off
the frames rather than typed, since `OBJECT` is `10P/Tempel 2` and `CometDesignation.TryParse` takes
`10P` off the front of it.

Worth being precise about why the mount being polar aligned does not remove this: diurnal parallax
is a change in the OBSERVER's position, not a rotation of the field, so no amount of tracking
accuracy addresses it.

## The rate is derived in code (was items 1 and 3, done)

`stack --comet [designation]` registers on the body with no hand-computed constant anywhere.
`--comet-rate dx,dy` is the offline counterpart and the override, and wins when both are given.

The chain, all of it inside `ProcessLightGroupAsync` before the first frame is registered:

| Step | Where | Note |
|---|---|---|
| Resolve the designation | `CometTrackRequest.TryBuild` | `--comet` with no value reads `OBJECT`; `10P/Tempel 2` compacts to `10P`, which is what Horizons' `DES=` takes |
| Derive the window | same | spans every frame in the group, padded one step each side so no epoch is extrapolated |
| Plate-solve the REFERENCE frame | `CatalogPlateSolver` | the one place the pipeline solves anything but the finished master |
| Fetch a topocentric track | `HorizonsObserverSource` | `SITE_COORD` from the frames' own `SITELAT`/`SITELONG`/`SITEELEV` |
| Fit the canvas rate | `CometRateSolver` | OLS per axis, reports a straightness residual |

Three decisions worth not re-litigating:

- **The reference frame has to be solved, and nothing solved it before.** Registration is
  frame-to-frame star-quad matching and never needed to know where the sky was; the only solve was of
  the finished master, in `MasterPostProcessor`, which is far too late. The master's WCS would
  actually serve -- it differs from the reference frame's by a translation, which a rate is immune to
  -- but only after integration, and the rate is needed during it.
- **An unknown site is a refusal, not a geocentric fallback.** Horizons answers a geocentric query
  perfectly happily and the result is wrong by 3.4 degrees of heading. Since it also fits a
  *straighter* line than the correct track, nothing downstream can catch it, so declining and letting
  the caller pass `--comet-rate` is the only defence. `SITEELEV` is different and does default to sea
  level: it is worth well under a thousandth of a pixel.
- **The residual is logged and nothing gates on it.** It measures how straight the track was, never
  whether it pointed the right way. It is there to catch a body too fast for one linear rate.

`SITEELEV` now round-trips through `ImageMeta.SiteElevation`; the Horizons query already took an
elevation, so reading it beat passing a zero that looks like a measurement.

**Reading the body off `OBJECT` needed a parser fix, and it also fixed the search box** -- a separate
function that would otherwise have made the leniency look done while still failing.
`CatalogUtils.TryGuessCatalogFormat` decides a digit-leading string is a comet via `IsNumberedShape`,
and was handing it SPACE-STRIPPED input. That is fatal to the distinction, because the only thing
separating a named comet from a catalogued object that merely starts with digits is the orbit letter
sitting immediately against the number -- and stripped, `10P Tempel` and `30 Doradus` are both
`<digits><PDI><letters>`. The guesser now probes the original string too, and the probe still refuses
a bare letter tail, so 30 Doradus keeps its own catalog. Both directions are pinned.

## Not done

Roughly in dependency order.

1. **Stamp the colour calibration into the master.** The comet layer (item 3) is starless by
   construction, so SPCC -- which fits against catalogue stars -- **cannot be re-derived for it**, and
   the screen combine in item 4 needs both layers on one calibration or it is meaningless. Nothing
   carries it today: `IntegrationFitsWriter` stamps `STACK_N` / `NUMFRAME` / `SWCREATE` / `REJ_*` and
   no colour at all, and `SpccDiagnostics` only ever reaches `GroupResult.Spcc` in memory, where it
   dies with the process. Worse, the ordering rules out simply adding cards at the write: SPCC is
   solved inside `MasterPreviewRenderer.RenderAsync`, which runs *after* `IntegrationFitsWriter.Write`,
   so the FITS is already on disk when the calibration comes into existence. A header-append with the
   existing `FitsHeaderEditor` (what `dataset tag-filter` uses) fits the ordering without a reorder.

   Split, following the `--split-plates` rule that already governs this: the **WB triple is
   inherited** (`WBSOURCE` = `SPCC`/`SKYBG`/`NONE`, plus `WBRED`/`WBGREEN`/`WBBLUE`) because the
   starless plate has nothing to fit, while **background neutralisation stays per-plate**, computed
   from the plate's own pixels *using* that inherited WB -- grafting the star master's bg-neut onto a
   plate with a different background is the documented double-correction that tints it. Stamping the
   measured sky background too (`BKGR`/`BKGG`/`BKGB`) lets a later pass sanity-check its own rather
   than trust blindly.
2. **Treat the ephemeris as a SEED, not the answer.** Then centroid the nucleus per frame as if it
   were a star, fit a smooth track through the centroids with outlier rejection, and use that. The
   fit residuals double as a per-frame quality gate, which the ephemeris alone cannot give. **This is
   also the only check that can catch a wrong heading**, since the straightness residual demonstrably
   prefers the wrong track: a heading error shows here as a GROWING cross-track offset, ~2.7 px by the
   end of this run against a 2.15 px FWHM.
3. **Per-frame SXT over 135 lights**, keeping the starless plate, then integrate those comet-aligned.
   Needs item 1 first, or the layer has no colour.
4. **The screen combine** of artifacts 1 and 3.
5. **A test pinning the compose itself.** A synthetic pair with a known rate and a known dither,
   asserting the target lands on the same canvas pixel and the stars do not. That would catch an
   operand-order slip, which is the one failure the derivation chain cannot.

## The headers were amended (was item 7, done)

The frames now carry the three cards they should have carried at capture. All were written with
`FitsHeaderEditor` through the CLI, which rewrites only the primary header and copies every other
byte verbatim; frames were digested from the data section before and after and **0 had altered pixel
data or size** (186 for the first two amendments, a 30-frame sample for the third, which also
confirmed 0 header-length changes -- the card already existed, so it was replaced in place).

```
tianwen dataset tag-object --path <LIGHT> --object "10P/Tempel 2" --expect "10p Temepl" --apply
tianwen dataset tag-filter --path <LIGHT> --filter "IDAS LPS-D3" --apply
tianwen dataset tag-filter --path <FLAT>  --filter "IDAS LPS-D3" --apply
tianwen dataset tag-site-elevation --path <ROOT> --elevation 74 --expect 120 --apply
```

**`SITEELEV = 74.0`, on every frame type rather than the lights alone**, because where the rig stood
is true of a dark and a bias as much as of a light. The capture profile had recorded 120 m against a
true 74 m. **This corrects the RECORD, not a measurement**: 46 m is 0.0007% of an Earth radius, so it
moves the derived comet rate by 0.00002 px over the night -- against a 0.11 px registration residual,
which is to say never. The number that would matter is latitude or longitude, and those are why an
unknown site is a refusal rather than a fallback.

Two things came out of doing it. `FitsHeaderEditor` only knew how to write STRING cards, and a number
put through that path comes out quoted -- which reads correctly to a human and is a *string* to
anything that types its cards; `SetNumericCardAsync` + `FormatNumericCard` write the fixed format
(unquoted, right-justified to byte 30) and share every guard with the string path so the two cannot
drift. And the first run silently skipped a `MASTERDARKFLAT`, which exposed a `DarkFlat` missing from
the command's default frame-type list while its own help claimed "every frame type" -- a real gap for
anyone using the standard CMOS dark-flat workflow, not just for this one derived file.

**`FILTER = 'IDAS LPS-D3'`, on the flats as well as the lights.** The flat panel's light went through
the same glass, and `MasterGroupKey` compares lights to flat masters on filter, so tagging only the
lights would leave the pair disagreeing about the optical train. It would not have *broken* anything
here -- a filter mismatch is a 1000-point penalty in `MatchMaster`, not a gate, and this dataset has
exactly one flat group, so it would still have won on being the only candidate -- but the agreement
should be true rather than merely harmless. Bias, darks and dark-flats were left alone: no light
passes, and `MasterGroupKey` documents the filter as empty for them.

What the card buys, measured through `FilterCurveDatabase.BuildChannelThroughputs` on frame 0001:

| | R | G | B |
|---|---|---|---|
| before (no `FILTER`) | 0.3550 | 0.4064 | 0.2386 |
| after (`IDAS LPS-D3`) | 0.2488 | 0.4562 | 0.2951 |

Red loses 30% of its weight, which is the D3 being what it is: a notch filter suppressing NaI
589.0/589.6 and OI 630.0/636.4, all of them in the red CFA passband. Before the card, SPCC integrated
its stellar SEDs against QE x CFA alone -- i.e. modelled an optical train with no filter in it -- and
returned a white balance for a camera that was never used. This is the whole reason the digitised
curve exists; the frames just could not reach it.

**`OBJECT = '10P/Tempel 2'`, corrected from `'10p Temepl'`.** Not cosmetic in two ways.
`LightGroupKey` partitions lights by `OBJECT` and its slug names the master, so the typo was being
baked into every output filename (`master_10pTemepl_...`). And the card is the only place a frame
says which body it is, which the ephemeris work in item 1 has to read back -- so the *form* matters
as much as the spelling: `CometDesignation.TryParse` takes the designation off the front of
`10P/Tempel 2` and answers `10P`.

Two things deliberately left as they were. The `PROC/` outputs carry the same typo and
`FILTER = 'RGB'`, but they are Siril products of this data rather than inputs, and for the `-cbg`
plates already colour-calibrated `RGB` is the honest answer. And the `MASTER/` calibration masters
are another tool's, which TianWen rebuilds from the raw frames anyway.

## Also worth knowing

The nonlinearity number (0.185 px) is the argument for keeping a single linear rate for now, and the
place a quadratic term would go is documented on the option itself. Do not add one speculatively:
it is currently below the registration's own residual, so it would be fitting noise.

## Star-tracked capture only, and whether that can be detected

**Everything here assumes the mount tracked the STARS**, which is what this dataset did. The other
way round is a real technique -- drive the mount at the comet's own rate -- and it is not supported.

**The registration maths is the same either way, which is the non-obvious part.** If the mount
follows the comet, then between frames the STARS move across the sensor; star registration undoes
exactly that, so after it the comet drifts across the canvas at the same rate it would have under
star tracking. `CometRatePxPerHour` therefore applies unchanged. Nothing about the compose needs to
know which mode produced the frames.

What differs is **which object is trailed INSIDE each sub**, and that is not recoverable by any
alignment:

| | star-tracked | comet-tracked |
|---|---|---|
| stars | points | trailed by rate x exposure |
| comet | trailed by rate x exposure | point |
| star layer (artifact 1) | good | trailed, unusable |
| comet layer (artifact 3) | nucleus slightly trailed | sharp |

So comet tracking buys a better comet and ruins the stars, which is why the usual answer for a slow
comet is to track the stars and accept a marginally soft nucleus.

**Can an unattended run tell which it got?** In principle yes, from the field stars' elongation and
its position angle: comet-tracked frames elongate every star along the ephemeris motion vector by
rate x exposure, and we already measure per-frame median HFD and ellipticity (the inputs
`--quality-reject-sigma` uses). Comparing the measured elongation axis against the motion vector the
ephemeris predicts is a clean, cheap discriminator.

**On this dataset it is neither possible nor necessary**, and the numbers say why. At 12.64 px/hr a
60 s sub trails 0.211 px, which against a 2.15 px FWHM adds in quadrature to 2.160 px -- a **0.48%**
elongation, far under the frame-to-frame scatter of the measurement itself. Reaching a 10%
elongation would need a ~1 px trail, i.e. about 59 px/hr at this exposure, five times this comet:

| sub | trail | elongation |
|---|---|---|
| 60 s | 0.211 px | 0.48% |
| 120 s | 0.421 px | 1.90% |
| 300 s | 1.053 px | 11.4% |
| 600 s | 2.107 px | 40.0% |

The same arithmetic that makes the mode undetectable makes it **irrelevant**: where the trail is a
fraction of the seeing disc, the two capture modes produce near-identical data, and the question only
becomes worth asking when rate x exposure approaches the FWHM. A detector should therefore be gated
on that product rather than run unconditionally -- below roughly half a FWHM it would be reporting
noise. Until one exists, an unattended run should assume star tracking, which is both the common case
and the one that degrades gracefully if it is wrong.
