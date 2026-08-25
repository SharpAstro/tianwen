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

**A geocentric ephemeris is not good enough.** Topocentric minus geocentric moves by 2.74 px across
this run, which is 25x the registration residual. So `CometEphemeris.TryGetEquatorialJ2000`, which is
geocentric, cannot drive this; it needs a Horizons OBSERVER ephemeris at the site in the header's
`SITELAT`/`SITELONG`. One WAS cached at `C:/temp/eph-1min.txt`, 221 samples on a 1-minute grid; it did
not cross to the second machine, so it needs re-fetching. The target to ask about can now be read off
the frames rather than typed, since `OBJECT` is `10P/Tempel 2` and `CometDesignation.TryParse` takes
`10P` off the front of it.

Worth being precise about why the mount being polar aligned does not remove this: diurnal parallax
is a change in the OBSERVER's position, not a rotation of the field, so no amount of tracking
accuracy addresses it.

## Not done

Roughly in dependency order.

1. **Derive the rate in code.** Today it is a hand-computed constant. Wants: read `SITELAT`/`SITELONG`
   and the exposure epochs from the frames, fetch a Horizons OBSERVER ephemeris, convert two sky
   positions to canvas pixels through the reference frame's WCS, and divide. The plate-solve fix that
   makes the WCS trustworthy here is merged (SIP rms 0.11 px), which is why this is now worth doing.
   **Which body to ask Horizons about can come from the frames**, now that `OBJECT` is corrected and
   `CometDesignation.TryParse` reads a space-separated name tail: `10P/Tempel 2` and `10P Tempel 2`
   both answer `10P`. So `stack --comet` in item 3 wants a designation only to OVERRIDE the header,
   not to supply what the header already knows.

   The same change fixed the search box, which is a separate function and would otherwise have made
   the leniency look done while still failing: `CatalogUtils.TryGuessCatalogFormat` decides a
   digit-leading string is a comet via `IsNumberedShape`, and was handing it SPACE-STRIPPED input.
   That is fatal to the distinction, because the only thing separating a named comet from a
   catalogued object that merely starts with digits is the orbit letter sitting immediately against
   the number -- and stripped, `10P Tempel` and `30 Doradus` are both `<digits><PDI><letters>`. The
   guesser now probes the original string too, and the probe still refuses a bare letter tail, so
   30 Doradus keeps its own catalog. Both directions are pinned.
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

## The headers were amended (was item 7, done)

The frames now carry the two cards they should have carried at capture. Both were written with
`FitsHeaderEditor` through the CLI, which rewrites only the primary header and copies every other
byte verbatim; all 186 frames were digested from the data section before and after and **0 had
altered pixel data or size**.

```
tianwen dataset tag-object --path <LIGHT> --object "10P/Tempel 2" --expect "10p Temepl" --apply
tianwen dataset tag-filter --path <LIGHT> --filter "IDAS LPS-D3" --apply
tianwen dataset tag-filter --path <FLAT>  --filter "IDAS LPS-D3" --apply
```

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
