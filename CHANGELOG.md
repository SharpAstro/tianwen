# Changelog

Release notes for TianWen, newest first, one section per `MAJOR.MINOR`.

The version NUMBER is not here. It is one line in `src/Directory.Build.props`
(`VersionMajorMinor`), and CI's `version` job reads that property back rather than restating it, so a
build can never declare a version this file disagrees with. Bump it there and add the entry here, in
the same commit.

The patch segment is CI's `github.run_number` and is never written by hand, which is why the tags
read `v7.0.1532` while the sections below read `7.0`. Per the org convention in the SharpAstro
`.github` repo, `X.Y` to `X.(Y+1)` is additive and `X.*` to `(X+1).0` is breaking.

Each GitHub Release is cut with `generate_release_notes: true`, so it already lists the commits.
This file is the half a commit list cannot write: what a release was FOR, and what breaks.

Entries before 7.1 were reconstructed from the release commits and the tags rather than written at
the time, so they record what each bump was for. Where a bump commit said nothing beyond the number,
the entry says so instead of inventing a theme. The date on each is its release tag's; a new entry
carries none, because the tag will.

**The history was rewritten once, during 4.2** (the LFS migration of 2026-04-22, between CI runs 601
and 611), and **releases before 4.2 no longer exist on GitHub.** Until 2026-09-06 the seven release
tags from `v3.0.440` through `v4.0.564`, plus 71 orphan tags, still pointed into the pre-migration
history, which kept 594 MB of superseded catalog and fixture blobs (the Tycho-2 binary in four
encodings, the `.fits.gz` fixtures) in every fresh clone: a 632 MB pack for a 48 MB repository.
Those seven releases and their binaries were deleted (four had never been downloaded; the others had
12 to 28 lifetime downloads, all from their first weeks), every orphan tag was removed, and the seven
release tags were re-created on `main` at the commits with the same subjects, so the entries below
stay, their dates stay verifiable, and `git log v3.6.493..v4.0.564` answers the same 200 commits it
always did. Hashes quoted in the docs from before the migration were re-pointed the same way. Any
other commit hash from before 2026-04-22 no longer resolves anywhere.

## 9.0

Breaking, so a major. The geometry types in `TianWen.Lib`'s public API are its own now, the
dependency floor moves two majors, a filter name the library does not recognise stops comparing
equal to `Filter.Unknown`, and the hourly forecast record carries the seeing estimate's four inputs.
Three further changes alter what a dataset bake produces from the same archive, which no signature
announces.

**`System.Drawing.Rectangle` and `Point` are gone from the public API**, replaced by
`TianWen.Lib.Geometry.PixelRect` and `PixelPoint` in 24 public signatures, among them `Image.Crop`,
`Image.LargestCoveredRectangle`, `Image.SettledCoverageRectangle`, `Image.DebayerRegionIntoAsync` and
`CoverageEdgeWalk.Trim` / `Measure`. The assembly was never the GDI+ one, but the name carried a
drawing and Windows association the imaging and device layers do not want, and `Devices` had already
kept it out with its own `RoiRect`. `Right` and `Bottom` stay EXCLUSIVE, as the replaced type defines
them; the one inclusive convention is the FITS section cards, converted in `FitsSection` and nowhere
else. A caller swaps the type and changes nothing else.

**The dependency floor moves**: DIR.Lib 9.3 to 10.0 and FITS.Lib 5.0 to 6.0, both majors (FITS.Lib's
`PartialFitsReader.ReadRegion` takes four edges instead of a drawing rectangle, which is what let the
namespace go), with TianWen.DAL 2.1, QHYCCD.SDK 1.1 and ZWOptical.SDK 4.3. The DIR.Lib move landed on
`main` after 8.2's entry was written and so was published under 8.2 packages; Console.Lib 5.0,
SdlVulkan.Renderer 7.43 and WebGl.Renderer 1.33 moved with it. Within 9.0 the toolkit then moved
again, additively, to DIR.Lib 10.2 (popover triggers and groups, tab item presses, layout scroll
containers, and a router that no longer blurs a field the same press's handler focused), with
Console.Lib 5.1, SdlVulkan.Renderer 7.44 and WebGl.Renderer 1.35 rebuilt against it.

**`Filter.FromName` keeps a name it does not recognise.** It returned `Filter.Unknown` and dropped the
text, and an unknown filter's identity IS that text, so every such filter built from a name (the
implicit string conversion, a manual filter wheel) was identical to no filter at all: the info panel
showed "Filter: Unknown" over every L-eNhance and LPS-D3 frame, and a manual wheel stored the literal
`Unknown` in its URI and lost the filter it was created for. Only the FITS reader put the text back,
by hand. It now returns an unknown filter carrying the text as `RawName`, which is also its
`IdentityKey`. A test of `filter == Filter.Unknown` no longer
catches it: ask `filter.IsUnknown`. A blank or whitespace name still returns `Filter.Unknown` itself.

