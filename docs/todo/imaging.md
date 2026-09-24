# TODO -- Imaging, Stretch & Colour

**The open items are GitHub issues** labelled [`area:imaging`](https://github.com/SharpAstro/tianwen/issues?q=is%3Aissue+is%3Aopen+label%3Aarea%3Aimaging) since 2026-09-24, when this file was migrated. What is left here is the DONE archive, kept for the measurements and reasons it records. Never add an open `- [ ]` here: open an issue.

## A map sidecar is stored quantised and gzipped, and the mask is kept

- [x] **DONE 2026-09-21, filed and fixed the same day.** Filed on the change that made every strategy
  retain a coverage plane (PR #327), which would have added roughly **6 GB of sidecars** to a
  139-master store the moment it was re-baked. Two things shipped together, because they were one
  question: how a per-pixel map is stored, and which of them we keep at all.

**The finding that decided it: the compression comes from the quantisation, not from the compressor.**
Measured on a real store map (`Eta Car Neb SY135mm`, 3072x3060x3, 81 frames, drizzle weights):

| stored as | raw | gzipped |
|---|---|---|
| float32, 3 channels (what it was) | 112.80 MB | 94.68 MB (1.2x) |
| uint8, 3 channels | 28.20 MB | **1.71 MB** (66x vs float32) |
| uint16, 3 channels | 56.40 MB | 2.09 MB (54x) |
| float32, 1 channel | 37.60 MB | 18.36 MB (6.1x) |

Gzip alone is worth 1.2x because float mantissa bits are noise; quantise first and the same map has 28
distinct values with the interior mode covering 89% of it. So the rule is **quantise, then compress**,
and it lives in one place, `IntegrationFitsWriter.MapStorage`:

- **A map whose samples are whole numbers and fit in the container keeps unit steps** (`BSCALE = 1`),
  so a coverage COUNT is stored as that count and reads back exactly, in any tool, with no scale to
  believe. 8-bit up to 255 frames, 16-bit beyond.
- **Anything else spreads its own range over a 16-bit container** through `BSCALE`, so the step is the
  smallest the data allow: a drizzle's accumulated weight and a rejection fraction both get 65535
  levels of whatever they hold, against the 0.95-of-median comparison the crop tier makes of them.
- **The scale comes off the OBSERVED peak, not the declared one.** A coverage plane is labelled with
  the frame count while a drizzle's weights top out below it; scaling to what is there is a finer step
  for free and loses nothing, no sample exceeding it by construction.
- Sidecars are written `.fits.gz` and read from either form, so every store written before this one
  still reads. `ExistingSidecarPath` is the one place that knows.

**Two bugs found on the way, both silent.** `Fits.Write` wraps any stream in a `BinaryReader`, so a
write-only `GZipStream` is rejected with "Stream was not readable" -- the writer goes through a scratch
file instead of buffering tens of MB per map. And **the `.gz` READ path had never worked**: handed
FITS.Lib's own `BufferedFile`, a compressed file yields an EMPTY HDU list rather than an error, so
every reader here answered "unreadable" for one, silently, for as long as the suffix has been
recognised. Nothing had written one yet. `Image.OpenFits` is now the single opener and uses a plain
`FileStream` for a compressed file; a header-only peek additionally cannot SKIP a data block over gzip
(no seek), which is why `TryReadCoverageMap` looks in the coverage-named slot first.

**Still open, and no longer urgent:**

- **Tile compression (`.fz`)** is the standards-native answer and would keep third-party readability
  without a `.gz` step, but FITS.Lib only DECODES it (`CompressedImageHDU` has no encoder), so it is a
  sibling-repo change. Worth doing when something else needs the encoder; the ratio would be similar.
- **A coordinate-list bad pixel map** (a FITS binary table of set pixels) would be kilobytes rather
  than the compressed raster's tens of KB, but it is a format only we could read, and the raster is
  already small enough that the difference does not pay for that.
- `CoveragePlane` still has three entry points because three strategies hold their counts in three
  shapes (a sink, a `uint[,]`, an assembled plane). One definition, three doors: acceptable, worth
  collapsing if a fourth appears.
- **The re-bake itself.** It was gated on this decision and no longer is.

### The bad pixel map is now persisted, in the format the archive already uses

- [x] **DONE 2026-09-21, with the above.** `BadPixelDetection` returns `BitMatrix[]`, the union of its
  two producers is the mask the integration applies, and it used to be discarded at the end of every
  run. It is now written beside the master as `.badpixels.fits.gz` from both paths (the stacker
  through `MasterPostProcessor`, the bake through `RetainedMasterStore`), which makes a store a dated
  SERIES per camera rather than a single latest mask: **that is the point, a sensor's defect
  population moves over years.**

**Why persisting it was worth it, in one incident we already paid for.** An EVEN sampling stride
phase-locked to the CFA, 100% of blue was flagged hot, and the master was written with an all-NaN blue
plane while the session reported success. `BadPixelDetection.DefaultMaxMaskedFraction` is the guard
that stops that now; a written map, with `NBADPIX` and `PBADPIX` in its header, is what would have
SHOWN it at a glance instead of a log line nobody read.

**The format is AstroPixelProcessor's, deliberately** (`BadPixelMap`). Reading one of this archive's
own maps settled it: `BPM-ZWO_ASI462MC-1936x1096.fits` is `BITPIX = 8`, one byte per photosite, three
levels -- 127 linear, 255 hot, 0 cold -- with `NBADPIX = 58629` (2.763%, kappa 3, 200 darks) and cards
naming the instrument and the frame counts. We write 127 and 255 only, our detectors converging a
threshold rather than sorting hot from cold, and READ anything that is not 127 as flagged, so their
maps are legible here and ours in their tools. It gzips 23.4x on that real map (2.12 MB to 0.091 MB),
and the conversion walks `BitMatrix` a word at a time, skipping the zero words a sparse mask is almost
entirely made of.

**The archive's 18 APP maps stay useful for the other two reasons:** they are real masks to validate
ours against rather than only synthetic ones, and their dimensions are an authoritative per-camera
frame size, which is where `SensorGeometry`'s table comes from. Note
`BPM-ZWO_ASI533MC_Pro-2256x2256.fits`: a BPM is built against a FRAME geometry, so a ROI or a binned
run gets its own, and the LARGEST per camera is the sensor.

### Make the sensor table a baked database, not a private list in the crop path

`SensorGeometry`'s dictionary is 13 cameras hand-entered from this archive's bad pixel maps. That is
the right SOURCE and the wrong SHAPE: it is private to the auto-crop fallback, it covers only gear
that has passed through here, and at least three other things want the same data.

**Bake it the way `FilterCurveDatabase` is baked** -- a generated table plus a tool that builds it,
rather than a literal someone edits. Three sources, and the first is already done:

- **Canon needs no fetching.** `FC.SDK.Raw.CanonSensorInfo.Resolve(width, height, cfa, makerNote)`
  already returns a per-body `CanonActiveArea`, which is exactly this fact and is what
  `Image.TryReadCanonRaw` crops to. A bake reads it rather than restating it.
- **IMX and the CMOS astro cameras** are the fetch: ZWO, QHY, SVBONY and Player One publish full-well,
  pitch and resolution per model. One scrape, reviewed by hand, committed as data. Keyed on the camera
  as `INSTRUME` writes it, never on the die -- the ASI585MC Pro and the Uranus-C are the same IMX585
  with different active areas.
- **The archive's own BPM filenames** stay as the cross-check, since they are measured rather than
  claimed.

**Three consumers, which is what justifies the shape:**

1. The auto-crop fallback, for a foreign master with no coverage plane (today's only caller).
2. Smart framing, which currently gets sensor specs only by capturing them off a connected camera into
   the profile -- so a rig you have not plugged in cannot be framed.
3. **The web app**, where a sensor + telescope picker is the whole point: let someone try framings for
   gear they do not own yet. That one needs the database to ship as data rather than to be discovered
   from hardware, and it is the reason this belongs somewhere more public than a crop helper.

## Calibration + integration gaps vs Siril / APP / PixInsight WBPP

Filed 2026-08-03 from a stage-by-stage comparison of `StackingPipeline` against the three tools,
prompted by finding that the master flat was never calibrated. Ordered by value per unit of work.
The flat gap itself is **fixed** (see [known-limitations](../known-limitations.md)); these are what
the comparison turned up alongside it.

## Archive filter inference (committing a validated method)

Filed 2026-08-04. The method is validated on 48 sessions / 7,161 frames and the *write* end is
committed (`dataset tag-filter` + `FitsHeaderEditor`), but every measurement lives in scratch scripts
plus a `_provenance` folder on `D:`, so a fresh checkout re-derives nothing. Full method, the
reference bias table, the resolution limits and four recorded negative results:
[docs/plans/filter-inference.md](../plans/filter-inference.md).

### Do NOT discard the SharpCap era on the focus assumption (measured 2026-08-04)

The working assumption was that only N.I.N.A. sessions are worth keeping, because SharpCap will not
refocus on temperature drift without hassle. **The first half is true and the conclusion does not
follow.** Measured with `tianwen image stats` on the one rig that spans both programs (ASI533MC Pro
at FL 130, the Samyang 135, so identical 5.97 arcsec/px), 23 distinct sessions, `FOCUSPOS` and
`FOCTEMP` read from every frame:

| | focuser moved mid-run | HFD drift start to end | mid-session HFD |
|---|---|---|---|
| N.I.N.A. (12) | **11 of 12** | -3.2% to +3.4%, median **-0.1%** | median **2.45 px** |
| SharpCap (11) | **0 of 11** | -8.3% to +10.5%, median **+6.2%** | median **2.71 px** |

- **The mechanism is confirmed by direct evidence, not inference.** Zero SharpCap sessions moved the
  focuser, across every run, while `FOCTEMP` fell 0.7 to 3.8 C. That is the focuser's own reported
  position, so it needs no argument from image quality.
- **But the quality distributions overlap, so a blanket discard is wrong.** The best SharpCap session
  (2024-07-06 Rim Nebula SII, 2.42 px, drift -0.3%) is **sharper than 9 of the 12 N.I.N.A. sessions**.
  Discarding by program label would throw away frames better than most of what is already kept.
- **There is a free triage signal.** SharpCap sessions split cleanly on thermal drift: all 4 with
  `|FOCTEMP delta| <= 1.1 C` held focus, and 6 of the 7 above 1.4 C degraded by 6 to 10%. That is
  readable **from headers alone**, with no pixel reads, so all 45,134 SharpCap frames can be triaged
  cheaply before any measurement. The categorical split is much stronger than the linear fit
  (Pearson r is only +0.31, dragged down by the two anomalies below), so use a threshold, not a slope.
- **A N.I.N.A. session fails the same way.** `HIP 80609` 2026-04-21 is the **softest** session in the
  whole sample (2.89 px) and its focuser never moved. Judge per session, never per program.
- **Two SharpCap sessions improved through the night** (-8.3% and -7.8%). Both have healthy star
  counts (1,316 and 1,005), so this is real and not noise; a manual mid-session refocus would explain
  it and the data cannot distinguish that.

**Two limits on how far this generalises.** Star counts were 791 to 1,710 everywhere, so no session
was noise-limited, but (1) at 5.97 arcsec/px an HFD of 2.4 px is 14.3 arcsec and the rig is heavily
undersampled, so HFD is partly floor-limited and the differences above **understate** the true focus
error; and (2) this covers the ASI533-at-FL130 years only. The 2021 to 2022 era was **not** tested,
because 511 of its directories carry no `IMAGETYP` card at all and were skipped by the session gate.

## Archive + FITS interop backlog (recovered 2026-08-20)

Filed from the `feat/ai-enhancements` handover. **Provenance matters here:** these were tracked as a
numbered task list that lived only inside a chat session, so the numbers referenced nothing durable
and are dropped; the old number is given once per entry only so the handover history stays greppable.
Several entries carry a DECISION already made, which is the part that was actually at risk.

Interop facts hit on real data during that review, kept because they are not written down anywhere
else and each cost time to establish:

- **`PEDESTAL = -100` means +100 ADU was re-added after calibration** (MaxIm's sign convention), so
  the number is not a value to subtract as it reads.
- **A CFA pattern is measurable from the pixels alone**, which is how `BAYERPAT='VALID'` was resolved
  without trusting any header: the two green subplanes' medians agree to ~0.2% (that names the green
  diagonal), and of the two remaining subplanes the brighter one is red on any real sky.

Closed from the same review, recorded so nobody re-litigates them: `SNAPSHOT` (the SBFITSEXT twin of
`STACK_N`) and `SWMODIFY` (modifying-software card) were adopted and shipped; `READOUTM` folded into
the calibration temporal/tolerance work and shipped with it.

## Quad-Bayer sensors and the read mode that changes what a CFA even is

Filed 2026-09-16 (owner). **The QHY294C Pro is an IMX492, a QUAD-Bayer sensor with two read modes,
and nothing in TianWen has a concept of a read mode at all** (`grep ReadMode src/TianWen.Lib/Devices`
returns nothing; the `READOUTM` card was folded into the calibration tolerance work and never became
a capability). The 11 MP mode is the one the whole archive is in (77 directories, 4164 x 2795, plain
`RGGB`) and it is effectively bin 2: four same-colour photosites combined on the sensor. The other
mode reads all of them, roughly 47 MP at half the pitch, and there **the colour filter is not a 2x2
Bayer tile but a 4x4 block of 2x2 same-colour quads**, so a standard demosaic is wrong on it by
construction rather than by a phase error.

## An embedded preview HDU in our own masters

Filed 2026-09-13, out of the Explorer-thumbnail work (`fix(thumbnails): read nothing until asked`).

## Imaging

- [x] **DONE 2026-08-21. Document-open traversal cost, and a correction to how it was first
  reported.** The original entry here quoted `Statistics(c)` x3 = 1,028-1,195 ms and called it the
  dominant cost. **Those were DEBUG numbers.** `dotnet test` defaults to Debug, and this library's
  inner loops are ~7x slower there, so the figure described the test configuration rather than the
  product. Re-measured in Release on the same 6000x4000x3 probe (planes pre-touched so page faults
  are not charged to the first stage), the ranking inverts:

  | stage | Debug (as filed) | Release before | Release after |
  |---|---|---|---|
  | `Statistics(c)` x3 | 1,028-1,195 ms | 130-177 ms | 120-150 ms |
  | `GetStarMaskedMedianAndMADScaledToUnit(c)` x3 | 557-755 ms | 412-632 ms | **23-70 ms** |

  So the real hot spot was the star-masked median, not the histogram, and its cause was algorithmic
  rather than micro: **two full `Array.Sort` calls over ~1.5 M samples per channel to extract two
  medians.** A median needs selection, not a sort. Fixed by `StatisticsHelper.NthSmallest` (a public
  wrapper over the private `QuickSelect` that was already there), reusing one buffer for both
  passes. `Normalizer.MedianViaQuickSelect` had already made exactly this trade for the stacking hot
  path -- and `Image.Histogram.cs` already had `using static StatisticsHelper` while still sorting.

  The histogram loop was improved too, 50 -> 32 ms per 24 MP channel, by two changes that a code
  reading would rank the wrong way round: a flat span instead of `float[,] [h, w]` indexing (50 ->
  36) and a float-domain clamp instead of `Math.Clamp`, whose `(float, int, uint)` call binds the
  **double** overload and so ran float -> double -> clamp -> double -> int per pixel (36 -> 32).
  Every change is bit-identical, pinned by `HistogramSelectionParityTests` (which reimplements the
  pre-change algorithm as an oracle) plus `NthSmallestTests`.

  Two lessons worth keeping: **quote the configuration with any timing**, and note that the parity
  test could NOT distinguish `MedianFast` from the upper median on any real fixture (swapping it in
  left every assertion green), because a quantised background ties the two middle samples. That
  convention is pinned on synthetic distinct values instead.
- [x] **DONE 2026-08-21. Time AND allocation for the stats paths, in BenchmarkDotNet:
  `StatsPathBenchmarks`.** Run with
  `dotnet run -c Release --project TianWen.UI.Benchmarks -- --filter '*StatsPath*'`. Each change
  sits against the implementation it replaced as an explicit `[Benchmark(Baseline = true)]`, so the
  Ratio and Alloc Ratio columns are computed rather than asserted. At Size=3008 (an ASI2600 sub,
  and the size at which these buffers cross into the LOH):

  | path | before | after | ratio | alloc before | alloc after | alloc ratio |
  |---|---|---|---|---|---|---|
  | star-masked median+MAD (x3 ch) | 136.0 ms | 25.6 ms | **0.19** | 13,141 KB | 6,645 KB | 0.51 |
  | `Normalizer.ComputeStats` whole | 79.4 ms | 75.3 ms | 0.95 | 28,089 KB | 28,089 KB | **1.00** |
  | `Normalizer.ComputeStats` box | 77.3 ms | 72.4 ms | 0.94 | 18,727 KB | 14,046 KB | 0.75 |
  | histogram bin buffer | 154.8 us | 7.0 us | **0.05** | 512 KB | 256 KB | 0.50 |

  **Read the two Normalizer rows with the error bars in view**: at `[ShortRunJob]` those means
  carry a 99.9% CI half-width of 5-35 ms, so a 0.94-0.95 ratio is not distinguishable from 1.0.
  The Normalizer change is justified by the duplication it removed and by its EXACT allocation
  numbers (MemoryDiagnoser counts, it does not sample), not by its timing. The star-masked and
  histogram rows are far outside the noise.

  **This replaced two hand-rolled probes, and getting the tool right corrected them.** They timed
  "best of 3" around `GC.GetTotalAllocatedBytes` after a forced collection, and reported the box
  path as **19.20 -> 0.00 MB/call**. That zero was an artefact of measuring five reps with a
  perfectly warm pool; over BDN's steady state the honest figure is 0.75. The probes also each
  carried a private copy of the pre-change implementations, which is how one wrong label ("2
  rents" on a path that had one) came to be written twice. They are deleted; the baselines live
  once, in the benchmark.

  What survived from the probe work: **the whole-image `Normalizer` path allocates exactly what it
  did** (alloc ratio 1.00 -- the old one had ONE rent, the min pass rented nothing), and
  `ArrayPool<float>.Shared` **does** pool a 34 MB array, so the guess that it capped near 1 MB was
  wrong. And the benchmark found a cost nobody had suspected: `Image.Histogram` built its bins in
  an `ImmutableArray<uint>.Builder`, paying for the builder's backing array AND a second one
  because `ToImmutableArray()` on a Builder copies. Now a plain `uint[]` wrapped by
  `ImmutableCollectionsMarshal.AsImmutableArray`: half the memory, **20x** less time, the 64-call
  zero-fill loop deleted (`new uint[]` is already zeroed), and bit-identical bins. A document open
  makes 10-12 of those calls.

  `MemoryDiagnoser` also reports Gen0/1/2 collections per 1000 ops, which the probes could not
  produce at all (five reps never triggered a GC): the histogram buffer goes 14.77 -> 7.20
  collections per 1000 ops at Size=1280.
- [x] `RollingWindowStacker.BuildMasterAsync`: reuse a persistent sum scratch; **DONE (2026-07-06, same-day as the audit)** with a twist the audit missed: `PlanetaryMaster.NormalizeInPlace` *wraps* its input arrays into the returned `Image` and `MergeAndDemosaicAsync` passes mono/RGB masters through, so for mono/RGB the old `Clone()` **was** the master's backing store (not reducible, the previous master may still be displayed / cached for wavelet re-sharpen). Fix shipped as the fused `PlanetaryMaster.NormalizeInto(src, weight, dst, meta)` (single read-sum/write-dst pass replaces Clone-then-normalise for **both** paths, halving memory traffic) + a persistent `_sumScratch` used **only** on the split-CFA branch, where the normalised sub-planes are transient (merged + demosaiced into a fresh master). Scratch is shape-checked (reference/ROI can change). Pinned by `RollingWindowStackerTests.Published_mono_master_stays_valid_after_the_next_publish` (guards against ever routing mono through the scratch).
- [x] `Image` constructor should take `Channel`s, not raw `float[][,]`; **DONE (2026-07-06, same day as filed)**. The primary ctor is now `Image(ImmutableArray<Channel> channels, BitDepth, pedestal, meta)`: per-channel `Filter`/`MinValue`/`MaxValue`/`Index` live on each `Channel` (readable via `Image.GetChannel`), the image-wide `MaxValue`/`MinValue` are **derived extrema** across the channels, shapes are validated same-dimension, and the ref-counted camera buffer travels ON the channel (new `internal Channel.Buffer` init-prop, harvested by the ctor, `WithChannelBuffers` and the `ICameraDriver.ChannelBuffer` side-channel property are deleted). The legacy raw-array signature survives as a delegating overload that stamps the image-wide values on every channel, so all ~164 existing construction sites compiled untouched (derived extrema == the passed values by construction). `ICameraDriver.GetImageAsync` is the single typed hand-off (`new Image([channel], …)`); all four buffer-recycling drivers (DAL, Fake, Alpaca, ASCOM) attach the buffer at `Channel` creation, and `FakeCameraDriver.ReleaseImageData` strips the buffer from its retained channel so a second `GetImageAsync` cannot harvest an already-transferred ref. `ScaleFloatValuesToUnitInPlace` rewraps per-channel min/max scaled but deliberately drops `Buffer` (release responsibility stays with the original, double-release guard). Pinned by `ImageChannelCtorTests`.

## Codecs facade (read + write)

The `SharpAstro.Codecs 3.6.*` facade (sniff → dispatch: PNG/JPEG/TIFF/JXR/EXR/JXL, plus the
`SharpAstro.Jpeg.GainMap` Ultra HDR member) is consumed for **read** in tianwen as of the Phase-5
fallback (`Image.Import.TryReadViaCodecs`; full arc in [`../plans/image-codecs-facade.md`](../plans/image-codecs-facade.md)).
Open gaps:

- [x] **CMYK / Separated TIFF renders as a negative (DONE 2026-08-20).** A `Photometric = 5` TIFF with 4 samples per
  pixel (a print export, e.g. GraXpert's `..._printer.tiff`) has its C/M/Y read as R/G/B with K
  dropped, and since a high CMYK value means MORE ink the polarity inverts: white sky, dark stars,
  cyan cast. `SharpAstro.Tiff.TiffImageDecoder` already declares this out of scope
  (`page.Photometric is not (MinIsBlack or Rgb)` -> refuse), but `Image.Import.cs` calls
  `TiffReader.Read(bytes)` **directly** and bypasses that guard. Two options: honour the guard so the
  file fails to open with a clear message (consistent with a decision the codec layer already made),
  or convert CMYK->RGB. Converted, in `Image.Import.cs`: accurate conversion needs the embedded ICC
  the naive `R = (1-C)(1-K)` form is what most viewers do and would at least fix the polarity.
  Backlogged and then done the same day, out of [`../plans/viewer-prerelease-fixes.md`](../plans/viewer-prerelease-fixes.md)
  (was P4): a printer proof is a print export, not a working frame, so it does not gate a release.
  Note it only became *visible* once the Predictor 2 fix made the file decode to structure at all.
- [x] **Gain-map JPEG export during stacking / rendering (DONE).** Emits Ultra HDR (hdrgm 1.0 / Android
  Ultra HDR v1) gain-map JPEGs from the stacking/render preview path; a broadly-supported HDR delivery
  format (Android / Chrome / Adobe) alongside the existing cICP-PQ PNG HDR previews. Unlike the PQ preview
  (a uniform re-map of the already-clamped SDR raster), the gain map performs **per-pixel highlight
  recovery**: `Image.RenderHdrLinearRgb` renders a display-referred *linear* rendition (1.0 = SDR white)
  from the master's PRE-MTF signal, the value the midtones transfer function flattens to a white plate in
  SDR, so a bright core (star / nebula / galaxy) that the stretch over-blew keeps its structure and gradient
  on HDR viewers while the faint background matches SDR exactly (gain ~0 below the clip). Wiring:
  `MasterPreviewRenderer.RenderAsync(ultraHdrPath:)` builds the SDR base + HDR-linear pair, `JpegGainMap.Compute`
  fits the map, `JpegEncoder.Encode` encodes both renditions, `JpegGainMap.Assemble` splices GContainer XMP +
  MPF. Selected via the `MasterRenderOutputs` `[Flags]` enum on `StackingOptions.RenderOutputs`
  (`stack --output-format uhdr`, `--hdr-peak-nits`) and `ImageOutputFormat.UltraHdr` (`image render/sharpen
  --output-format uhdr`, headroom from `--png-pq-peak-nits`). Headroom = peak nits / 203-nit BT.2408 SDR
  reference white; cores roll off smoothly toward the cap. Stretched display raster only, never the linear
  FITS/EXR masters or split-plate TIFFs (same rule as `MaskedBoost`). Pinned by `MasterPreviewUltraHdrTests`.
  **Caveat:** a gray gain map recovers highlight *structure/luminance*, not saturation (the recovered core
  keeps the SDR base's hue), per-channel re-saturation would need an RGB gain map (a later refinement),
  and it can only recover headroom the linear master actually holds (a sensor-saturated core stays flat).

## AI Enhancement

Shipped on branch `ai-enhancement` (Phases 0-6 of `docs/plans/ai-enhancement.md`): `IStarRemover` + `IStellarSharpener` + `INonStellarDeconvolver` atomic enhancers, `SharpenPipeline` orchestrator (additive + screen modes), shared `ChunkedNafnetRunner`, MTF helpers on `Image.Stretch.cs`, `ChunkedInference` tile/stitch, `HfdPsfEstimator`, `tianwen image {sharpen,remove-stars}` CLI. Items below are deferred follow-ups.

### Deferred CLI verbs (image group)

Each verb maps to an enhancer / classical implementation that hasn't been wired yet. CLI shape mirrors the shipped `tianwen image sharpen` (input FITS, `-o output`, default `<input>_<verb>.fits`).

- [x] `tianwen image flatten` -- ABE gradient removal (classical, no AI). **Core SHIPPED 2026-09-02** as
  `ClassicalBackgroundExtractor` (`docs/plans/background-extraction.md`, "Implementation": robust degree-2
  polynomial + optional inpainted surface, linear, level preserved per plane, CFA per photosite), and
  `flatten` runs it whenever GraXpert's weights are absent (`FallbackGradientCorrector`). RBF was dropped by
  the reference review, not deferred.
- [x] **Gradient-distribution report over the retained masters** (gradient-remover-training.md G1).
  **DONE 2026-09-03**: `tianwen dataset gradient-report --masters <bake>/session-masters --out <bake>`
  (`DatasetGradientReport`, `tools/run-gradient-report.ps1` for the detached run), append-only
  `stats/gradient-masters.jsonl` + a rewritten `stats/gradient-report.md`. 118 masters over both bakes:
  amplitude, shape, principal direction joined to altitude, azimuth, airmass, parallactic angle and Moon
  geometry, plus a threshold sweep. The H1 verdict is in the plan.
- [x] **Read Siril's gradient-correction scripts as a reference for the above** (user, 2026-08-18).
  **DONE 2026-09-02**: all three read in full and distilled into
  [background-extraction.md](../plans/background-extraction.md) "Reference review". Two of them
  change that plan: `AutoGradientRemoval.py` needs NO sample points (robust pixel-set rejection +
  a masked low-pass inpainting surface, which also retires the RBF solver question), and
  `AutoBGE.py` (the SAS v2 AutoDBE port) fits AND corrects in a STRETCHED domain, which we must not copy;
  SAS Pro's current `abe.py` (read from GitHub the same day) calls moving the correction back to linear
  its "KEY FIX", seeds its sampler and defaults RBF on. Also
  found: GraXpert is GPL-3.0 code + CC-BY-NC-SA-4.0 models, not the "MIT" two of our comments said.
  Siril's background extraction is one of the three prior-art implementations
  [background-extraction.md](../plans/background-extraction.md) already names (with PixInsight
  ABE/DBE and GraXpert), but only GraXpert has a concrete interop question recorded against it (its
  open question 4, reading GraXpert's exported background images). Siril's are readable scripts
  rather than a binary, which makes them much the cheapest of the three to learn the sample-placement
  and rejection heuristics from -- exactly what `image flatten` has to get right and what the plan
  currently leaves open. **The note carried no link**; these were located afterwards in the same
  `free-astro/siril-scripts` repo the narrowband work already sources
  ([narrowband-colour.md](../plans/narrowband-colour.md)), so there is nothing further to hunt for:
  - [`processing/AutoGradientRemoval.py`](https://gitlab.com/free-astro/siril-scripts/-/blob/main/processing/AutoGradientRemoval.py)
    -- the closest match to the note's own words.
  - [`processing/AutoBGE.py`](https://gitlab.com/free-astro/siril-scripts/-/blob/main/processing/AutoBGE.py)
    -- automatic background extraction; the sample-placement half, which is the part `image flatten`
    most needs and the part the plan leaves open.
  - [`processing/GraXpert-AI.py`](https://gitlab.com/free-astro/siril-scripts/-/blob/main/processing/GraXpert-AI.py)
    -- **not** asked for, but it is Siril driving GraXpert, so it answers
    background-extraction.md's open question 4 (how a GraXpert workflow interoperates) from a working
    implementation rather than from the docs.
  **The licence rule from the narrowband work applies verbatim**: the Siril script repo is GPL-3.0
  against our AGPL-3.0, so reimplement from the recorded maths, never vendor.

### Other deferred AI work

- [x] **Productionise the GHS starless stretch.** DONE on branch `ghs-converge` per [ghs.md](../plans/ghs.md). The dim-output problem was the convergence target -- median-target = 0.25 left the bg peak (mode) below 0.25 for typical astro frames where median sits above mode (signal tail). Resolved by adding mode-target convergence (`--ghs-target Mode`) + Cranfield's canonical multi-stage chain (`--ghs-stages 3`: stage 1 + BackgroundReduce + stage 2 b=2.5/hp=0.95 + stage 3 b=-1/hp=0.99 log). Canonical recipe: `--dual-stretch --starless-stretch-mode Ghs --ghs-target Mode --ghs-target-value 0.25 --ghs-stages 3`. **GHS stays opt-in per `feedback_ghs_not_default`** -- MTF remains the default starless stretch; user explicitly decided against promotion to `SharpenRequest.Canonical()` because GHS is a different aesthetic, not a universal upgrade. Outstanding: {broadband, narrowband, single-light} corpus validation outside SoL drizzle. The parameter-prediction model idea is parked -- the multi-stage canonical chain with mode convergence covers the in-corpus failure modes without ML.

## Stretch / Image Processing

Learnings from PixInsight Statistical Stretch (SetiAstro, v2.3).

- [x] **Masked finishing boost for the preview render** (2026-07-03); `Image.MaskedBoost` composes the new mask primitives (`LuminanceRangeMask` + `BlendThroughMask` + `Saturate` / `ContrastBoost`, `Image.Masks.cs`) into the Affinity masked-contrast-boost + saturation macro; surfaced as `stack --saturation/--contrast-boost` + the same flags on `image render`, applied to the stretched preview PNG only (`MasterPreviewRenderer.ApplyMaskedBoost`). Basic mask support shipped alongside: `Invert`, `Binarize`, `GaussianBlur` (feathering), scalar `Multiply` (partial-strength masks). Linear masters + split-plate TIFFs untouched by design.

- [x] Luma-only stretch mode (Rec. 709 luminance, stretch Y, scale RGB by Y'/Y)
- [x] HDR compression in GPU shader (Hermite soft-knee, `uHdrAmount`/`uHdrKnee` uniforms)
- [x] Normalize after stretch (2026-05-11): `StretchUniforms.NormalizeScale` carries a precomputed `1/max` so the GPU stays single-pass. `Image.PredictPostStretchMaxScale` walks the top non-zero histogram bin of each channel and pushes it through the full chain (stretch + curves + HDR); CPU and GPU multiply the post-HDR value before the final clamp. Producer surfaces a `normalize: bool` knob on `AstroImageDocument.ComputeStretchUniforms`; tests in `StretchTests_NewPipeline.GivenColorFitsWithHdrWhenNormalizingThenPeakLiftedToFullRange` + `GpuStretchPipelineTests.GpuMatchesCpuForHdrNormalize`.
- [x] Iterative convergence: `Image.ConvergeStretchFactor` bisects stretchFactor using histogram until post-stretch median converges to target (0.25). Gated by `AstroImageDocument.UseIterativeConvergence`. **Bisection direction was inverted (fixed 2026-05-10)**; **WB-aware (median/mad/binNorm scaled by `whiteBalance` scalar) since 2026-05-10** so converged factor matches per-channel rendering when SPCC/skyBg WB is active.
- [x] Star-masked background extraction: `GetStarMaskedMedianAndMADScaledToUnit` recomputes median/MAD excluding star pixels after detection; `StarMaskedStats`/`StarMaskedLumaStats` preferred in `ComputeStretchUniforms`. **Two bugs fixed 2026-05-10**: (1) returned median in raw pixel-value space while the unmasked twin returns pedestal-subtracted, now consistent; (2) MAD floor `invMax * 0.5f` collapsed to 0.5 after `ScaleFloatValuesToUnitInPlace`'d images had `MaxValue=1`, pinning every masked MAD at half the dynamic range; replaced with fixed `0.5/65535` bin-width floor.
- [x] CPU mirror of GLSL stretch: `Image.StretchChannelCpu` / `StretchLumaPixelCpu` / `ApplyHdr` / `RenderStretchedRgba` (full image → RGBA buffer). `ConsoleImageRenderer` and `StretchTests_NewPipeline` route through these; both must produce visually equivalent output to the GLSL fragment shader for the same `StretchUniforms`.
- [x] Tycho-2 photometric color calibration: `Tycho2ColorCalibration.ComputeWhiteBalance` matches detected stars to Tycho-2, extracts aperture photometry, computes WB multipliers; flows through GPU UBO and CPU path
- [x] SPCC spectrophotometric color calibration: `Tycho2ColorCalibration.ComputeSpectrophotometricWhiteBalance` integrates Pickles SED × system throughput (QE × CFA × filter) per matched star, fits WB multipliers; `AstroImageDocument.ComputeSpccColorCalibrationAsync` surfaces to viewer; `W` key tries SPCC first, falls back to sky-bg method. **Verified end-to-end** by `StretchTests_NewPipeline.GivenSyntheticStarFieldWhenSpccCalibratedThenWritesTiff`; projects Tycho-2 stars onto a synthetic Sony OSC field with matching synthetic WCS, runs SPCC against IMX533 QE × Sony CFA throughputs.
- [x] Background neutralization (pivot1 mode): `BackgroundNeutralization.ComputeGains` ports SETI Astro Suite Pro's highlight-protecting neutralization; uses existing `ScanBackgroundRegion` for dark-region sampling; GPU shader applies `out = norm * g + (1-g)` before white balance; `N` key toggle, toolbar button. Algebraically verified equivalent to SETI's `out = 1 - (1 - val) * g`.
- [x] Fritsch-Carlson spline curves: `FritschCarlsonSpline` struct with monotonic cubic Hermite interpolation; `applyCurveLUT` in GLSL shader via 33-knot UBO; `ApplyCurveLut` CPU path. **`ComputeKnots33` capacity bug fixed 2026-05-10** (would crash GUI when user pressed Shift+B to toggle curve mode); array now sized to 33 floats with no padding so CPU/GPU divisor (lut.Length-1 vs hardcoded 32) align.
- [x] WB-vs-shadow coordinate-space mismatch fixed (2026-05-10); `ComputeStretchUniforms` now scales per-channel median+mad by WB before deriving shadows/midtones/rescale, so post-WB norm and shadow live in the same space and channels reduced by WB don't clamp to zero.
- [x] SASP filter/sensor/SED data tracked in git (2026-05-10); `filter_curves.gs.gz`, `sensor_qe.gs.gz`, `pickles_sed.gs.gz` exempt from the gitignore wildcard so CI can load them. Total +3 MB; only changes when SASP-data upstream changes.
- [x] Test verification overhaul (2026-05-10): `StretchTests_NewPipeline` asserts every `StretchUniforms` field (Pedestal/Shadows/Midtones/Rescale/WhiteBalance/BackgroundNeutralization/CurveData) plus per-channel byte means after rendering. `StretchTestBase` got per-channel float-range + AutoLevel quantum-range assertions for all 4 legacy stretch test files. Catches per-channel collapse regressions.
- [x] **Mesa lavapipe CPU/GPU divergence: root cause was a dangling-pointer bug in `SdlVulkan.Renderer/VkPipelineSet.cs`** (resolved 2026-05-11 evening). NOT a Mesa bug.

  **Actual root cause**: `new VkPipelineColorBlendStateCreateInfo(blendAttachment)` (Vortice.Vulkan 3.2.1 constructor that takes a single attachment by value) stores `pAttachments = &attachment` pointing at the constructor's stack frame, which is reclaimed when the constructor returns. The graphics-pipeline create then reads garbage `VkBlendOp` from that location. On ARM64 the post-frame stack happened to contain values that decoded to valid blend ops; on x86_64 it contained values outside the valid `VkBlendOp` enum range. Release Mesa silently passed the garbage through `vk_blend_op_to_pipe`, producing zeroed-out fragment writes for primitives and the partial channel corruption we observed when the clear color was non-zero.

  **Fix**: in `SdlVulkan.Renderer/src/SdlVulkan.Renderer/VkPipelineSet.cs::CreatePipeline`, replace the single-arg constructor with an explicit `stackalloc VkPipelineColorBlendAttachmentState[1]` whose lifetime spans the `vkCreateGraphicsPipeline` call, then `pAttachments = blendAttachments; attachmentCount = 1`. The local `tools/lavapipe-repro` rebuilt against the fix reports the expected nonzero pixel counts on x86_64 lavapipe with Mesa 25.2.8 / LLVM 20.1.2 / 256-bit AVX2: FillRectangle=18200, DrawRectangle=2752, DrawLine=180-236, FillEllipse=15380, DrawEllipse=1272.

  **How we found it**: built Mesa 25.2.8 from source with `-Dbuildtype=debug -Dshared-llvm=enabled -Dgallium-drivers=llvmpipe -Dvulkan-drivers=swrast -Dplatforms=` and pointed the repro at `lvp_devenv_icd.x86_64.json` via `VK_DRIVER_FILES`. The debug build trips the assertion `vk_blend_op_to_pipe: Invalid blend op` in `src/vulkan/runtime/vk_blend.c:66` and tells us the value passed to `vkCmdBindPipeline`'s blend op was bogus. Distro Mesa is shipped without `--enable-debug`, so `LP_DEBUG=llvm` is a no-op and validation layers don't catch this; only the assertion in debug Mesa surfaces it.

  **Follow-ups** (all DONE, verified 2026-06-12):
  - Commit the fix to `SdlVulkan.Renderer`: done, published (tianwen consumes 6.0 as of PR #21).
  - Bump `SdlVulkan.Renderer` minor and publish via `/release-lib`; done.
  - Bump tianwen `Directory.Packages.props` to consume the new version; done (`549b612` bumped 5.1 -> 6.0).
  - Revert the `Assert.Skip(llvmpipe)` guards in `GpuStretchPipelineTests`, `VkHistogramPipelineTests`, `VkRendererPrimitiveTests`; done, no llvmpipe skips remain in the tree.
  - Delete `.github/workflows/test-mesa-latest.yml`: done, only `dotnet.yml` remains.
  - `lavapipe-bug-report-draft.md` deleted: no upstream bug to file.

- [x] Luma blend (2026-05-11): `StretchUniforms.LumaBlend` (0 = pure linked, 1 = pure luma, default 1 preserves status-quo Luma-mode behaviour). Producer always populates `LumaStretch` (scalar Luma MTF params) and per-channel linked `Shadows/Midtones/Rescale` in Luma mode so the shader has both branches ready; GLSL `mix(linked, luma, lumaBlend)` inside the Luma branch. Tests: `StretchTests_NewPipeline.GivenColorFitsWhenBlendingLumaWithLinkedThenOutputInterpolates` + `GpuStretchPipelineTests.GpuMatchesCpuForLumaBlend`.
- [x] Rec.601 / Rec.2020 luma weighting (2026-05-11); new `LumaWeighting` enum, `StretchUniforms.LumaWeights` `(R,G,B)` triple, resolved by producer; CPU mirror + GLSL Luma branch + `ComputePostStretchBackground` all read from the uniform. Default Rec.709 keeps existing callers on the same numerical path. Tests: `StretchTests_NewPipeline.GivenColorFitsWhenSwitchingLumaWeightingThenWeightsFlowThrough` + `GpuStretchPipelineTests.GpuMatchesCpuForLumaWeightingProfiles`.
- [x] Sensor-derived luma weights (2026-05-11): `LumaWeighting.SensorMatched` resolves through `FilterCurveDatabase.TryComputeSensorLumaWeights(meta, ...)`, which integrates the doc's `BuildChannelThroughputs` (sensor QE x Sony CFA R/G/B) and normalises to sum to 1. Helper retries with `SensorType.RGGB` so debayered OSC images still resolve to the sensor-specific triple; gated on a recognised SensorModel so typos fall back to Rec.709 instead of silently returning CFA-only weights. Pure producer-side wire-up via `AstroImageDocument.ResolveLumaWeights`; no UBO / shader churn. Sample weights: IMX533 (0.29,0.36,0.34), IMX571 (0.35,0.37,0.28), IMX455 (0.30,0.37,0.34) -- broadband response (no photopic V(lambda) convolution, since the database doesn't ship it). Tests: `FilterCurveDatabaseTests.TryComputeSensorLumaWeights_*` + `StretchTests_NewPipeline.GivenOscMetaWhenLumaWeightingIsSensorMatched...` + `GpuStretchPipelineTests.GpuMatchesCpuForSensorMatchedLumaWeights`.

## Colour: Unified camera→sRGB matrix

The dcraw `adobe_coeff` 3×3 (now shipped via `FC.SDK.Raw.CanonCameraProfiles`) handles Canon CR2 sensible-default rendering. For OSC astro cameras (ZWO / QHY / etc.) and for Canon bodies whose spectral data is publicly available, we can derive the matrix from first principles; same QE × CFA spectral integration that `Tycho2ColorCalibration.ComputeSpectrophotometricWhiteBalance` already does for SPCC WB. Three pieces, in order:

Dispatch order on CR2 import: try spectral matrix first (best, first-principles); fall back to dcraw matrix (factory-curated); fall back to identity (warn). For non-Canon raws (NEF / ARW / etc.) only the spectral path applies until / unless a vendor-specific factory table lands too.

## Colour: narrowband

Everything above (and `Tycho2ColorCalibration`) assumes a **broadband** system: SPCC integrates a Pickles
SED against QE × CFA over the visible, which is exactly the wrong model for an Ha / OIII / SII stack. Today
a narrowband master has no colour-calibration path at all, so the palette is whatever the channel-assignment
and per-channel autostretch happen to produce.

Planned in **[docs/plans/narrowband-colour.md](../plans/narrowband-colour.md)** (researched 2026-08-02),
which carries the algorithms, a pros/cons table across the three candidate techniques, and four ADRs.
Summary of what was decided, so this file is not misleading on its own:

Two items added by the 2026-09-08 Slack sweep, both from the user working on an HOO master:

- [x] **Auto stretch resolved to Linked on a narrowband master, and that was where the cast came from**
  (FIXED 2026-09-08)
  (reported 2026-09-06: *"Auto mode for HOO SPCC looks wrong (it switches to Linked which gives it a
  strong colour cast), unlinked works better there"*). `StretchModeExtensions.ResolveAuto` is handed two
  facts and neither can tell an HOO master from an RGB one: whether the frame is colour, and whether a
  calibration is being applied (`AstroImageDocument`, `mode.ResolveAuto(isColour, autoWb is not null)`).
  A calibration exists on the document, so Auto picks Linked, which is what preserves a white balance as
  colour. On a broadband frame that is exactly the point. On HOO it preserves a fit whose premise, a
  Pickles SED integrated over a broad passband, never held; Unlinked neutralises each channel's own
  background and looks right, which is what the user found by hand.
  - The narrow fix is a third input: whether the frame's filters are narrowband. The FITS `FILTER` cards
    state it and `FilterCurveDatabase` can classify by passband width, so Auto would resolve colour plus
    narrowband to Unlinked whatever the calibration says.
  - The wider question is whether SPCC should have produced a triple at all, which is Phase 4 above and
    blocked on Gaia. A frame we cannot calibrate should probably not display as calibrated; settle that
    and the Auto rule falls out instead of being a special case.
  - Watch the mono boundary while changing it: `isColour` is true for a 3-plane HOO master, and a mono
    frame already resolves to Linked, where the two modes coincide.
  - **Shipped as a third input to `ResolveAuto`, measured from the throughputs SPCC itself integrates**
    (`FilterCurveDatabase.IsLineSelective(tsysR, tsysG, tsysB)`, recorded on the document as
    `IsNarrowbandColorCalibration` and read through the same `Basis` as the calibration, so a blink run
    cannot flicker between two stretch modes). Never from the filter's NAME: that would have to go back
    through the token matcher and could answer differently from what the fit used, and `Filter.Bandpass`
    is no help at all, being populated only for canonically-named filters while the headers this is about
    ("Ha 3nm", "L-eXtreme", "Antlia ALP-T") all canonicalise to `Unknown` with `Bandpass.None`.
  - **The 38 nm cut was measured over all 183 shipped curves, not chosen**, summing each curve's width
    above half peak: dual-band filters land at 3 to 8 nm (L-Ultimate, ALP-T), the tri-band and
    duo-narrowband family at 24 to 34 (IDAS NBZ, L-eNhance, Antlia Triband), then **nothing at all until
    42**, where UHC-style filters start and run straight into the broadband camera channels (Johnson U 53,
    Nikon R 50 to 60, Canon R 68 to 70). UHC and wider therefore count as broadband, deliberately: they
    overlap a genuine R channel in width, so no threshold separates them and calling a real R filter
    narrowband is the worse error. `OPTOLONG_L_QUAD_ENHANCE` measuring 206 nm is not a contradiction; a
    quad-band *enhance* passes the continuum between the sodium lines.
  - **That first cut did NOT fix the reported file, and opening it is what said so.**
    `Sag_Triplet_OIII-HOO_1.fits` is an Astro Pixel Processor composite carrying **no `FILTER`, no
    `INSTRUME` and no sensor card** (its filter lives in APP's own `FILT-1 = 'HOO 1 composite'`, which
    nothing reads), so `BuildChannelThroughputs` returns null, SPCC never runs, and the triple on it comes
    from the **sky-background** path (`ComputeColorCalibrationAsync`), which needs only stars. The
    throughput classifier had nothing to classify.
  - **The frame itself is the signal that works: it is RANK-DEFICIENT.** HOO puts the same OIII plane in
    green and blue, so three gains are being fitted to two measurements. Measured on that file: green and
    blue are identical in **100.0% of its 9,477,205 pixels**, max abs difference exactly 0. `Image`
    reports `IndependentChannelCount()` (bit-for-bit, exits at the first differing pixel, so an ordinary
    frame costs a few elements), the document measures it once at construction while the planes are still
    resident, and `ColourIsNotPhotometric` folds it together with the narrowband verdict. Auto now
    resolves **Unlinked** on the real file, confirmed end to end through `AstroImageDocument.OpenAsync`.
  - **Cost, measured on that 3073 x 3085 x 3 frame in Release:** the duplicate pair is the worst case and
    takes **3.8 ms**; a frame whose channels differ at the first pixel takes **0.002 ms**. Against a
    403 ms `OpenAsync` and the **12.95 ms** each of the three `Statistics()` passes the constructor
    already runs, that is 0.9% of a load, paid once per document and never per frame.
  - **`ResolveAuto`'s third parameter is named for the meaning, not the cause** (`colourIsNotPhotometric`):
    a line-selective passband and a duplicated channel are two ways of saying the colour is not a
    measurement of the sky.
  - **The equality test is bit-for-bit on purpose.** A nearly grey RGB frame still calibrates; only a
    plane that IS another plane counts, which is what a synthetic palette produces.
  - **The calibration is still computed and still shown**; what changed is only that Auto will not ASSERT
    it as colour. The wider question below stands.
  - Pinned by `NarrowbandStretchModeTests`, whose broadband control is what stops the fix degenerating
    into "Auto never picks Linked again"; both narrowband cases were seen red with the rule reverted.