**`HourlyWeatherForecast` gained four fields, so its constructor changed.** The seeing estimate reads
the wind at 250, 500 and 850 hPa (#305, published under 8.2 after its entry was written) and records
the boundary-layer "mixing height" beside them; all four are trailing optional parameters of the public
positional record, NaN where a provider has none. A caller that names or omits them compiles
unchanged, but one compiled against 8.2 binds a constructor that no longer exists, and the record's
positional deconstruction takes four more out-parameters. A forecast cache written before them reads
them back as NaN, not zero: the primary constructor is now `[JsonConstructor]`, since zero jet wind is
the best seeing class there is.

**A flat whose headers cannot prove the optical train is used only within 14 days of the lights.**
`CalibrationResolver` read a missing `TELESCOP` or `FOCALLEN` as a wildcard, and SharpCap writes
neither on a flat, so a flat from one train could be chosen for lights shot through another on the
same camera. Flats now rank by filter first in three levels (same filter, one side unstated, both
stated and different), then a train the cards prove ahead of one only the date admits, then the old
score or the distance in days; `calibration-coverage.tsv` gains a `flat_train_proof` column saying
which. The window is the archive's own distances: a session's own flats sit 0.03 to 1.97 days from
its lights, a SharpCap campaign's shared set up to 8.9, and the nearest set from another train 17.5.
On the reference archive 19 of 95 sessions changed flat, all of the SharpCap era, and one now has
none.

**An unnormalised drizzle shifts every frame's sky onto the session's.** The dataset bake integrates
with normalisation off so its masters stay on the subs' linear scale, and drizzle's uneven weights
over the four Bayer phases turned sky drift into a fixed 2x2 level pattern: a median 0.28 sigma over
the 2026-09-16 bake's 57 drizzled masters, 8.9 at worst. `IntegrationOptions.DrizzleSkyReference`
(opt-in, ignored when normalising) moves each frame's per-colour sky onto one reference without
touching its scale, and `SessionRegistrar` hands the median sky of the session's subs to the master
and both halves. On Statue of Liberty the worst term went from 8.88 sigma to 0.06 and blue's
background sigma from 7.1e-4 to 1.8e-4, with the sky level within 7 percent of before. A drizzled
master baked earlier keeps the pattern until it is re-baked.

**A hot-pixel mask is measured per Bayer position, and a degenerate one is refused.** A CFA dark is
one channel holding four interleaved populations that need not share a level, and `BadPixelDetection`
sampled it at stride 8: an EVEN stride lands on (even, even) everywhere, so the noise scale came from
one photosite colour and was applied to the other three. On the eta Carinae ASI294MC dark the four
colours sit at R 540, G 520, G 520, B 621 ADU, because a non-neutral in-camera white balance was left
on and a ZWO applies it as a digital gain that scales the pedestal too. The sample was the red plane,
its MAD collapsed to the 4.0 ADU fallback, and the sigma-8 threshold landed at 587.4432, below blue's
own floor: 100.000 percent of the blue photosites were flagged hot, drizzle deposited nothing into
that plane, and the master was written with an all-NaN blue channel while the session reported
success. Each Bayer position now gets its own median and MAD (0.278 percent masked for blue on that
dark, 0.33 to 0.89 across the four), the sampling stride is odd so an undeclared mosaic cannot
phase-lock either, and the runaway guard that bounded the estimation loop now also covers the chosen
threshold, which is the step that writes the mask: a count past it masks nothing and logs why.
`IntegratedMaster.Labelled` refuses a master with a channel holding no finite pixel at all, so any
other route to an empty plane fails the session instead of writing it. One master of the reference
archive's 92 was affected; masks change slightly for every CFA dark, so a bake re-run will differ.

**Published under 8.2 after its entry was written, and recorded here (#292).** With normalisation on,
drizzle normalises each frame per CFA colour, closing a phase-locked colour bias and the column
stripe a whole-frame scalar left behind. Every integration strategy writes its master in [0, 1],
labelled with its observed peak as `MaxValue`, no `SensorFullScaleAdu`, and the pedestal and black
point the normalisation actually left; a master written before claimed `DATAMAX = 1` over pixels up
to 62.

**`tianwen image sharpen` runs the canonical program, which changes what it produces.**
`SharpenRequest.Canonical` and `DeblurFirst` are the single source of truth for the enhance step
order, and the FITS viewer's Enhance button and `tianwen stack --enhance` both take their program
from there. This verb did not: it assembled its own list starting at `RemoveStarsStep`, so it ran
neither the whole-frame deblur nor the gradient correction the other two have always run, and the
same master enhanced in the viewer and on the command line came out visibly different, with an
uncorrected background and its colour cast intact. It now takes the head of the canonical program:
`DeblurStep` when a deblurrer is live, then `GradientCorrectionStep`, before the star split; and with
BlurX live the starless deconvolution is dropped, as `DeblurFirst` drops it, since the whole frame
has already been deconvolved. An explicit `--deconv-blend` still asks for it and `--no-gradient` opts
out. A script pinned to the old output will see a different picture, deliberately: measured on the
10P/Tempel master the background's peak-to-peak per channel goes from 3.43 / 2.77 / 2.77 percent to
0.25 / 0.19 / 0.18, with the three channel backgrounds landing on one level.

**Additive CLI verbs**: `image autocrop` crops a master to the rectangle its subs covered, calling the
same `ViewerActions.ScanForCrop` the viewer's auto-crop uses (the drizzle weight plane from the
`.rejection.fits` sidecar where one exists, the coverage edge walk otherwise) and holding no logic of
its own; its `--margin` insets every edge by a fraction of the axis for a caller whose crop feeds a
background model rather than an eye, because the walk REFUSES an edge whose band never settles and a
refusal keeps the ramp for the model to fit. `image deblur` and `image denoise` expose
`IImageDeblurrer` and `IDenoiseEnhancer` as verbs of their own, as `remove-stars` and `flatten`
already did for their roles; both are absent rather than fatal on a host with no backend for the role.

**Additive**: `ImageMeta.DataSection` and `BiasSection`, read and written as `DATASEC` / `BIASSEC`
(`TRIMSEC` stands in only where `DATASEC` is absent); `ImageMeta.FrameSequence` with its counter
source (`FRAMESEQ`, `SEQSRC`), counted per connect by the DAL camera driver; the sensor geometry DAL
2.1 exposes, declared on full-frame unbinned captures only; `FitsHeaderEditor.SetFrameTypeAsync` and
the `dataset tag-frame-type` and `relabel-frame-type` verbs; `CalibrationProvenance` on the PSF
store's session record; `--hold-out-session` and a `dataset-parameters.json` for standing bake
decisions.

**Additive, planner**: a "Seeing" row in the weather band, a class from 1 to 5 estimated from the
jet-stream wind (a forecast, never written to `StarFWHM` and never read by the session), and the
mixing height in the band's tooltip. An OpenWeatherMap profile, which has no upper-air field, gets
both from keyless Open-Meteo through `IWeatherDriver.GetHourlyForecastWithUpperAirAsync`, which fills
only the fields the provider lacks.

**Additive, planner: the night calendar.** The GUI's status-bar date opens a month of nights, each
with its Moon, its forecast where there is one, and one verdict: Go, Marginal or No-go from the clear
dark hours inside the forecast horizon, Dark or Moonlit from the Moon alone past it. A click plans that
night, and the planned night's verdict sits beside the date. The pure halves are public API:
`NightSummary.Compute`, `NightVerdict.For`, and `IWeatherDriver.GetExtendedHourlyForecastAsync`, whose
`ExtendedForecast` is the profile's own provider for every hour it covers and Open-Meteo after it, with
the split named. The planner's weather band is now a slice of that one multi-day forecast, so stepping
the date through the next two weeks asks the network nothing, and a night past Open-Meteo's horizon
asks for nothing where it used to fail with a 400.

**Open-Meteo reads a null in any hourly array as that hour unknown.** Only the upper-air arrays were
nullable, so a null anywhere else failed the whole response. A single night never met one; a 16-day
request always does, at its far end (`cloud_cover[403]` on the calendar's first live request, which
lost all 408 hours to it). The weather band also stops drawing an hour with no cloud value as a clear
sky.

**Additive, planner: the pinned targets on each night.** A strip along the foot of each calendar
cell shows when the pinned pointings are up that night, dusk to dawn, clear or cloudy by the verdict's
own rule, and the detail strip adds a line per pointing: its window, its hours up and clear, and the
Moon's closest approach. The verdict stays about the sky and does not read the pins. The computation
is public: `NightPins.Compute`, with a planet or comet placed on each night by its catalogue index.

**Additive: the night calendar in the TUI and in the browser.** The same widget, not a copy: the TUI
draws it over the planner chart on its Sixel canvas, with the planned night and its verdict on the
top bar. The web draws it on the canvas under a new date control in the toolbar. Every calendar now
takes keys: the arrows move a cursor, PageUp and PageDown a month, Enter plans the night and T plans
tonight. The web build gains weather for the first time, from keyless Open-Meteo, so its planner chart
now has the weather band. `NightCalendarActions.RefreshAsync` and `EnsureMonth` take a site
`Transform` and a weather URI for a host with no profile.

**Device behaviour**: a QHY guide pulse now runs for the duration asked where the device can time its
own, where it was a fixed 50 seconds nothing could stop; a QHY cooler with nothing engaged reports its
setpoint as `NaN` rather than a plausible -100; the QHY DDR frame buffer is enabled on every connect,
since it reads off on each freshly opened handle.

**Every headless render now obeys four rules the viewer already had, and the pictures change.**
`MasterPreviewRenderer` (the `master_*.png` companion of `tianwen stack`, `image render`, the dataset
gallery) resolved its stretch mode from a literal `StretchMode.Linked` where the viewer resolves through
`StretchModeExtensions.ResolveAuto`, so the line-selective veto measured over all 183 shipped filter
curves applied on screen and nowhere else: a 3 nm L-Ultimate master's SPCC triple (a fit against a
continuum that never reached the sensor, `1.136 / 1.000 / 1.860` on one) was asserted as colour through
one shared shadow point, and red clipped to zero once the enhance had shrunk the noise. Five of 139
gallery cards, every one that filter, rendered teal; each such master renders Unlinked now, with the
triple still printed. The coverage-plane crop tier lacked the border-reachability rule the pixel tier
gained in 8.0, so a saturated core whose samples rejection had dropped (the Trapezium, 35 blocks of
35,910) read as an uncovered edge and cut the Great Orion master to 51.8 percent of a canvas whose
blocks pass at 97.3; `FloodFromBorder` is the one method both tiers call, and that crop is 96.8
percent. Interior NaN (a drizzle master's rejection voids) reached the PNG as a blue-and-yellow speck
on the brightest part of the picture; the display render fills them from their neighbours as the
viewer's document open does (`Image.WithInteriorHolesFilled`, a copy, the linear master untouched).
And `SharpenPipeline` filled the same voids with each channel's frame MEAN before enhancing, a third
implementation of the fill and the wrong one: red's hole got the sky beside green's real value, and
the enhanced view of that card carried the speck after the render fix had cleared the raw one. The
enhance boundary takes the neighbours now and keeps the mean for the border ring alone. Also passed
through: the fourth resolver input, whether a frame's backgrounds are already level, measured by
`StretchSolver.ChannelsAgree` for both renderers instead of by the viewer alone.

**The GraXpert gradient corrector preserves each plane's own level, and both correctors restore the
model's median.** `OnnxBackgroundExtractor` added back one mean over all three channels, which lands
every channel on that value: background neutralisation, not level preservation, and it silently
equalised the colour of every OSC master that went through an enhance with GraXpert installed. On
the SV605CC Small Magellanic Cloud master (channel medians genuinely `R/G 0.332`, `B/G 0.552`) the
output read `1.004 / 1.003`; it reads `0.331 / 0.553` now. The level restored is each plane's MEDIAN
of the model, the statistic `ClassicalBackgroundExtractor` has always restored, so which corrector a
machine has installed no longer decides where its sky lands. Every enhance output through GraXpert
changes; the bake's retained linear masters, tiles and training corpus never ran the enhance and are
untouched.

**The linear enhance step order has one declaration.** `LinearEnhanceProgram.For(supportsDeblur)` is
the program `SharpenRequest.Canonical`, `SharpenRequest.DeblurFirst`, `tianwen stack --enhance`, the
hosted enhance endpoint and `tianwen image sharpen` all read; callers vary which steps are present and
at what blend, never their sequence. The `image sharpen` correction above was the symptom of four
hand-written copies drifting.

**Additive**: `tianwen image render --white-balance R,G,B` renders on a given triple instead of
solving one, and the verb prints the triple it used in the same form, so a render of an enhanced
master can inherit the balance solved on the unenhanced one rather than re-fitting against a
background the enhance has flattened (`MasterPreviewRenderer` always took the override;
`MasterPostProcessor` has always shared one solve across the split-plate TIFFs this way);
`tianwen dataset masters --store <bake> --out <json>` lists a bake's retained masters by the rules
that made the files (sidecar, pier side, the stats join, a real plate solution, and where the sky is
through `Image.FindBackgroundRegion`); the bake prints where each session's frames went on the
console and records every dropped sub with the stage that dropped it (`SessionPsf.DroppedSubs`);
`Image.Clone`, `Image.WithPedestal`, a per-channel `Image.Subtract`, and `StretchSolver.ChannelsAgree`.
The dataset gallery tooling under `tools/dataset-gallery/` is not shipped and is documented in its
skill.

**A staged master carries a coverage plane too, so its crop is exact.** Only drizzle wrote one (its
weight, in the `.rejection.fits` slot); every other strategy's sidecar was a rejection fraction, which
nothing reads and the exact crop tier cannot use, so every consumer of a staged master fell to the
edge walk, which estimates the border and refuses an edge whose band never settles. On the V1045 Ori
master a dither strip 350 px wide at 2.7x the noise ran down the left, the walk declined it, the
fallback trimmed 264 px and the strip stayed. `IntegrationResult.Coverage` (frames with a finite
sample per pixel, counted where the streaming integrator already reads every sample) is written as
`<stem>.coverage.fits` with `MAPKIND=COVERAGE` beside the fraction, `IntegrationFitsWriter.TryReadCoverageMap`
takes whichever sidecar says coverage, and master listings and the ingest skip exclude the new
suffix. A master baked before this keeps the estimated crop until it is re-baked.

## 8.2

The sibling pins move as a family, and a flat pixel with no throughput stops being multiplied by a
million.

**A dead flat pixel yields absence, not a number six orders too big.** `Calibrator` divided by
`max(flat, FlatEpsilon)` with an epsilon of 1e-6, and that clamp does not prevent a division by
zero: it converts one into a multiplication by a million and returns a finite value nothing
downstream can tell from data. Measured in the wild on `Eta Car SII NB / QHYCCD / 2024-03-02`, whose
overscan columns are exactly 0 in the flat, the integrated master peaks at 2.46e8 against a sky of
213, and 245,827,712 x 1e-6 = 245.83 is precisely the ADU value that went in. Those pixels rode
through warp, staging and the mean into the master.

`FlatEpsilon` keeps its name and changes both its meaning and its default, to **0.02**: it is now
the throughput below which a flat pixel carries no calibration, and such a pixel is marked
`NaN`. That is the vocabulary the rest of the pipeline already has for absence, and
`LargestCoveredRectangle`, `MeanCombiner` and `FillInteriorHolesInPlace` each do the right thing
with it where none of them could do anything with the old number. The threshold was measured, not
chosen: across the 18 master flats of the reference bake, 15 carry no pixel at all below 0.3 of the
mean and the three that do have populations complete by 0.02, so the dead pixels and the shallowest
real vignette sit either side of an empty band 15x wide.

**This changes OUTPUT for existing inputs**, which no API-compatibility check can catch: a caller
relying on the clamp now gets `NaN`, and a master rebuilt after this differs from one built before.
Only 3 of the bake's 18 flats contain a pixel the new floor touches, so a session calibrated with
any of the other 15 is byte-identical and needs no re-bake.

**The pins move together** (`DIR.Lib` 9.2 to 9.3, `Console.Lib` 4.35 to 4.36, `SdlVulkan.Renderer`
7.38 to 7.40, `WebGl.Renderer` 1.29 to 1.30), which the 9.1 `MouseUp`/`MouseMove` release made
mandatory, and `Directory.Packages.props` moves from `src/` to the repo root so `tools/` comes under
CPM as well. A split pin in `tools/lavapipe-repro` had been invisible to the sibling sweep for the
third time in this repo's history; a version the sweep cannot see is a version that drifts.

## 8.1

Additive: one new method on `Image`, a fix to what the auto-crop calls absence, and a viewer control
that stops claiming to be something it is not.

**The viewer's `HDR` button was never HDR, and now says so.** It applies a soft knee AFTER the MTF
and INSIDE [0, 1] (`Image.ApplyHdr`), so it compresses highlights into SDR white and never asks the
panel for a nit above it. Useful, and misnamed: someone with an HDR display reads that label as a
promise about their panel. It has folded with `Boost` into one `Tone` popover on the terms
`Calibrate` and `SPCC` folded into the white-balance one -- the boost and its curve mode, the soft
clip's amount and knee as continuous dials, and a greyed block naming the display HDR the viewer
cannot do, with the reason beside it. Two toolbar buttons become one; `ToolbarAction.CurvesBoost` and
`ToolbarAction.Hdr` are gone, replaced by `ToolbarAction.Tone`. `B` and `H` keep their ladders and
the wheel over the button keeps the soft clip's; the boost loses its wheel with its button, because a
popover has two dials and a wheel has one axis. The MATH is untouched: the shader, `Image.ApplyHdr`
and every stretch test are byte-for-byte what they were. `docs/plans/hdr-display.md` P1.

**An interior drizzle hole is not a canvas ring (#250).** `Image.LargestCoveredRectangle()` treated
any NaN as absence on the reasoning that NaN is "unambiguous". It is unambiguous about the PIXEL and
says nothing about WHY, which is the only question being asked, and the exempt case was the common
one: 53 of 79 masters in one bake carry interior NaN, every `BayerDrizzle` one, and 1,856 of them on
a 3024 x 3025 master took a 99.94 percent covered frame to 0.528 of its canvas, because a largest
RECTANGLE has to thread between islands. Absence is now border-reachable for NaN exactly as it already
was for zero: a ring touches the border by construction, an island never does. The two are still
recognised differently (zero in EVERY channel, NaN in ANY) and prove the same thing.

**`Image.FillInteriorHolesInPlace`** (new, the additive half) interpolates each interior NaN from its
measured 8-neighbours, per channel, closing a hole from its rim inward one pixel per pass, and never
touches the ring, which is the only evidence the crop works from. `AstroImageDocument` calls it at
load before any statistic is taken and reports the count as `InteriorHolesFilled`, because a viewer
that silently invents pixels is one you cannot trust a measurement from. A frame with no NaN costs one
classify pass and nothing else.

**The absence flood runs 64 columns at a time.** Both halves come out of one walk of the pixels, the
planes are `BitMatrix`, and the border flood is a word AND plus a word OR across rows and a
Kogge-Stone occluded fill along them, six shifts per word with one carry bit crossing each word
boundary: 29 ms to 6.3 ms on that master, the whole crop 92 ms to 67 ms. `BitMatrix` gained a flat
backing and a word surface (`RowWords`, `AllWords`, `PopCount`, `Any`, `NextSetBit`, `ClearPadding`);
its existing API is unchanged. Two things were measured and NOT done: bit-packing the per-row scratch
(28 ms to 72 ms, it is in L1 either way) and blocking the classify loop by word (22 ms worse, it puts
plane residency resolution in the inner loop). The flood is pinned against a per-pixel reference at
widths 63/64/65/127/128/129, because a frame wider than that passes with either carry deleted.

## 8.0

Breaking, so a major. Four things break, three of them narrow and one of them the whole toolkit
underneath.

**`Image.GetLumaStretchStatsAsync` lost its `debayerAlgorithm` parameter.** It materialised a full
debayer on an RGGB frame to reach the Rec. 709 path, three scalars bought with an interpolated
three-plane copy, some 288 MB on a 6000x4000 OSC sub. It takes the statistic in place now, as the
document already did, and the parameter went with the debayer rather than staying a knob that selects
nothing. Nothing in this repository was paying that cost (the document passes the mosaic case itself,
and the test harness guards on `ChannelCount >= 3`, which a mosaic is not), so the removal is visible
to outside callers only.

**`StretchParameters.Presets` is an `ImmutableArray<StretchParameters>`, and `Default` is a property
over `Presets[0]` rather than a field.** A plain array is readonly only in its reference: any caller
could rewrite element zero and move `Default`, which reads it on every call, for the whole process.
Indexing and iteration are unchanged; assigning the array to a `StretchParameters[]` is not, and the
field-to-property change is binary-breaking even where the source still reads the same.

**`Image.FindStarsAsync` takes a `SpikeGuard` before its `CancellationToken`.** A call passing the
token positionally in the last slot no longer binds; named or defaulted calls are unaffected. The
guard is the new one described below, and it is defaulted on, which is itself the behavioural half of
this break.

**The dependency floor moves to DIR.Lib 9.0**, whose `DesignScale` is a per-axis pair where the
pre-layout scale was a bare float. `TianWen.Lib` carries DIR.Lib for `TiffWriter` and `PngWriter` on
the export path, so a consumer pinned to DIR.Lib 8.x cannot take TianWen.Lib 8.0 without moving too.
The rest of the sibling set moves with it: SdlVulkan.Renderer 7.37, Console.Lib 4.33,
SharpAstro.AppShell 1.1 and the codec family at 3.14.

**Three things RENDER differently, which is the half of a major that no signature announces.**

The default auto-stretch is an inspection stretch, N.I.N.A.'s per-light preview exactly: the sky
lands at 0.2 rather than 0.1, and a pixel three MAD above it separates by more than 0.08 where the
old default gave 0.036. That is why a target could be barely visible here while N.I.N.A. showed it
plainly on the same sub, and the viewer's 150 percent Boost could not make up the difference, 0.1
times 1.5 still being under 0.2 before contrast enters. Every preview, thumbnail and TUI render moves
with it; `StretchDefaultInspectionTests` pins the rendered result rather than the parameters, since
what went wrong was never that the numbers looked unusual.

Every VNG demosaic changes. A gradient in a demosaic compares two samples of the SAME colour, so it
is zero on a flat field whatever the sky's colour is, and VNG's were colour differences that were
also an affine function of the value they select: "keep the smallest gradient" meant "keep the value
nearest the centre pixel's own level", so green read about +99 ADU high at blue sites and correctly
at red ones. Blue sites are alternate rows AND columns, so it reached the screen as a two-pixel
alternation on both axes, fine stripes over the whole background at 1:1, 6.4 display levels against
12 of pixel noise and thirty times what MHC or AHD show on the same frame. It was invisible to every
test the suite had, because a demosaic had only ever been checked against ITSELF or against the GPU,
which mirrors the same mistake; `VngFlatFieldBiasTests` asserts on a flat field, the one input where
a bias has nowhere to hide.

A Bayer mosaic's statistics are three colours, taken on the mosaic in place, wherever they are taken
-- the document, the live preview and the masked walk. `Statistics`, `Histogram` and
`GetPedestralMedianAndMADScaledToUnit` gained a defaulted `CfaChannel? cfa`, and
`StretchSolver.CollectPerChannelStats` a defaulted `pixelStride`, all additive. The masked case was
the one still wrong at the end of the cycle: without a `cfa` the walk is a fixed grid from (0, 0) and
any EVEN `pixelStride` keeps both parities, so the default 4 never left the phase it started on and
`StarMaskedLumaStats` measured whichever photosite the origin landed on while claiming the whole
mosaic. The even-stride rule is now stated on the API rather than left for the next caller to find.

**A star detector's spike guard** (`SpikeGuard.PeakShare`, default on): a detection carrying over 85
percent of its background-subtracted 3 by 3 flux in one photosite is not a star. A single warm
photosite on an OSC mosaic becomes a 2 by 2 blob under the detector's mono fold and passed the size
floor, so on a night whose lights kept residual warm pixels half of every star list was them, and
every median over it read their width.

**The viewer is where most of the release went.** The sky behind the frame left the annotation ladder
for its own button and key (`Y`): it reads as one more rung of the same idea and is not, since it
puts a second view BEHIND the photograph, takes the pan clamp off and raises its own layer palette,
and riding the ladder cost it three ways, each now pinned. A click in the file list is its own
auto-stretch, and carries the run's calibration, with `Ctrl+H` also carrying the stretch and a blink
reduced to a click within one target. The white balance and the colour calibration moved into one
popover under a mark-only toolbar button (`W`). Below 100 percent the colour demosaic runs at the
screen's resolution, so an OSC frame stops aliasing at fit zoom. The live preview draws a histogram,
solved from a mosaic's three colours as a document's is, with one collector deciding what a channel
is. `Shift+H` steps HDR back, the held frame carries an `[H]` in the file list, and `D` no longer
cycles a demosaic a mono frame has no use for. `CycleStretchPreset` reconciles against the parameters
in hand instead of stepping off a stored index that went stale whenever anything else set them, so
the first press stops appearing to skip.

**Source detection lands in `TianWen.Lib`**: `BackgroundMap` and `SourceSegmentation`, the
`Background2D` and `detect_sources` shapes, with `EdgeSpreadProfile` reading an extended segment's
boundary as a line-spread function. The crowded-field rule was measured on the Statue master -- a
large segment with many maxima is a field, not a source, so the detection re-thresholds a sigma
higher and says which sigma it was detected at: 49,614 segments in 25 s with the field-sized blobs
gone. `tianwen image sources` writes the detection to disk as `MAPKIND`-stamped sidecars with a
segment table.

**Deconvolution P2 E3** shipped the unrolled Richardson-Lucy operator, its noise-free control and the
residual prior (#241), on the calibration, warp default and degradation cache of #227.

**The rest.** The catalog pins OpenNGC to v20260501 with both snapshots re-baked. A thumbnail handler
reads nothing until asked and declines a fast extraction, so Explorer asking whether thumbnails exist
no longer hydrates a folder of cloud placeholders; `0x8004B205` is recorded as EXTRACTIONPENDING
rather than a failure, and OneDrive's own whole-root provider is documented as the reason our handler
is never asked inside a sync root. The macOS lane signs every file in `Contents/MacOS`, not only the
Mach-Os, which is what two release runs died on. APP's `AD-PED` is read as the pedestal where
`PEDESTAL` is absent. MCP SDK 2.2.0.

## 7.1

Additive, so a minor: every public member that existed at 7.0 survives (the only public line the
diff removes is `HfdPsfEstimator`'s old one-parameter constructor, replaced by the wider one), and
the new constructor parameters are all defaulted, so `new HfdPsfEstimator()` still compiles.

**`RichardsonLucy` (`TianWen.Lib/Imaging/Deconvolution`)** is the known-kernel maximum-likelihood
iteration, shipped as library surface rather than as a training script, because the deconvolution
work needs an oracle that anything trained can be scored against. It was validated against answers
known in advance before it was used to judge anything: a ceiling measured with a broken instrument
is worse than no ceiling, since every arm would be scored against it and the error would never
surface. It recovers 100, 97 and 86 percent of a known blur on noise-free synthetic stars given the
exact kernel, conserves interior flux to 0.000 percent, is inert to 4e-4 under a near-delta kernel,
and reports rather than absorbs a negative input, since the update is multiplicative and a negative
sample flips the sign of its own correction.

**`PsfKernel.Mirrored()`** is the adjoint the correction pass convolves with. It equals the original
for every kernel this family can build, and is built unconditionally rather than short-circuited, so
that a future asymmetric kernel turns it into a real correction instead of silently leaving every
deconvolution running the wrong operator. A `[Theory]` pins that equality.

**The measured oracle ceiling**, which is the number any trained deconvolver may be held to, at 60
Richardson-Lucy iterations with the exact kernel over 180 rows of real masters: full recovery to 1.3x
blur (residual 0.00 px), the recovered star 1.01 to 1.02 times the truth through 1.6x and 1.09 to
1.14 at 1.6 to 2.0x, so "within 10 percent up to about 2x" holds; past 2x the star is about 1.6x the
truth, most of the blur still present. A first table at 30 iterations read 1.6x as the boundary and
was under-converged by roughly three in residual; a too-low ceiling flatters every arm scored against
it, so it was re-measured rather than annotated. Nothing recovers below the truth (rec/truth at or
above 0.99 in every band), which is the line a trained arm may not cross, and ringing arrives before
recovery degrades (15 percent of stars at 1.1 to 1.3x while the residual is still 0.00 px), so the
usable range is set by the ringing column, not the width.

**`HfdPsfEstimator` takes its radius range at construction**, and TianWen's own measured contract
lands beside it as `TianWenMinRadiusPx` / `TianWenMaxRadiusPx` ([0.5, 4.0] px). **The default is
deliberately NOT changed**: it stays SAS's [1, 8], because the range belongs to the MODEL, and the
shipped `OnnxNonStellarDeconvolver` runs SAS AI4. A graph trained on [1, 8] handed a number encoded
over [0.5, 4] does not fail. It runs, and is quietly told a PSF about twice the one it was given. A
test pins that the container still resolves the shipped range, which is exactly what the new
optional constructor parameters could otherwise break in silence.

The encoding arithmetic behind that choice is worth stating, because the intuitive move is
backwards. `psf01` is a ratio of logs, so widening the range divides every difference by its span:
lowering the floor makes the spread SMALLER. [0.5, 4] and [1, 8] are both 8:1 and resolve
identically; the whole measured gain is unclamping the six of 79 masters that were pinned to SAS's
floor. The span sets the resolution, the window's position decides what clamps, and buying real
resolution means narrowing the span at the cost of clamping one end.

**`DatasetDegradationExporter`** gains `MaxBlurRatio`, `PerChannelKernels`, and psf01 labelling on
every blur row: `Psf01Estimated`, read off the DEGRADED cell, beside the training-only
`Psf01FromKernel`. Two rules the exporter enforces. The label is measured on the LINEAR cell,
because `OnnxNonStellarDeconvolver` measures it there too, before the runner stretches, and a tone
curve moves a star's half-maximum crossing. And a psf01 is never written without stars behind it:
the estimator falls back to a constant default radius when it finds none, so the exporter writes
null instead and a consumer drops the row, rather than conditioning a model on a number nothing
measured.

**The ceiling with an ESTIMATED kernel** (E1b), measured with the same probe: where the kernel is read
off the blurred frame's own stars, the recovered width stays within 0.01 to 0.05 of the exact-kernel
result everywhere but the 1.3 to 1.6x band, where the difference width is over-read by a quarter and
the width-only estimate over-deconvolves into fabrication (15 percent more stars than the truth has).
The number that decides the next step is that `PsfProfileFit` declined 75 of 180 rows, half of the
noisy ones, most of them narrowband masters: at deployment depth the estimator answers about half the
time, so a kernel estimator becomes its own step before any unrolled Richardson-Lucy is built.

**`DatasetPsfNoiseReport.SessionPsf` gains a per-sub identity**: `SubFile`, `SubEpochUtc`,
`SubAirmass` and `SubHeaderAirmass`, aligned index for index with the existing `SubFwhm`, plus
`SubSelection` naming which subs the arrays describe. Records written before this read back with the
new columns null; `SubIdentity` carries them through a master re-measure, which cannot recover them
(a master does not say which frames made it). **`tianwen dataset build --remeasure-subs`**
(`DatasetBuildOptions.RemeasureSubs`) re-runs the measure stage alone over every recorded session
and rewrites the sub arrays with the identity, about two minutes a session against ten for the
re-registration `--force-psf` falls back to; the two are refused together and run as two passes,
subs first. The store's JSON context now allows NaN, which the per-sub air mass is for every light
whose header has no site.

**`SiteContext.Airmass` and `SiteContext.AirmassFromAltitude`** are the one air-mass computation;
the session's sky gauge and the gradient report forward to it. The per-sub form answers NaN for a
target below the horizon rather than a clamped value that would read as a deep observation.
**`ImageMeta.Airmass`** reads the capture software's `AIRMASS` card, recorded beside the computed
value as a cross-check and never substituted for it. The archive-wide measurement it was built for
(E2.9) then found that computed and card agree to a median 0.015 on 50 of the 52 sessions carrying
both, and that every SharpCap capture writes no `SITELAT`/`SITELONG` at all (27 of 79 sessions have
no computed value); `tools/psf-airmass-report.py --airmass either` uses the card where the
computation has no site, labelled as such, on the strength of that agreement. The FWHM half of that
measurement is withdrawn: `SessionPsf.SubFwhm` is the registration detector's width on the
pre-debayer mosaic, which reads 1.70 px on every sub of an OSC night whatever the seeing (the
debayered green plane puts the same subs at 1.7 to 2.5), so it ranks frames on a floor, not on
seeing, and a per-sub width that reads seeing is owed before the question is asked again.

**`MoffatComposition` (`TianWen.Lib/Imaging/Degradation`)** is the width of one Moffat convolved
with another and its inverse, the kernel that takes a clean profile to an observed one. It exists
because a quadrature of FWHMs is the Gaussian rule and a Moffat is not a Gaussian: read as an
estimator does, `sqrt(obs^2 - clean^2)` over-reads a difference kernel by 1.11 to 1.24 on the
archive's cores (measured numerically, then found as the 1.24 the estimated-kernel oracle showed at
1.3 to 1.6x blur), and the composed inverse lands within 0.02 of the truth at every band from 1.3x up.
It also composes a continuous core with a `PsfKernel` **as sampled**, and inverts that into the
kernel's `EffectiveKernelFwhm`, because `PsfKernel` samples its profile at pixel centres and a
kernel narrower than about 1.5 px does not blur by its label: a nominal 1 px beta-4 Moffat widens a
2.15 px core as a 0.73 px continuous one would, and a nominal 0.5 px kernel is a near-delta. Nothing
downstream had noticed, since the training label is measured on the degraded cell; the oracle probe
had, reading a correct estimate as a 0.65 under-read against a width the kernel never applied. The
exporter's training-only `Psf01FromKernel` now composes the clean width with the kernel applied
instead of a quadrature with the drawn width. And the exporter's blur draw is a RATIO of the cell's
own width (`MinBlurRatio`, `--min-blur-ratio`, default 1.05, up to `MaxBlurRatio`), with the nominal
kernel width solved by bisection so the kernel as sampled realises it (`SolveNominalFwhmForRatio`;
realised within a thousandth on every row of the test fixture, nominal 0.74 to 3.47 px); the row
records `BlurRatioDrawn` and `ComposedFwhmPx`, the realised width. A cell with no measured width, or
a ratio floor of 1.0 or less, keeps the pixel draw, and the pixel bounds still clamp the solved width.
A cache exported before this carries the lighter light end E1d found. Opt-in `EstimateKernels`
(`--estimate-kernels`, `--estimate-window`, default 1024 px) runs the estimator step on every draw
and writes its kernel on the row for the unrolled operator to train on: the profile fit by the signal
floor on the linear clean window (once per cell) and on the linear degraded window, the width by
Moffat composition and the shape from the degraded fit (`EstimatedKernelFwhmPx`,
`EstimatedKernelBeta`, `KernelSource` estimated or drawn, the refusal), beside the drawn kernel's
`EffectiveKernelFwhmPx`. A 256 px tile cannot support the fit (17 stars where it needs 40), which is
why the reading is over a window, as inference takes it over the whole image; on the Rosette session
47 of 48 rows estimate, within 3 to 8 percent of the effective width at the median from 1.3x blur up.

**`PsfProfileFit` says why it refuses, and can select its stack by an absolute floor.** A
`Diagnostics` overload returns which of the five checks declined (`Refusal`) with the counts at each
stage, after two years of a bare null; the first tally over 180 oracle rows put 28 of 30 whole-frame
refusals on ONE check (`PoorFit`) on RICH fields, because the default brightness band is a
percentile of the frame's own detections and on a field with 5,000 stars it stacks faint ones whose
wings meet the noise floor within a few pixels. `StarSelection.SignalFloor` (opt-in, the default
band is unchanged so the archive survey stays comparable) takes every star over fifty background
MADs, brightest first, and cut the refusals to 30 of 180 with the widths unchanged; the ones that
remain are sparse or heavily blurred frames with too few such stars, a different and honest refusal.

**`OnnxNonStellarDeconvolver(perChunkPsf:)`**, off by default, conditions each inference tile on
its own region's PSF instead of one frame-wide value: `ChunkedInference.Layout` is the tile grid
without the pixels, `ChunkedNafnetRunner` takes its extra inputs per chunk, `IPsfEstimator` gains an
`EstimateChunkAsync` overload carrying the whole-image value as the fallback, and `HfdPsfEstimator`
answers it under `MinChunkStars` (8) stars. Measured on the seven Rim masters, the per-tile radius
spans 45 to 61 percent from p10 to p90 on the 2025-26 sessions with the CENTRE the soft end, so one
value tells most tiles the wrong width; the switch stays off until the output comparison against the
shipped whole-image graph reads, because that graph was trained on whole-image labels, and a master
whose radii sit under the shipped range's 1 px floor clamps every tile to the same value and cannot
be told apart either way.

**`tianwen stack --group-temp-tolerance <C>`** (`StackingOptions.LightGroupTemperatureToleranceC`)
lets a night whose cooler drifted across a degree stack as ONE master. The light grouping reuses the
calibration key, whose temperature is rounded to the degree, so a real 13.7 to 12.1 C session came
out as three masters of 49, 18 and 4 frames with three reference frames, which nothing downstream
can recombine. With a tolerance, a target's frames are cut only where consecutive temperatures are
further apart than it (a drift is one cluster, a different night's 8 C another) and the group's dark
is matched at its median temperature. The default stays 0, grouping exactly as before, because the
same rounding is what pairs a group with a dark at its temperature. `LightGroupKey.Assign` is the
one implementation; five facts pin it. Found by E2.10, which needs one manifest for one night.

**Two processes of one app started within the same second no longer collide on the log file.**
`FileLoggerProvider` named its file to the second and truncated it, so the second process died at
start-up with "the process cannot access the file" before it had logged a line (a launcher that ran
`tianwen --version` and then `tianwen stack` lost the stack). The name is created fresh and gets the
process id appended only when it already exists, so every existing log name stays as it was.

**Every master now carries `CANVASX0` / `CANVASY0` / `REFFRAME`**: its pixel (0, 0) in the reference
frame's pixel space, and the reference frame's name. The canvas is the union of the registered frames'
footprints, so two masters built from one reference but different frame sets (a seeing split's sharp
and soft thirds, a layer and a re-run) differ in extent AND origin, and until now the origin was only
ever logged; two such masters could not be overlaid after the fact. `AlignmentProvenance` carries it,
an autocrop moves it by its offset (`ForCrop`), and a master written before the cards simply has none.
`--site lat,lon` on `tianwen dataset build` supplies an observing site for lights whose header has
none (SharpCap writes no site cards), used only where the header is silent and recorded on the
session (`SessionPsf.SubSiteFromFallback`), so the per-sub air mass exists for those sessions too.

**The registration's rigid refiner no longer averages sensor-fixed detections into a frame's shift.**
A residual warm pixel (a dark colder than its lights leaves them) sits at the same sensor position in
every frame and passes the star detector; the refiner paired each one with its own copy in the
reference inside its 5 px tolerance, and its least-squares fit landed between the stars and them. On a
night whose star lists were half warm pixels, every frame within 5 px of the reference was placed at
HALF its drift, reproduced to the hundredth of a pixel, and every stack of it sat at 2.7 px on green
from subs of 1.7 to 2.5, the blur growing with the frame count as if resampling cost it. A pair whose
raw positions coincide while the bulk affine moved the detection by more than 0.7 px is now dropped and
counted ("N unmoved dropped" in the register log; hundreds a frame say the calibration left the defects
in). A frame drifting under 0.7 px cannot be separated this way and keeps them, with a bias bounded by
half its own drift. Pinned by `RegistrationRefinerTests`; the measurement is in
`docs/plans/deconvolver-training.md` (E2.10a, "the third finding placed"). Masters stacked before this
from a well-guided night with residual warm pixels carry the blur, the retained dataset masters included.

**A single warm photosite on an OSC mosaic is no longer a star.** The detector measures an RGGB frame on
a mono fold of the mosaic, which turns one hot photosite into a 2 by 2 blob that passed the size floor,
so a residual warm pixel was a star to every consumer of the list: the quality gate's medians and the
reference pick, the PSF store's per-sub fit, the registration, the plate solver's candidates. On the
night above half of every list was warm pixels and every width medianed over it read theirs (1.70 px).
The guard reads the raw mosaic, where a star's flux is spread over its photosites and a warm pixel's is
not: a detection carrying over 85 percent of its background-subtracted 3 by 3 flux in one photosite is
dropped and logged (`Image.SinglePhotositeFractionMax`; measured 0.92 to 0.99 for the warm pixels
against 0.15 to 0.41 for stars, no overlap). On the real RGGB test frame the count moves by 1.2 percent
and the removed detections are the narrow spikes; on the Orion night the lists halve and the fit that
refused every sub is expected to run. Pinned by `StarDetectionWarmPixelTests`.

**`tianwen stack --warp-interpolation Bilinear|Lanczos3`** (`StackingOptions.WarpInterpolation`,
`Image.WarpToReferenceGridAsync` and `WarpRegionAsync` overloads). The kernel that places each frame on
the reference grid was bilinear and only bilinear, and it costs a star phase times one minus phase of a
pixel's variance per axis: measured star by star on a real night, a master is the mean of its warped
frames to 0.3 percent, and the frames at fractional shifts widen from their subs' 2.15 px to 2.4 to 2.7
while the frames at integer shifts keep 2.15; on a synthetic 2.15 px star a half-pixel shift adds 1.15
px of FWHM in quadrature under bilinear and none under Lanczos-3 (`WarpInterpolationTests`).
**Clamped Lanczos-3 (`WarpInterpolation.Lanczos3Clamped`: six taps an axis, normalised over the taps
present, exact at integer shifts, PixInsight's clamping rule) is the default**, for `tianwen stack`,
`tianwen dataset build` (which gained the same `--warp-interpolation` so a bake states its kernel and
`bake-provenance.json` records it) and the `Image.WarpToReferenceGridAsync` overloads that name no
kernel; `--warp-interpolation Bilinear` reproduces every master built before this release byte for
byte, and `Lanczos3` is the plain kernel R1 measured. Measured on the same night (R1 in
`docs/plans/deconvolver-training.md`): the six-frame master goes 2.39 to 2.15 px per star and the
whole night's 2.70 to 2.48, each still the mean of its frames, with no ringing on any of four measures
at 2 px seeing, which is the case for the default; it changes every master, which is why it was a
decision (2026-09-12) rather than a fix. **The clamp turned out to be load-bearing, not a nicety.**
Flipping the plain kernel on the synthetic RGGB fixture put a ring of 400 to 2000 ADU below a 1000 ADU
sky two pixels from a 15000 ADU star on seven of eight subs: a mono 2 px star does not ring (0.04
percent of its peak), but a debayered OSC plane samples it on a 2 px pitch, so per plane it is a spike
with 6 percent at the neighbours, and the sinc's negative lobes ring on a spike by construction (13
percent of the peak). That ring set the frame minimum, the SAS auto-detect's median-minus-minimum
statistic crossed 0.125, and the tile exporter wrote a LINEAR sub unstretched, the domain skew the
denoiser once hid for two weeks. The clamp is PCL's `LanczosInterpolation` rule verbatim (the
weighted samples split by sign, negative lobes attenuated smoothly above a threshold and dropped once
they outweigh the positive ones), at a threshold of **0.7, measured, not PixInsight's 0.3**: on a 2.12
px mono Gaussian at half phase 0.3 lifts every star's skirt by 0.73 px of second-moment FWHM in
quadrature (the half-maximum width is untouched at every threshold), the widening is gone from 0.6 up,
and the spike's ring holds at 0.8 percent through 0.7 before climbing (1.3 at 0.8, 3.9 at 0.9, 5.9
plain). On the fixture the clamped kernel takes every sub's gate statistic to 0.013 to 0.026. The
softer cubic kernels were weighed and not added: they trade width for a ringing the clamp already
bounds. One consequence: the star profile fit refuses the sharper master, and every sharp input, because
a Gaussian core with a faint wing is not a fixed-width Moffat to an equal-weight log fit
(`known-limitations.md`). `PsfProfileFit.Diagnostics` now carries the stacked profile, the floor and
the fitted bins, so a refusal can be read bin by bin.

**`PsfProfileFit` fits the star's core and reports its wing** (`Result.WingAt2Fwhm`, `WingAt3Fwhm`).
The Moffat was fitted in log space, equal weight per bin, over every bin above 0.2 percent of the peak
out to 12 px, and it refused every sharp input: a 2.3 px Lanczos master, a two-frame stack, a 1.8 px
sub, because their stacked star is Gaussian to 2 px with a faint half-to-two-percent wing that no
fixed-width Moffat follows across three decades, while blurrier masters sat closer to the family and
passed. The fit now runs over the bins above two percent of the peak (about two FWHM) and reports the
profile's own value at two and three FWHM beside the exponent. Every refused profile returns, every
accepted width is unchanged to the hundredth, and the exponents rise by one to three everywhere (the
wing bins had been pulling them toward heavy wings); above an exponent of about six the core cannot
tell exponents apart, so a high beta means Gaussian-cored. **The store's `MoffatBeta` and E0's beta
statistics were measured the old way** and are a different quantity until the masters are re-measured.

## 7.0

Two releases: `v7.0.1513` on 2026-09-04 and `v7.0.1532` on 2026-09-06, the second carrying 60 more
commits, 27 of them features, listed at the end of this entry.

**BREAKING: `TryLease(out Image?)` is now `TryLease(out ImageLease)`**, cut in one wave with no
compatibility overload. **Migration:** convert each call site to `using`. `default(ImageLease)` is
inert, its `Dispose` a no-op and its `Image` throwing to name the call-site bug. The own side is
unchanged: an owner still calls `Image.Release`, which stays a no-op for self-owned frames. The
token is the BORROW side only.

The reason for the cut is that a shared refcount can DETECT a double release but cannot prevent one
(the offending call is byte-identical to a legitimate last release), and a bare `Image` returned
from `TryLease` carried its release obligation only in a doc comment. `ImageLease` is a readonly
struct whose `Dispose` is the borrower's whole obligation, 1:1 with the claim and spent exactly once
however many struct copies exist. Note that CA2000 does NOT catch a forgotten one: it ignores
value-type disposables, measured directly (a forgotten `ImageLease` beside a forgotten `FileStream`,
only the stream fired), so a dropped lease is the DEBUG leak tracker's catch and not the compiler's.

The cut also closed a latent poison. The bufferless branch used to hand out `this`, so the
borrower's dispose marked the SOURCE released and every later lease of the same published frame
refused: a repeat-polling preview got one frame and then 404s until the frame happened to swap. A
lease is now always a distinct image sharing the planes.

Also in 7.0:

- **Mount safety limits**, the mechanical bound where the tube meets the pier or the ground, which
  is not the meridian flip and can exist with it, without it, or neither. Enforced with no session
  running too, via `MountLimitWatcher`, so `tianwen-server` and the GUI both hold the rig.
- **A meridian flip is verified from the IMAGE, not the mount's word**, wherever the pointing state
  is computed rather than measured, plus `Session.GetSideOfPierAsync` as the canonical pier side and
  a solved field reporting its position angle.
- **Explorer thumbnails** for `.fits` / `.fit` / `.fts` / `.fz` / `.ser`, a NativeAOT COM DLL living
  inside the viewer's own publish tree.
- **Comet integration finished**: the nucleus comes from the raw frames, each channel gets its own
  amplitude, the model reaches as far as each channel's coma, and one run emits the star layer with
  the body subtracted out of it.
- **`StretchMode.Auto`**, the new viewer default, picking Linked when calibrated and Unlinked
  otherwise.
- **Session measurement frames are kept** (`SaveIntermediates`) and every light is stamped with the
  guide RMS of its own exposure.
- Plate solving searches for the frame when the hint is not where it says it is.

The second 7.0 release, `v7.0.1532`, added:

- **Classical background extraction** (`ClassicalBackgroundExtractor`), the AI-free gradient
  corrector: a robust degree-2 polynomial on a block-mean working grid, standing in for GraXpert
  wherever its weights are absent, and exposed as `tianwen image flatten`. Both of its thresholds
  were measured over 118 real masters and turned out not to be knobs.
- **The N2N trainer ported into the repo**, with E0 reproducing the v19d checkpoint bit for bit, one
  degradation exporter serving three trainings, and the injected noise shape treated as a
  measurement rather than a choice.
- **The shipped denoiser checkpoint moves to the gate-selected WIDE seed**
  (`tianwen_denoise_osc_e2wide_s2.onnx`) and the weights go back into LFS. The file name carries the
  checkpoint identity by design: a retrain gets a new name, never a silent replacement.
- Cross-night N2N pairs, both nights resampled onto the midpoint grid, and a training metric that
  asks Gaia whether its "faint stars" are stars (on a nebula field, two thirds were not).
- Viewer P18 to P22: Open and Save as baked marks, save the image as displayed at its own size, one
  frame's stretch held across the folder, a blink through the file list, Shift as the other
  direction rather than a mode, a right-click link into the web Sky Atlas (which now reads a share
  link), and saving the annotated view without a second drawing path.

## 6.3

Released 2026-08-22 (`v6.3.1352`). Additive.

- **Damage-based repaint plus the cached image layer**: a mouse move no longer repaints the window,
  and a divider drag repaints the strip it swept rather than the whole pane. Rides DIR.Lib 8.8,
  Console.Lib 4.27 and SdlVulkan.Renderer 7.25.
- An 8-bit document drops the float planes it duplicates, and uploads its own bytes rather than the
  floats it had been widened into.
- The viewer info pane reports what the camera was SET to (gain, ISO, offset), each suppressed where
  the header said nothing rather than printed as a -1 sentinel. The same fix retired a literal
  "Frame: None".
- A viewer window with nothing open adopts a file from anywhere, instead of letting a second window
  open beside it. A window already showing the folder still wins, and a non-empty one still never
  adopts across folders.
- Calibration gained time-aware master matching (a 2021 dark never blends into a 2026 master), a
  bad-pixel map derived from the lights themselves, and rejection of a wrong-gain dark by default.

## 6.2

Released 2026-08-20 (`v6.2.1314`). Additive, and cut because over 160 features had already shipped
under 6.1: the previous binary release was 2026-06-24, and main had taken 648 commits since, 162 of
them `feat`, every one of which would otherwise have gone out under the same minor.

- **Remote rigs**: the Home board listing every rig you can look at, bindings that survive a DHCP
  lease change, backoff on a rig that is not answering, and a mirrored guider tab that draws the
  guide star rather than an empty panel.
- **The in-house N2N denoiser ships in the repo**, one flag away (`--ai-backend n2n`), with Auto
  rescuing with it when the SAS weights are absent, and CI running the parity test.
- **The image is not necessarily in HDU 0**, so every reader walks to the first HDU that carries
  one. This is what made tile-compressed `.fz` readable at all, since a binary table is only legal
  as an extension.
- **Four-state colour theme** (System / Light / Dark / Night) on one palette, with F12 for dark
  adaptation, and the colour literals becoming roles.
- **Text input as a declaration** on both surfaces, including non-Latin input.
- Viewer toolbar rework: baked icons needing no font, a before/after split, a wrapping second row,
  and a zoom control that says what the zoom is.
- The web build fetches only the sky the view is looking at, off a region-aligned Tycho-2 bake.

## 6.1

Released 2026-06-24 (`v6.1.921`). Additive.

Headlined by the **planetary lucky-imaging stack**, phases 1 through 9: frame quality grading, 2D
FFT and sub-pixel phase correlation, global alignment, alignment points with a displacement-mesh
warp, per-AP quality-weighted integration, Bayer drizzle, a-trous wavelet sharpening with bandpass
and combo presets, the `planetary-stack` CLI, and the live rolling-window stack in the viewer with
adjustable sharpen sliders.

Also: SER planetary video opened as a frame sequence with off-thread frame-paced playback, MHC
debayer mirrored on GPU and CPU, manual and gray-world white balance, Alpaca ImageBytes binary
transfer, and the `TIANWEN_NOW` startup clock anchor.

## 6.0

Released 2026-06-20 (`v6.0.885`). The bump rode the layout foundation milestone and was described as
additive at the time, despite the major number. The sibling pins moved to DIR.Lib 5.0,
SdlVulkan.Renderer 6.4 and Console.Lib 3.0 in the same release, DIR.Lib 5.0 being where the layout
engine this migration consumes had shipped.

- **Shared `GuiTheme` plus surface-agnostic layout engine adoption**, replacing roughly 35 duplicated
  colour constants and six copies of the base font size across seven tabs and the renderer.
  Byte-identical by construction: only value-matching constants were migrated.
- A data-driven equipment panel via a section driver, with the per-OTA panel tree built from the
  layout engine.
- **API keys move to the OS credential vault**, off the profile URI.
- Solar-system planets become first-class in sky-map click, search and the info panel;
  centre-anchored zoom and a limiting-magnitude readout.
- Planner weather work: per-hour humidity, a solar-midnight marker, precipitation probability.

## 5.0

Released 2026-06-14 (`v5.0.862`).

**BREAKING, and the reason for the major: the CLI output formats split by data type.** OpenEXR
becomes the unstretched linear HDR master out of the stacking pipeline (`stack --output-format exr`,
full float32 mono and RGB), and JXR is restricted to stretched or processed output. So
`--output-format jxr` is now rejected by `stack` (use `exr`), and `exr` is rejected by `image`.

Also: the GHS reference math ported and validated, `stack --enhance` integrating `SharpenPipeline`
into `MasterPostProcessor`, sRGB v4 embedded in display TIFF/PNG/JPEG output, 16-bit PNG with cICP,
a zenith-anchored first-scout obstruction oracle with a cloud gate, moon-avoidance penalties in
target scoring, and the DEBUG-only live UI inspector with its MCP sidecar.

## 4.2

Released 2026-05-04 (`v4.2.640`). 242 commits since 4.0, and the first release cut on the rewritten
history (see the preamble).

The **hosting API completed all four phases**, including the ninaAPI v2 compatibility shim for Touch
N Stars, and `tianwen-server` got its README section and download table. The solution migrated to
`.slnx`, with `open-vs.ps1` replacing an auto-generated local solution.

The sky map grew most: an object overlay with priority-based label placement, DSO ellipses instanced
into one draw call, the Milky Way background baked from real Tycho-2 plus Planck radiance and dust
extinction, a Goto button and Pin/Unpin on the info panel, and the Notifications tab. Also: Canon
WPD discovery with mirror lockup and ISO gain modes, and astro defaults applied on connect; the
central serial probe service, with SkyWatcher moved onto it; ASCOM COM throws contained so a dead
hub cannot fail-fast the process; driver-resilience polls routed through `PollDriverReadAsync`; the
polar-alignment refine loop as a third mode of the Live Session tab, with frozen-seed quad matching
replacing ROI-anchor tracking in the incremental solver; the catalog binary format rollout; and the
first plan-status summary, `PLAN-summary.md`.

## 4.1

Never released. Bumped and superseded by 4.2 the same day (2026-04-07), so no tag carries it.

## 4.0

Released 2026-04-07 (`v4.0.564`; binaries removed 2026-09-06, see the preamble).

The bump marks TianWen running **a complete imaging session end to end**: device management,
observation planning, session configuration, automated imaging with guiding, auto-focus, filter
sequencing and live monitoring, for single and dual-rig setups. Dual-rig was the original
motivation, and the README was rewritten at this point to describe an imaging suite rather than a
library.

Also: QHYCCD camera, filter wheel and QFOC focuser support; a Weather device type with an
Open-Meteo forecast overlay in the planner; the moon altitude curve and phase; and hosting API
phases 1 and 2.

## 3.6

Released 2026-03-26 (`v3.6.493`; binaries removed 2026-09-06, see the preamble).

Sixel image preview in the TUI live session tab, per-OTA status in that tab, shared setup logic
consolidated, an application icon (the Helix Nebula), iterative centering with auto-focus pipeline
overlap, and unified per-frame star detection shared by the viewer, the drift check and the exposure
log. Sibling packages moved back to nuget.org and the local nupkg directory was removed.

## 3.5

Released 2026-03-18 (`v3.5.455`; binaries removed 2026-09-06, see the preamble). The bump commit records only the number.

## 3.4

Released 2026-03-17 (`v3.4.454`; binaries removed 2026-09-06, see the preamble).

`IMountDependentGuider`, so a built-in guider receives the same mount driver instance rather than a
second `NewInstance`, wired through `SessionFactory`. `Setup` exposed on `ISession` to remove a
downcast in tests.

## 3.3

Skipped. The version went 3.2 to 3.4 directly.

## 3.2

Released 2026-03-15 (`v3.2.449`; binaries removed 2026-09-06, see the preamble). The bump commit records only the number.

## 3.1

Released 2026-03-11 (`v3.1.442`; binaries removed 2026-09-06, see the preamble). The bump commit records only the number.

## 3.0

Released 2026-03-09 (`v3.0.440`; binaries removed 2026-09-06, see the preamble).

**AOT compatibility**: `IsAotCompatible` and `IsTrimmable` on `TianWen.Lib`, a six-platform AOT
publish matrix (Windows, Linux and macOS on x64 and arm64), and CI split into the build, test,
publish-cli, release and publish-nuget jobs it still uses. This is the release that started cutting
GitHub Releases with binaries attached, which is why the tag history begins here.

## Before 3.0

No tags, so nothing is reconstructed here. For the record, the version lineage runs back through
2.0, 1.8, 1.7, 1.6 and 1.5 in `dotnet.yml`, with the framework bumps at .NET 10 (2026-02), .NET 8
(2024-01) and .NET 7 (2023-10). The first CI workflow was added 2022-05.
